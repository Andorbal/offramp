using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Rules;
using Offramp.Analysis.Serialization;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>A serialization API from <c>rules/newtonsoft-apis.yml</c>.</summary>
internal sealed record NewtonsoftApi(string Api, SiteDirection Direction, string Payload);

internal static class NewtonsoftApis
{
    private static readonly Lazy<IReadOnlyDictionary<string, NewtonsoftApi>> Loaded = new(() =>
        RuleFiles.Load("newtonsoft-apis.yml")["apis"]!.AsArray()
            .Select(a => new NewtonsoftApi(
                a!["api"]!.GetValue<string>(),
                a["direction"]!.GetValue<string>() switch
                {
                    "serialize" => SiteDirection.Serialize,
                    "deserialize" => SiteDirection.Deserialize,
                    "dom" => SiteDirection.Dom,
                    var other => throw new InvalidDataException($"Unknown direction '{other}' in newtonsoft-apis.yml."),
                },
                a["payload"]!.GetValue<string>()))
            .ToDictionary(a => a.Api, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, NewtonsoftApi> All => Loaded.Value;

    /// <summary>A method's documentation ID without parameters: what the rule files name.</summary>
    public static string Key(IMethodSymbol method) =>
        "M:" + method.OriginalDefinition.ContainingType.GetDocumentationCommentId()![2..] + "." + method.Name;

    public static bool InNewtonsoft(ISymbol? symbol) =>
        symbol?.ContainingAssembly?.Name == NewtonsoftReferences.PackageId;
}

/// <summary>What a site's settings create (the types, with their base types) and whether they set <c>MaxDepth</c>.</summary>
internal sealed record SettingsShape(IReadOnlySet<string> Types, bool SetsMaxDepth)
{
    public static readonly SettingsShape Empty = new(new HashSet<string>(StringComparer.Ordinal), false);

    public bool Creates(string type) => Types.Contains(type);
}

/// <summary>A site with the semantic facts the detectors need.</summary>
internal sealed record FoundSite(NewtonsoftSite Site, IInvocationOperation Call, IReadOnlyList<ITypeSymbol> Payloads, SettingsShape Shape)
{
    /// <summary>What the settings resolve to: the creation (inline) or the static member reference (member); null otherwise.</summary>
    public IOperation? Settings { get; init; }
}

/// <summary>Finds the sites and configuration of one project's compilation (<c>newtonsoft scan</c>, steps 2 and 3).</summary>
internal sealed class NewtonsoftSiteFinder(string repositoryRoot, Compilation compilation)
{
    private const string Settings = "Newtonsoft.Json.JsonSerializerSettings";
    private const string Serializer = "Newtonsoft.Json.JsonSerializer";

    public List<FoundSite> Sites { get; } = [];

    public List<NewtonsoftConfiguration> Configuration { get; } = [];

    /// <summary>The settings <c>JsonConvert.DefaultSettings</c> is given, when the project assigns it.</summary>
    public SettingsShape? DefaultSettings { get; private set; }

    public void Find()
    {
        var found = new List<(string File, int Position, FoundSite Site)>();
        foreach (var tree in AuditEngine.Sources(compilation))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var operation = node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or AssignmentExpressionSyntax
                    ? model.GetOperation(node)
                    : null;
                switch (operation)
                {
                    case IInvocationOperation invocation when node is InvocationExpressionSyntax:
                        if (Site(invocation, model) is { } site)
                        {
                            found.Add((site.Site.File, node.SpanStart, site));
                        }

                        ConfigurationFrom(invocation, model);
                        break;
                    case IObjectCreationOperation creation:
                        ConfigurationFrom(creation, model);
                        break;
                    case ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property: { Name: "DefaultSettings" } property } } assignment
                        when property.ContainingType.ToDisplayString() == "Newtonsoft.Json.JsonConvert":
                        DefaultSettings = Shape(assignment.Value);
                        Configure(ConfigurationKind.DefaultSettings, assignment.Syntax, model);
                        break;
                }

                if (node is BaseTypeDeclarationSyntax declaration && model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type)
                {
                    if (DeclaredKind(type) is { } kind)
                    {
                        var (file, line, _) = AuditEngine.Position(repositoryRoot, declaration.Identifier.GetLocation());
                        Configuration.Add(new NewtonsoftConfiguration(kind, file, line, type.GetDocumentationCommentId()!));
                    }
                }
            }
        }

        Number(found);
        Configuration.Sort((a, b) => (a.File, a.Line, a.Kind, a.Symbol).CompareTo((b.File, b.Line, b.Kind, b.Symbol)));
    }

    /// <summary>Gives each site its id: the type and member name, and its rank among the sites of members with that name.</summary>
    private void Number(List<(string File, int Position, FoundSite Site)> found)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, _, site) in found.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Position))
        {
            var stem = site.Site.Id;
            counts[stem] = counts.GetValueOrDefault(stem) + 1;
            Sites.Add(site with { Site = site.Site with { Id = $"{stem}#{counts[stem]}" } });
        }
    }

    private FoundSite? Site(IInvocationOperation invocation, SemanticModel model)
    {
        var method = invocation.TargetMethod;
        if (!NewtonsoftApis.InNewtonsoft(method) || !NewtonsoftApis.All.TryGetValue(NewtonsoftApis.Key(method), out var api))
        {
            return null;
        }

        var member = Member(model, invocation.Syntax);
        var (file, line, _) = AuditEngine.Position(repositoryRoot, invocation.Syntax.GetLocation());
        var payloads = Payloads(api, invocation, model);
        var (settings, shape, source) = SettingsOf(invocation, model);
        return new FoundSite(
            new NewtonsoftSite
            {
                Id = AuditEngine.Name(member.ContainingType) + "." + member.Name,
                File = file,
                Line = line,
                Member = member.GetDocumentationCommentId() ?? AuditEngine.Name(member),
                Api = method.OriginalDefinition.GetDocumentationCommentId()!,
                Direction = api.Direction,
                PayloadTypes = [.. payloads.Select(TypeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                Settings = settings,
            },
            invocation,
            payloads,
            shape)
        {
            Settings = settings.Kind is SettingsKind.Inline or SettingsKind.Member ? source : null,
        };
    }

    /// <summary>The member a node belongs to, as the call graph names it.</summary>
    internal static ISymbol Member(SemanticModel model, SyntaxNode node) => Offramp.Analysis.CallGraph.CallGraph.Member(model, node);

    internal static string TypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.None));

    private List<ITypeSymbol> Payloads(NewtonsoftApi api, IInvocationOperation invocation, SemanticModel model)
    {
        IEnumerable<ITypeSymbol> types = api.Payload switch
        {
            "none" => [],
            "type" => TypeArgument(invocation),
            "anonymous" => Argument(invocation, "anonymousTypeObject") is { } anonymous ? [anonymous.Type!] : [],
            var argument when argument.StartsWith("argument:", StringComparison.Ordinal) =>
                Argument(invocation, argument["argument:".Length..]) is { Syntax: ExpressionSyntax expression }
                    ? PayloadTypes.Of(compilation, model, expression)
                    : [],
            var other => throw new InvalidDataException($"Unknown payload '{other}' in newtonsoft-apis.yml."),
        };
        return [.. types.Where(t => t is not null && !PayloadTypes.IsOpaque(t)).Distinct(SymbolEqualityComparer.Default).Cast<ITypeSymbol>()];
    }

    private static IOperation? Argument(IInvocationOperation invocation, string parameter) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == parameter)?.Value is { } value ? Unconverted(value) : null;

    /// <summary>The type argument, a <c>typeof</c> passed for the <c>Type</c> parameter, or the cast applied to the result.</summary>
    private static IEnumerable<ITypeSymbol> TypeArgument(IInvocationOperation invocation)
    {
        var method = invocation.TargetMethod;
        if (method.IsGenericMethod)
        {
            return [method.TypeArguments[0]];
        }

        if (invocation.Arguments.FirstOrDefault(a => a.Parameter?.Type.ToDisplayString() == "System.Type")?.Value is { } type)
        {
            return Unconverted(type) is ITypeOfOperation typeOf ? [typeOf.TypeOperand] : [];
        }

        var parent = invocation.Parent;
        while (parent is IParenthesizedOperation)
        {
            parent = parent.Parent;
        }

        return parent is IConversionOperation { IsImplicit: false } cast ? [cast.Type!] : [];
    }

    /// <summary>The settings a call uses: its settings, serializer, or converters argument, or the serializer it is called on.</summary>
    private (SiteSettings, SettingsShape, IOperation?) SettingsOf(IInvocationOperation invocation, SemanticModel model)
    {
        var source = invocation.Arguments
            .Where(a => a.Parameter is { } p && (IsType(p.Type, Settings) || IsType(p.Type, Serializer)
                || p.Type is IArrayTypeSymbol { ElementType: var element } && IsType(element, "Newtonsoft.Json.JsonConverter")))
            .Select(a => Unconverted(a.Value))
            .FirstOrDefault(v => v is not IArrayCreationOperation { Initializer: null or { ElementValues.Length: 0 } } and not ILiteralOperation { ConstantValue.Value: null });
        if (source is null && invocation.Instance is { } instance && IsType(invocation.TargetMethod.ContainingType, Serializer))
        {
            source = Unconverted(instance);
        }

        return Classify(source, model);
    }

    /// <summary>The settings' provenance, their shape, and the operation they resolve to.</summary>
    private (SiteSettings, SettingsShape, IOperation?) Classify(IOperation? operation, SemanticModel model)
    {
        switch (operation)
        {
            case null or ILiteralOperation { ConstantValue.Value: null } or IDefaultValueOperation:
                return (new SiteSettings { Kind = SettingsKind.None }, SettingsShape.Empty, null);
            case IInvocationOperation { TargetMethod: { Name: "Create" or "CreateDefault" } create } call when IsType(create.ContainingType, Serializer):
                return call.Arguments.Length == 0
                    ? (new SiteSettings { Kind = SettingsKind.None }, SettingsShape.Empty, null)
                    : Classify(Unconverted(call.Arguments[0].Value), model);
            case IObjectCreationOperation or IArrayCreationOperation:
                return (Located(Reproducible(operation) ? SettingsKind.Inline : SettingsKind.Unknown, null, operation.Syntax), Shape(operation), operation);
            case IFieldReferenceOperation { Field: { IsStatic: true } field }:
                return (Located(SettingsKind.Member, field, Declaration(field)), Shape(Initializer(field)), operation);
            case IPropertyReferenceOperation { Property: { IsStatic: true } property, Arguments.Length: 0 }:
                return (Located(SettingsKind.Member, property, Declaration(property)), Shape(Initializer(property)), operation);
            case ILocalReferenceOperation { Local: var local } when AssignedOnce(local, operation, model) is { } initial:
                return Classify(Unconverted(initial), model);
            default:
                return (Located(SettingsKind.Unknown, null, operation.Syntax), SettingsShape.Empty, null);
        }
    }

    private SiteSettings Located(SettingsKind kind, ISymbol? symbol, SyntaxNode? where)
    {
        if (where is null || where.SyntaxTree.FilePath.Length == 0)
        {
            return new SiteSettings { Kind = kind, Symbol = symbol?.GetDocumentationCommentId() };
        }

        var (file, line, _) = AuditEngine.Position(repositoryRoot, where.GetLocation());
        return new SiteSettings { Kind = kind, Symbol = symbol?.GetDocumentationCommentId(), File = file, Line = line };
    }

    private static SyntaxNode? Declaration(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

    /// <summary>A static field's initializer, or a static property's initializer, expression body, or single returned value.</summary>
    private IOperation? Initializer(ISymbol member)
    {
        var expression = Declaration(member) switch
        {
            VariableDeclaratorSyntax { Initializer.Value: var value } => value,
            PropertyDeclarationSyntax { Initializer.Value: var value } => value,
            PropertyDeclarationSyntax { ExpressionBody.Expression: var value } => value,
            PropertyDeclarationSyntax { AccessorList: { } accessors } => accessors.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) switch
            {
                { ExpressionBody.Expression: var value } => value,
                { Body.Statements: [ReturnStatementSyntax { Expression: { } value }] } => value,
                _ => null,
            },
            _ => null,
        };
        return expression is null || compilation.GetSemanticModel(expression.SyntaxTree).GetOperation(expression) is not { } operation ? null : Unconverted(operation);
    }

    /// <summary>A local's initializer when nothing else assigns it in the member.</summary>
    private static IOperation? AssignedOnce(ILocalSymbol local, IOperation reference, SemanticModel model)
    {
        if (local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var initial } declarator)
        {
            return null;
        }

        var body = declarator.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) ?? declarator.SyntaxTree.GetRoot();
        var reassigned = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(a => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, local))
            || body.DescendantNodes().OfType<ArgumentSyntax>()
                .Any(a => !a.RefKindKeyword.IsKind(SyntaxKind.None) && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Expression).Symbol, local));
        return reassigned ? null : model.GetOperation(initial);
    }

    /// <summary>The types the settings create (with their base types), and whether they set MaxDepth.</summary>
    private static SettingsShape Shape(IOperation? operation)
    {
        if (operation is null)
        {
            return SettingsShape.Empty;
        }

        var types = new HashSet<string>(StringComparer.Ordinal);
        var maxDepth = false;
        foreach (var node in operation.DescendantsAndSelf())
        {
            if (node is IObjectCreationOperation { Type: { } created })
            {
                for (var type = created; type is not null; type = type.BaseType)
                {
                    types.Add(type.ToDisplayString());
                }
            }
            else if (node is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Property.Name: "MaxDepth" } })
            {
                maxDepth = true;
            }
        }

        return new SettingsShape(types, maxDepth);
    }

    /// <summary>Whether a test could write the expression down: constants, enum members, typeof, public statics, and creations of public types from those.</summary>
    internal static bool Reproducible(IOperation operation) => operation switch
    {
        ILiteralOperation or ITypeOfOperation or IDefaultValueOperation => true,
        IConversionOperation conversion => Reproducible(conversion.Operand),
        IParenthesizedOperation parenthesized => Reproducible(parenthesized.Operand),
        IUnaryOperation unary => Reproducible(unary.Operand),
        IBinaryOperation binary => Reproducible(binary.LeftOperand) && Reproducible(binary.RightOperand),
        IFieldReferenceOperation { Field: var field } => field.IsStatic && (field.IsConst || field.ContainingType.TypeKind == TypeKind.Enum || field.IsReadOnly && IsPublic(field)),
        IPropertyReferenceOperation { Property: var property, Arguments.Length: 0, Instance: null } => property.IsStatic && IsPublic(property),
        IObjectCreationOperation creation => creation.Constructor is { } constructor && IsPublic(constructor)
            && creation.Arguments.All(a => Reproducible(a.Value))
            && (creation.Initializer is null || Reproducible(creation.Initializer)),
        IObjectOrCollectionInitializerOperation initializer => initializer.Initializers.All(Reproducible),
        IMemberInitializerOperation member => Reproducible(member.Initializer),
        ISimpleAssignmentOperation assignment => assignment.Target is IPropertyReferenceOperation or IFieldReferenceOperation && Reproducible(assignment.Value),
        IInvocationOperation { Instance: IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ImplicitReceiver } } add => add.Arguments.All(a => Reproducible(a.Value)),
        IArrayCreationOperation array => array.DimensionSizes.All(Reproducible) && (array.Initializer is null || array.Initializer.ElementValues.All(Reproducible)),
        _ => false,
    };

    private static bool IsPublic(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private void ConfigurationFrom(IOperation operation, SemanticModel model)
    {
        switch (operation)
        {
            case IObjectCreationOperation { Type: { } type } when IsType(type, Settings):
                Configure(ConfigurationKind.Settings, operation.Syntax, model);
                break;
            case IObjectCreationOperation { Type: { } type } when IsType(type, Serializer):
            case IInvocationOperation { TargetMethod: { Name: "Create" or "CreateDefault", IsStatic: true } method } when IsType(method.ContainingType, Serializer):
                Configure(ConfigurationKind.Serializer, operation.Syntax, model);
                break;
        }
    }

    private void Configure(ConfigurationKind kind, SyntaxNode node, SemanticModel model)
    {
        var (file, line, _) = AuditEngine.Position(repositoryRoot, node.GetLocation());
        var member = Member(model, node);
        Configuration.Add(new NewtonsoftConfiguration(kind, file, line, member.GetDocumentationCommentId() ?? AuditEngine.Name(member)));
    }

    private static ConfigurationKind? DeclaredKind(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            switch (baseType.ToDisplayString())
            {
                case "Newtonsoft.Json.JsonConverter":
                    return ConfigurationKind.Converter;
                case "Newtonsoft.Json.Serialization.NamingStrategy":
                    return ConfigurationKind.NamingStrategy;
            }
        }

        return type.AllInterfaces.Select(i => i.ToDisplayString()).FirstOrDefault(i => i is "Newtonsoft.Json.Serialization.IContractResolver" or "Newtonsoft.Json.Serialization.ISerializationBinder") switch
        {
            "Newtonsoft.Json.Serialization.IContractResolver" => ConfigurationKind.ContractResolver,
            "Newtonsoft.Json.Serialization.ISerializationBinder" => ConfigurationKind.Binder,
            _ => null,
        };
    }

    private static bool IsType(ITypeSymbol? type, string name) => type?.OriginalDefinition.ToDisplayString() == name;

    private static IOperation Unconverted(IOperation operation) => operation switch
    {
        IConversionOperation { IsImplicit: true } conversion => Unconverted(conversion.Operand),
        IParenthesizedOperation parenthesized => Unconverted(parenthesized.Operand),
        _ => operation,
    };
}
