using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3202</c>–<c>OFR3204</c>: where binary-formatter data goes, and which types it carries.
/// <para>
/// A call is <b>transient</b> (the deep-clone idiom) when its stream is a <c>MemoryStream</c>
/// created in the same member, the member both serializes and deserializes through it, and the
/// stream is used for nothing else (repositioning and disposal aside). Everything else is
/// <b>persisted or transported</b>, with the evidence: a file stream, a stream parameter or
/// field, or a memory stream whose contents leave the member. Unknown flows count as persisted.
/// </para>
/// <para>
/// The types serialized are the static type of the serialized argument (followed to the
/// member's call sites in the compilation when it is an <c>object</c> parameter) and the cast
/// applied to <c>Deserialize</c>. They, their base types, and the types of their serialized
/// fields go into <see cref="AuditRunState.SerializedTypes"/> for <c>OFR3205</c>.
/// </para>
/// </summary>
public sealed class SerializationFlowMatcher : IAuditMatcher
{
    private static readonly HashSet<string> Formatters = new(StringComparer.Ordinal)
    {
        "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter",
        "System.Runtime.Serialization.Formatters.Soap.SoapFormatter",
        "System.Runtime.Serialization.NetDataContractSerializer",
        "System.Web.UI.ObjectStateFormatter",
        "System.Web.UI.LosFormatter",
        "System.Runtime.Serialization.IFormatter",
    };

    /// <summary>Members of a memory stream that keep its contents inside the member.</summary>
    private static readonly HashSet<string> LocalStreamUses = new(StringComparer.Ordinal) { "Position", "Seek", "Dispose", "Flush", "Close", "SetLength" };

    public string Name => "serialization-flow";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        var transient = context.Rule("OFR3202");
        var persisted = context.Rule("OFR3203");
        var typesRule = context.Rule("OFR3204");
        var calls = FormatterCalls(context).ToList();
        var types = new SortedDictionary<string, (ITypeSymbol Type, Location Site)>(StringComparer.Ordinal);
        var reportedTransient = new HashSet<SyntaxNode>();

