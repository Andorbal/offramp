using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Seams;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Extract;

public sealed record ExtractInterfaceRequest
{
    public required string RepositoryRoot { get; init; }

    public required ProjectInfo Project { get; init; }

    /// <summary>The concrete type, fully qualified.</summary>
    public required string Type { get; init; }

    /// <summary>The interface name (default: <c>I</c> + the type's name).</summary>
    public string? Name { get; init; }

    /// <summary>Member names to extract; empty for every public instance member.</summary>
    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>Member signatures from a seam (<c>seams --out seams.json</c>), matched to the type's members.</summary>
    public IReadOnlyList<string> SeamMembers { get; init; } = [];

    /// <summary>The types whose dependency on the concrete type is rewritten; empty for every type in the project that references it.</summary>
    public IReadOnlyList<string> Callers { get; init; } = [];

    /// <summary><c>microsoft</c>, <c>autofac</c>, or <c>none</c>: the registration snippet to print.</summary>
    public string Di { get; init; } = "microsoft";

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>A caller whose dependency now points at the interface.</summary>
public sealed record RewrittenCaller(string Type, string File, IReadOnlyList<string> Changes);

/// <summary>A caller that creates the concrete type itself (OFR4010): its dependency needs injection first.</summary>
public sealed record DirectInstantiation(string Type, string File, int Line);

/// <summary>The <c>result</c> of <c>offramp extract interface</c> (<c>schemas/v1/extract-interface.json</c>).</summary>
public sealed record ExtractInterfaceResult
{
    public required string Project { get; init; }

    public required string Type { get; init; }

    public required string Interface { get; init; }

    /// <summary>The new interface file, repository-relative.</summary>
    public required string File { get; init; }

    public required IReadOnlyList<string> Members { get; init; }

    public required IReadOnlyList<RewrittenCaller> Callers { get; init; }

    public required IReadOnlyList<DirectInstantiation> DirectInstantiations { get; init; }

    /// <summary>The container registration to add, or null for <c>--di none</c>.</summary>
    public string? Registration { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record ExtractInterfacePlan(ExtractInterfaceResult Result, ChangeSet ChangeSet);

/// <summary>
/// <c>extract interface</c>: writes <c>INAME.cs</c> next to the type with the selected members
/// (fully qualified, so it needs no usings), adds the interface to the type's base list, and
/// retypes the callers' constructor parameters, fields, and properties of the concrete type to the
/// interface when every member they use is on it. Callers that create the concrete type with
/// <c>new</c> keep doing so and are reported (OFR4010). The edited project is compiled before
/// anything is written: new errors refuse the extraction (OFR4012).
/// </summary>
public static class InterfaceExtractor
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.CSharpErrorMessageFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.CSharpErrorMessageFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static ExtractInterfacePlan? Plan(ExtractInterfaceRequest request, Compilation compilation)
    {
        var type = compilation.GetTypeByMetadataName(request.Type) ?? FindByDisplayName(compilation, request.Type);
        var declaration = type?.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<TypeDeclarationSyntax>().OrderBy(d => d.SyntaxTree.FilePath, StringComparer.Ordinal).FirstOrDefault();
        if (type is null || declaration is null || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4011, $"'{request.Type}' is not a class or struct declared in {request.Project.Id}.", new DiagnosticLocation(request.Project.Id));
            return null;
        }

        var name = request.Name ?? "I" + type.Name;
        var members = Members(request, type);
        var root = request.RepositoryRoot;
        var typeFile = FileOf(root, declaration.SyntaxTree);
        var interfaceFile = (typeFile.Contains('/', StringComparison.Ordinal) ? typeFile[..typeFile.LastIndexOf('/')] + "/" : "") + name + ".cs";
        var newLine = declaration.SyntaxTree.GetText().ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var interfaceText = InterfaceText(type, name, members, newLine);

        // Text edits per tree, applied from the end so earlier positions stay valid.
        var edits = new Dictionary<SyntaxTree, List<(int Position, int Length, string Text)>>();
        void Edit(SyntaxTree tree, int position, int length, string text) =>
            (edits.TryGetValue(tree, out var list) ? list : edits[tree] = []).Add((position, length, text));

        var qualifiedInterface = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
        AddToBaseList(declaration, qualifiedInterface == type.ContainingNamespace?.ToDisplayString() + "." + name ? name : qualifiedInterface, Edit);

        var memberSet = members.Select(m => m.OriginalDefinition).ToHashSet(SymbolEqualityComparer.Default);
        var callers = new List<RewrittenCaller>();
        var instantiations = new List<DirectInstantiation>();
        foreach (var tree in AuditEngine.Sources(compilation))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var callerDeclaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(callerDeclaration) is not INamedTypeSymbol caller || SymbolEqualityComparer.Default.Equals(caller, type)
                    || (request.Callers.Count > 0 && !request.Callers.Contains(AuditEngine.Name(caller), StringComparer.Ordinal)))
                {
                    continue;
                }