        foreach (var (call, method, model) in calls)
        {
            var member = call.Ancestors().FirstOrDefault(IsMember);
            foreach (var type in SerializedTypes(context, call, method, model))
            {
                if (type.GetDocumentationCommentId() is { } id && !types.ContainsKey(id))
                {
                    types[id] = (type, Calls.At(call));
                }
            }

            var flow = Classify(call, method, model, member, calls);
            if (flow.Transient)
            {
                if (transient is not null && member is not null && reportedTransient.Add(member))
                {
                    yield return Calls.Finding(transient, call, method, $"{Owner(model, member)} round-trips through a MemoryStream (deep clone); the data never leaves the process.",
                        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["flow"] = "transient" });
                }
            }
            else if (persisted is not null)
            {
                yield return Calls.Finding(persisted, call, method, $"{method.ContainingType.Name}.{method.Name} {flow.Evidence}; the data outlives the process.",
                    new SortedDictionary<string, string>(StringComparer.Ordinal) { ["evidence"] = flow.Kind, ["flow"] = "persisted" });
            }
        }

        foreach (var (id, (type, _)) in types)
        {
            Close(type, context.Run.SerializedTypes);
        }

        if (typesRule is null)
        {
            yield break;
        }

        foreach (var (_, (type, site)) in types)
        {
            if (type.SpecialType == SpecialType.System_Object)
            {
                continue;
            }

            var details = Traits(type);
            var name = AuditEngine.Name(type);
            var notes = new List<string>();
            if (details.ContainsKey("iSerializable"))
            {
                notes.Add("implements ISerializable");
            }

            if (details.ContainsKey("onDeserialized"))
            {
                notes.Add("has deserialization callbacks");
            }

            if (details.TryGetValue("delegates", out var delegates))
            {
                notes.Add("holds delegates (" + delegates + ")");
            }

            var suffix = notes.Count == 0 ? "" : ": " + string.Join(", ", notes);
            yield return new RawFinding(typesRule, site, name, $"{name} is serialized with a binary formatter{suffix}.", details) { Namespace = AuditEngine.NamespaceOf(type) };
        }
    }

    private static IEnumerable<(ExpressionSyntax Call, IMethodSymbol Method, SemanticModel Model)> FormatterCalls(AuditMatchContext context) =>
        Calls.In(context).Where(c => c.Method.Name is "Serialize" or "Deserialize" && IsFormatter(c.Method.ContainingType));

    private static bool IsFormatter(INamedTypeSymbol? type) =>
        type is not null && (Formatters.Contains(type.OriginalDefinition.ToDisplayString())
            || type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Runtime.Serialization.IFormatter"));

    private static bool IsMember(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax;

    private static string Owner(SemanticModel model, SyntaxNode member) =>
        model.GetDeclaredSymbol(member) is { } symbol ? AuditEngine.Name(symbol) : "The member";

    private readonly record struct Flow(bool Transient, string Kind, string Evidence);

    private static Flow Classify(ExpressionSyntax call, IMethodSymbol method, SemanticModel model, SyntaxNode? member, List<(ExpressionSyntax Call, IMethodSymbol Method, SemanticModel Model)> calls)
    {
        var streamArgument = Calls.Arguments(call).Zip(method.Parameters)
            .FirstOrDefault(a => IsStream(a.Second.Type)).First?.Expression;
        if (streamArgument is null)
        {
            return new Flow(false, "value", method.Name == "Serialize" ? "returns the serialized data" : "reads serialized data from a value");
        }

        var source = Source(model, streamArgument);
        switch (source.Kind)
        {
            case "file":
                return new Flow(false, "file", "uses a file stream");
            case "parameter":
                return new Flow(false, "parameter", $"uses the stream parameter '{source.Name}'");
            case "field":
                return new Flow(false, "field", $"uses the stream member '{source.Name}'");
            case "memory" when source.Local is { } local && member is not null:
                var escape = Escape(model, member, local, calls);
                if (escape is not null)
                {
                    return new Flow(false, "memory-escapes", $"writes to a MemoryStream whose contents leave the member ({escape})");
                }

                var both = calls.Where(c => c.Call.Ancestors().Contains(member) && UsesLocal(c.Model, c.Call, c.Method, local)).Select(c => c.Method.Name).ToHashSet(StringComparer.Ordinal);
                return both.SetEquals(["Serialize", "Deserialize"])
                    ? new Flow(true, "transient", "")
                    : new Flow(false, "memory-escapes", method.Name == "Serialize" ? "writes to a MemoryStream that is never read back here" : "reads a MemoryStream filled elsewhere");
            case "memory-over-data":
                return new Flow(false, "memory-escapes", "reads a MemoryStream over data from elsewhere");
            default:
                return new Flow(false, "unknown", "uses a stream from elsewhere");
        }
    }

    private static bool IsStream(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.IO.Stream")
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct StreamSource(string Kind, string? Name, ILocalSymbol? Local);

    private static StreamSource Source(SemanticModel model, ExpressionSyntax expression)
    {
        expression = Unwrap(expression);
        switch (model.GetSymbolInfo(expression).Symbol)
        {
            case IParameterSymbol parameter:
                return new StreamSource("parameter", parameter.Name, null);
            case IFieldSymbol or IPropertySymbol:
                return new StreamSource("field", model.GetSymbolInfo(expression).Symbol!.Name, null);
            case ILocalSymbol local:
                var initializer = local.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Initializer?.Value;
                var creation = initializer is null ? new StreamSource("unknown", local.Name, null) : Source(model, initializer);
                return creation.Kind == "memory" ? creation with { Local = local, Name = local.Name } : creation;
        }

        if (expression is BaseObjectCreationExpressionSyntax && model.GetTypeInfo(expression).Type is { } created)
        {
            return created.ToDisplayString() switch
            {
                "System.IO.MemoryStream" => new StreamSource(IsEmptyMemoryStream(expression) ? "memory" : "memory-over-data", null, null),
                "System.IO.FileStream" => new StreamSource("file", null, null),
                _ => new StreamSource("unknown", null, null),
            };
        }

        if (expression is InvocationExpressionSyntax && model.GetSymbolInfo(expression).Symbol is IMethodSymbol factory
            && factory.ContainingType?.ToDisplayString() is "System.IO.File" or "System.IO.FileInfo")
        {
            return new StreamSource("file", null, null);
        }

        return new StreamSource("unknown", null, null);
    }

    /// <summary><c>new MemoryStream()</c> or with a capacity: a stream that starts empty.</summary>
    private static bool IsEmptyMemoryStream(ExpressionSyntax creation) =>
        creation is BaseObjectCreationExpressionSyntax { ArgumentList: null or { Arguments.Count: 0 } }
        || creation is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } }] };

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => Unwrap(parenthesized.Expression),
        CastExpressionSyntax cast => Unwrap(cast.Expression),
        _ => expression,
    };

    /// <summary>How a member-local memory stream's contents leave the member, or null when they do not.</summary>
    private static string? Escape(SemanticModel model, SyntaxNode member, ILocalSymbol local, List<(ExpressionSyntax Call, IMethodSymbol Method, SemanticModel Model)> calls)
    {
        foreach (var use in member.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == local.Name))
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(use).Symbol, local))
            {
                continue;
            }

            switch (use.Parent)
            {
                case MemberAccessExpressionSyntax access when access.Expression == use:
                    if (!LocalStreamUses.Contains(access.Name.Identifier.ValueText))
                    {
                        return access.Name.Identifier.ValueText;
                    }

                    continue;
                case ArgumentSyntax argument when argument.Parent?.Parent is ExpressionSyntax invocation
                    && calls.Any(c => c.Call == invocation):
                    continue;
                case ReturnStatementSyntax:
                    return "returned";
                case ArgumentSyntax:
                    return "passed on";
                case AssignmentExpressionSyntax assignment when assignment.Right == use:
                    return "assigned";
                case UsingStatementSyntax:
                    continue;
            }
        }

        return null;
    }

    private static bool UsesLocal(SemanticModel model, ExpressionSyntax call, IMethodSymbol method, ILocalSymbol local) =>
        Calls.Arguments(call).Zip(method.Parameters)
            .Where(a => IsStream(a.Second.Type))
            .Any(a => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(Unwrap(a.First.Expression)).Symbol, local));

    /// <summary>The types a formatter call carries: the serialized argument's, or the cast on the deserialized result.</summary>
    private static IEnumerable<ITypeSymbol> SerializedTypes(AuditMatchContext context, ExpressionSyntax call, IMethodSymbol method, SemanticModel model)
    {
        if (method.Name == "Deserialize")
        {
            var parent = call.Parent;
            while (parent is ParenthesizedExpressionSyntax)
            {
                parent = parent.Parent;
            }

            var target = parent switch
            {
                CastExpressionSyntax cast => model.GetTypeInfo(cast.Type).Type,
                BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } binary => model.GetTypeInfo(binary.Right).Type,
                _ => null,
            };
            if (target is not null and not ITypeParameterSymbol)
            {
                yield return target;
            }

            yield break;
        }

        var graph = Calls.Arguments(call).Zip(method.Parameters).FirstOrDefault(a => a.Second.Type.SpecialType == SpecialType.System_Object).First?.Expression;
        if (graph is null)
        {
            yield break;
        }

        foreach (var type in ArgumentTypes(context, model, graph, depth: 0))
        {
            yield return type;
        }
    }

    private static IEnumerable<ITypeSymbol> ArgumentTypes(AuditMatchContext context, SemanticModel model, ExpressionSyntax argument, int depth)
    {
        var type = model.GetTypeInfo(argument).Type;
        if (type is null)
        {
            yield break;
        }

        var parameter = model.GetSymbolInfo(Unwrap(argument)).Symbol as IParameterSymbol;
        var opaque = type.SpecialType == SpecialType.System_Object || type is ITypeParameterSymbol || type.TypeKind == TypeKind.Interface;
        if (!opaque || parameter is null || depth >= 3 || parameter.ContainingSymbol is not IMethodSymbol owner)
        {
            if (type is not ITypeParameterSymbol)
            {
                yield return type;
            }

            yield break;
        }

        // Follow the parameter to the call sites of its method in this compilation.
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var siteModel = context.Compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (AuditEngine.Bound(siteModel, invocation) is not IMethodSymbol target
                    || !SymbolEqualityComparer.Default.Equals((target.ReducedFrom ?? target).OriginalDefinition, owner.OriginalDefinition))
                {
                    continue;
                }

                var index = parameter.Ordinal - (target.ReducedFrom is null ? 0 : 1);
                if (index >= 0 && index < invocation.ArgumentList.Arguments.Count)
                {
                    foreach (var passed in ArgumentTypes(context, siteModel, invocation.ArgumentList.Arguments[index].Expression, depth + 1))
                    {
                        yield return passed;
                    }
                }
                else if (index == -1 && invocation.Expression is MemberAccessExpressionSyntax access)
                {
                    foreach (var passed in ArgumentTypes(context, siteModel, access.Expression, depth + 1))
                    {
                        yield return passed;
                    }
                }
            }
        }
    }

    /// <summary>A serialized type, its base types, and the types of its serialized fields, transitively.</summary>
    private static void Close(ITypeSymbol root, HashSet<string> serialized)
    {
        var queue = new Queue<ITypeSymbol>([root]);
        while (queue.TryDequeue(out var type))
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    queue.Enqueue(array.ElementType);
                    continue;
                case not INamedTypeSymbol:
                    continue;
            }

            var named = (INamedTypeSymbol)type;
            if (named.OriginalDefinition.GetDocumentationCommentId() is not { } id || !serialized.Add(id))
            {
                continue;
            }

            foreach (var argument in named.TypeArguments)
            {
                queue.Enqueue(argument);
            }

            if (named.BaseType is { } baseType)
            {
                queue.Enqueue(baseType);
            }

            foreach (var field in named.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst && !f.GetAttributes().Any(IsNonSerialized)))
            {
                queue.Enqueue(field.Type);
            }
        }
    }

    private static bool IsNonSerialized(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() == "System.NonSerializedAttribute";

    private static SortedDictionary<string, string> Traits(ITypeSymbol type)
    {
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Runtime.Serialization.ISerializable"))
        {
            details["iSerializable"] = "true";
        }

        var callbacks = type.GetMembers().OfType<IMethodSymbol>()
            .Any(m => m.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() is "System.Runtime.Serialization.OnDeserializedAttribute" or "System.Runtime.Serialization.OnDeserializingAttribute"))
            || type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Runtime.Serialization.IDeserializationCallback");
        if (callbacks)
        {
            details["onDeserialized"] = "true";
        }

        var delegates = type.GetMembers()
            .Where(m => !m.IsStatic && !m.IsImplicitlyDeclared)
            .Where(m => m switch
            {
                IFieldSymbol field => field.Type.TypeKind == TypeKind.Delegate && !field.GetAttributes().Any(IsNonSerialized),
                IEventSymbol => true,
                _ => false,
            })
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (delegates.Count > 0)
        {
            details["delegates"] = string.Join(", ", delegates);
        }

        return details;
    }
}

/// <summary>
/// <c>OFR3205</c>: <c>[Serializable]</c> types declared in the project that no binary formatter
/// in the audited solution receives (directly, as a base type, or through a serialized field).
/// Runs after <see cref="SerializationFlowMatcher"/> has seen every project.
/// </summary>
public sealed class SerializableUnusedMatcher : IAuditMatcher
{
    public string Name => "serializable-unused";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3205") is not { } rule)
        {
            yield break;
        }

        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol { IsSerializable: true } type
                    || type.TypeKind is TypeKind.Enum or TypeKind.Delegate
                    || type.GetDocumentationCommentId() is not { } id
                    || context.Run.SerializedTypes.Contains(id)
                    || type.DeclaringSyntaxReferences.First().GetSyntax() != declaration)
                {
                    continue;
                }

                var name = AuditEngine.Name(type);
                yield return new RawFinding(rule, declaration.Identifier.GetLocation(), name, $"{name} is [Serializable] but no binary formatter in the solution receives it.")
                {
                    Namespace = AuditEngine.NamespaceOf(type),
                };
            }
        }
    }
}