                var changes = RewriteCaller(callerDeclaration, model, type, qualifiedInterface, memberSet, Edit);
                if (changes.Count > 0)
                {
                    callers.Add(new RewrittenCaller(AuditEngine.Name(caller), FileOf(root, tree), changes));
                }

                foreach (var creation in callerDeclaration.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                {
                    if (model.GetTypeInfo(creation).Type is { } created && SymbolEqualityComparer.Default.Equals(created.OriginalDefinition, type))
                    {
                        var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        instantiations.Add(new DirectInstantiation(AuditEngine.Name(caller), FileOf(root, tree), line));
                        request.Diagnostics.Report(DiagnosticCatalog.OFR4010,
                            $"{AuditEngine.Name(caller)} creates {type.Name} with new; inject {name} instead so the implementation can change.",
                            new DiagnosticLocation(request.Project.Id, FileOf(root, tree), line));
                    }
                }
            }
        }

        // Apply the edits and prove the project still compiles.
        var changeSet = new ChangeSet();
        var trial = compilation;
        foreach (var (tree, list) in edits.OrderBy(e => e.Key.FilePath, StringComparer.Ordinal))
        {
            var text = tree.GetText();
            var changed = text.WithChanges(list.OrderBy(e => e.Position).Select(e => new TextChange(new TextSpan(e.Position, e.Length), e.Text)));
            var file = FileOf(root, tree);
            var path = RepoPaths.ToAbsolute(root, file);
            var before = File.ReadAllBytes(path);
            if (!text.ContentEquals(SourceText.From(new UTF8Encoding(false).GetString(StripBom(before)))))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR4012, $"{file} changed since the last scan; run `offramp scan` and extract again.", new DiagnosticLocation(request.Project.Id, file));
                return null;
            }

            changeSet.Edit(file, before, Encode(before, changed.ToString()));
            trial = trial.ReplaceSyntaxTree(tree, tree.WithChangedText(changed));
        }

        if (File.Exists(RepoPaths.ToAbsolute(root, interfaceFile)))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4012, $"{interfaceFile} already exists; choose another name with --name.", new DiagnosticLocation(request.Project.Id, interfaceFile));
            return null;
        }

        changeSet.Create(interfaceFile, interfaceText);
        trial = trial.AddSyntaxTrees(CSharpSyntaxTree.ParseText(interfaceText, (CSharpParseOptions)declaration.SyntaxTree.Options, RepoPaths.ToAbsolute(root, interfaceFile)));
        var existing = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Key).ToHashSet(StringComparer.Ordinal);
        var added = trial.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !existing.Contains(Key(d)))
            .OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start).ToList();
        if (added.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4012,
                $"Extracting {name} would not compile: {string.Join("; ", added.Take(3).Select(d => $"{d.Id} {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}"))}",
                new DiagnosticLocation(request.Project.Id),
                [KeyValuePair.Create<string, JsonNode?>("errors", added.Count)]);
            return null;
        }

        var result = new ExtractInterfaceResult
        {
            Project = request.Project.Id,
            Type = AuditEngine.Name(type),
            Interface = qualifiedInterface,
            File = interfaceFile,
            Members = [.. members.Select(SeamsAnalyzer.Signature)],
            Callers = [.. callers.OrderBy(c => c.Type, StringComparer.Ordinal)],
            DirectInstantiations = [.. instantiations.OrderBy(i => i.File, StringComparer.Ordinal).ThenBy(i => i.Line)],
            Registration = request.Di switch
            {
                "microsoft" => $"services.AddSingleton<{qualifiedInterface}, {AuditEngine.Name(type)}>();",
                "autofac" => $"builder.RegisterType<{AuditEngine.Name(type)}>().As<{qualifiedInterface}>();",
                _ => null,
            },
            Preview = changeSet.Preview(),
        };
        return new ExtractInterfacePlan(result, changeSet);
    }

    private static string Key(Microsoft.CodeAnalysis.Diagnostic diagnostic) => diagnostic.Id + " " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    private static INamedTypeSymbol? FindByDisplayName(Compilation compilation, string name) =>
        compilation.GetSymbolsWithName(n => name.EndsWith(n, StringComparison.Ordinal), SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(t => AuditEngine.Name(t) == name);

    /// <summary>The members to put on the interface, in declaration order.</summary>
    private static List<ISymbol> Members(ExtractInterfaceRequest request, INamedTypeSymbol type)
    {
        var candidates = type.GetMembers()
            .Where(m => !m.IsStatic && !m.IsImplicitlyDeclared && m.DeclaredAccessibility == Accessibility.Public)
            .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol)
            .OrderBy(m => m.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
            .ToList();
        if (request.SeamMembers.Count > 0)
        {
            foreach (var signature in request.SeamMembers.Where(s => s.StartsWith("static ", StringComparison.Ordinal)))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR4003, $"{signature} stays on {type.Name}: an interface cannot declare a static member used this way.", new DiagnosticLocation(request.Project.Id));
            }

            return [.. candidates.Where(m => request.SeamMembers.Contains(SeamsAnalyzer.Signature(m), StringComparer.Ordinal))];
        }

        return request.Members.Count > 0 ? [.. candidates.Where(m => request.Members.Contains(m.Name, StringComparer.Ordinal))] : candidates;
    }

    private static string InterfaceText(INamedTypeSymbol type, string name, List<ISymbol> members, string newLine)
    {
        var b = new StringBuilder();
        var hasNamespace = type.ContainingNamespace is { IsGlobalNamespace: false };
        var indent = hasNamespace ? "    " : "";
        if (hasNamespace)
        {
            b.Append("namespace ").Append(type.ContainingNamespace!.ToDisplayString()).Append(newLine).Append('{').Append(newLine);
        }

        b.Append(indent).Append("/// <summary>The members of <see cref=\"").Append(type.Name).Append("\"/> its callers use (offramp extract interface).</summary>").Append(newLine);
        b.Append(indent).Append("public interface ").Append(name).Append(newLine).Append(indent).Append('{').Append(newLine);
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                b.Append(newLine);
            }

            b.Append(indent).Append("    ").Append(Declaration(members[i])).Append(newLine);
        }

        b.Append(indent).Append('}').Append(newLine);
        if (hasNamespace)
        {
            b.Append('}').Append(newLine);
        }

        return b.ToString();
    }

    private static string Declaration(ISymbol member) => member switch
    {
        IMethodSymbol method => $"{Type(method.ReturnType)} {method.Name}{TypeParameters(method)}({string.Join(", ", method.Parameters.Select(Parameter))}){Constraints(method)};",
        IPropertySymbol { IsIndexer: true } indexer => $"{Type(indexer.Type)} this[{string.Join(", ", indexer.Parameters.Select(Parameter))}] {{ {Accessors(indexer)}}}",
        IPropertySymbol property => $"{Type(property.Type)} {property.Name} {{ {Accessors(property)}}}",
        IEventSymbol @event => $"event {Type(@event.Type)} {@event.Name};",
        _ => "",
    };

    private static string Accessors(IPropertySymbol property) =>
        (property.GetMethod is { DeclaredAccessibility: Accessibility.Public } ? "get; " : "") + (property.SetMethod is { DeclaredAccessibility: Accessibility.Public } ? "set; " : "");

    private static string Type(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string TypeParameters(IMethodSymbol method) =>
        method.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">";

    private static string Constraints(IMethodSymbol method)
    {
        var clauses = new List<string>();
        foreach (var parameter in method.TypeParameters)
        {
            var parts = new List<string>();
            if (parameter.HasReferenceTypeConstraint)
            {
                parts.Add("class");
            }

            if (parameter.HasValueTypeConstraint)
            {
                parts.Add("struct");
            }

            parts.AddRange(parameter.ConstraintTypes.Select(Type));
            if (parameter.HasConstructorConstraint)
            {
                parts.Add("new()");
            }

            if (parts.Count > 0)
            {
                clauses.Add($" where {parameter.Name} : {string.Join(", ", parts)}");
            }
        }

        return string.Concat(clauses);
    }

    private static string Parameter(IParameterSymbol parameter)
    {
        var modifier = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : "",
        };
        var value = parameter.HasExplicitDefaultValue ? " = " + DefaultValue(parameter) : "";
        return $"{modifier}{Type(parameter.Type)} {parameter.Name}{value}";
    }

    private static string DefaultValue(IParameterSymbol parameter) => parameter.ExplicitDefaultValue switch
    {
        null => parameter.Type.IsValueType && parameter.Type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T ? "default" : "null",
        string text => SymbolDisplay.FormatLiteral(text, quote: true),
        bool flag => flag ? "true" : "false",
        char c => SymbolDisplay.FormatLiteral(c, quote: true),
        var other when parameter.Type.TypeKind == TypeKind.Enum => $"({Type(parameter.Type)}){Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)}",
        IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture) + Suffix(parameter.Type),
        var other => other.ToString() ?? "default",
    };

    private static string Suffix(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Single => "f",
        SpecialType.System_Decimal => "m",
        SpecialType.System_Int64 => "L",
        SpecialType.System_UInt32 => "u",
        SpecialType.System_UInt64 => "ul",
        _ => "",
    };

    /// <summary>Adds the interface to the type's base list, as text: <c> : IName</c> after the name, or <c>, IName</c> at the end of the list.</summary>
    private static void AddToBaseList(TypeDeclarationSyntax declaration, string name, Action<SyntaxTree, int, int, string> edit)
    {
        if (declaration.BaseList is { } baseList)
        {
            edit(declaration.SyntaxTree, baseList.Types.Last().Span.End, 0, ", " + name);
        }
        else
        {
            var after = declaration.TypeParameterList?.Span.End ?? declaration.Identifier.Span.End;
            edit(declaration.SyntaxTree, after, 0, " : " + name);
        }
    }

    /// <summary>
    /// Retypes a caller's constructor parameters, fields, and properties of the concrete type to the
    /// interface, when everything the caller does through them is on the interface.
    /// </summary>
    private static List<string> RewriteCaller(
        TypeDeclarationSyntax caller, SemanticModel model, INamedTypeSymbol type, string interfaceName, HashSet<ISymbol> members, Action<SyntaxTree, int, int, string> edit)
    {
        var changes = new List<string>();
        var candidates = new List<(TypeSyntax Syntax, ISymbol Symbol, string What)>();
        foreach (var node in caller.DescendantNodes())
        {
            switch (node)
            {
                case ParameterSyntax { Parent.Parent: ConstructorDeclarationSyntax } parameter when parameter.Type is { } parameterType && Is(model, parameterType, type):
                    if (model.GetDeclaredSymbol(parameter) is { } parameterSymbol)
                    {
                        candidates.Add((parameterType, parameterSymbol, $"constructor parameter '{parameter.Identifier.ValueText}'"));
                    }

                    break;
                case FieldDeclarationSyntax field when Is(model, field.Declaration.Type, type) && field.Declaration.Variables.Count == 1:
                    if (model.GetDeclaredSymbol(field.Declaration.Variables[0]) is { } fieldSymbol)
                    {
                        candidates.Add((field.Declaration.Type, fieldSymbol, $"field '{field.Declaration.Variables[0].Identifier.ValueText}'"));
                    }

                    break;
                case PropertyDeclarationSyntax property when Is(model, property.Type, type):
                    if (model.GetDeclaredSymbol(property) is { } propertySymbol)
                    {
                        candidates.Add((property.Type, propertySymbol, $"property '{property.Identifier.ValueText}'"));
                    }

                    break;
            }
        }

        // A parameter stored in a field is retyped only when the field is: drop candidates until stable.
        var retyped = candidates.Select(c => c.Symbol).ToHashSet(SymbolEqualityComparer.Default);
        while (candidates.FirstOrDefault(c => retyped.Contains(c.Symbol) && !OnlyInterfaceUses(caller, model, c.Symbol, members, retyped)) is { Symbol: not null } failing)
        {
            retyped.Remove(failing.Symbol);
        }

        foreach (var (syntax, symbol, what) in candidates.Where(c => retyped.Contains(c.Symbol)))
        {
            var (span, text) = Replacement(syntax, type, interfaceName);
            edit(syntax.SyntaxTree, span.Start, span.Length, text);
            changes.Add($"{what}: {type.Name} → {text}");
        }

        return changes;
    }

    /// <summary>
    /// Writes the interface the way the caller wrote the type: where it wrote <c>DirectoryLookup</c> the
    /// namespace is in scope, so <c>IDirectoryLookup</c> resolves too; qualified names keep their
    /// qualifier. Anything else (aliases, generics) gets the fully qualified name.
    /// </summary>
    private static (TextSpan Span, string Text) Replacement(TypeSyntax syntax, INamedTypeSymbol type, string qualifiedInterface)
    {
        var simple = qualifiedInterface[(qualifiedInterface.LastIndexOf('.') + 1)..];
        return syntax switch
        {
            IdentifierNameSyntax identifier when identifier.Identifier.ValueText == type.Name => (identifier.Span, simple),
            QualifiedNameSyntax { Right: IdentifierNameSyntax right } when right.Identifier.ValueText == type.Name => (right.Span, simple),
            AliasQualifiedNameSyntax { Name: IdentifierNameSyntax name } when name.Identifier.ValueText == type.Name => (name.Span, simple),
            _ => (syntax.Span, qualifiedInterface),
        };
    }

    private static bool Is(SemanticModel model, TypeSyntax syntax, INamedTypeSymbol type) =>
        model.GetTypeInfo(syntax).Type is { } found && SymbolEqualityComparer.Default.Equals(found.OriginalDefinition, type);

    /// <summary>Every member access through the symbol is on the interface; it is not passed on as the concrete type.</summary>
    private static bool OnlyInterfaceUses(TypeDeclarationSyntax caller, SemanticModel model, ISymbol symbol, HashSet<ISymbol> members, HashSet<ISymbol> retyped)
    {
        foreach (var use in caller.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == symbol.Name))
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(use).Symbol, symbol))
            {
                continue;
            }

            var parent = use.Parent is MemberAccessExpressionSyntax { Name: var n } qualified && n == use ? qualified.Parent : use.Parent;
            switch (parent)
            {
                case MemberAccessExpressionSyntax access when access.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
                    if (model.GetSymbolInfo(access).Symbol is not { } member || !members.Contains(member.OriginalDefinition))
                    {
                        return false;
                    }

                    break;
                case AssignmentExpressionSyntax assignment when assignment.Left.Span.Contains(use.Span):
                    break; // assigning the field or property from the retyped parameter
                case AssignmentExpressionSyntax assignment when assignment.Right.Span.Contains(use.Span):
                    // The parameter flows into a field or property that is retyped too.
                    if (model.GetSymbolInfo(assignment.Left).Symbol is not { } target || !retyped.Contains(target))
                    {
                        return false;
                    }

                    break;
                case ArgumentSyntax or EqualsValueClauseSyntax or ReturnStatementSyntax:
                    return false;
            }
        }

        return true;
    }

    private static string FileOf(string root, SyntaxTree tree) =>
        Path.IsPathRooted(tree.FilePath) ? RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath)) : tree.FilePath.Replace('\\', '/');

    private static byte[] StripBom(byte[] bytes) => bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;

    private static byte[] Encode(byte[] original, string text)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        return original.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }
}
