using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Symbols;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.CallGraph;

/// <summary>One use of a member by another: a call, a creation, or a property or field access.</summary>
internal sealed record CallSite(string Caller, string Callee, ISymbol CalleeSymbol, SyntaxNode Node, SemanticModel Model, ProjectInfo Project);

/// <summary>
/// Which member uses which, across every project's recorded compilation, by documentation ID
/// (ADR 0070). Calls inside lambdas and local functions count for the member around them,
/// accessors for their property; a call through an interface member or a base method counts for
/// every implementation and override.
/// </summary>
internal sealed class CallGraph
{
    private readonly Dictionary<string, List<CallSite>> _callers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CallSite>> _callees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ISymbol Symbol, ProjectInfo Project)> _declared = new(StringComparer.Ordinal);

    /// <summary>Interface members and overridden methods, to the source members that implement or override them.</summary>
    private readonly Dictionary<string, List<string>> _implementations = new(StringComparer.Ordinal);

    private CallGraph()
    {
    }

    public static CallGraph Build(IEnumerable<(ProjectInfo Project, Compilation Compilation)> compilations)
    {
        var graph = new CallGraph();
        foreach (var (project, compilation) in compilations)
        {
            foreach (var tree in AuditEngine.Sources(compilation))
            {
                var model = compilation.GetSemanticModel(tree);
                graph.Declare(project, model, tree);
                graph.Uses(project, model, tree);
            }
        }

        foreach (var list in graph._callers.Values.Concat(graph._callees.Values))
        {
            list.Sort(Compare);
        }

        return graph;
    }

    /// <summary>The source member a documentation ID names, with its project, or null.</summary>
    public (ISymbol Symbol, ProjectInfo Project)? Declared(string member) =>
        _declared.TryGetValue(member, out var declared) ? declared : null;

    /// <summary>The places that use a member, including through the interface members it implements and the methods it overrides.</summary>
    public IReadOnlyList<CallSite> CallersOf(string member)
    {
        var aliases = _declared.TryGetValue(member, out var declared) && declared.Symbol is IMethodSymbol method
            ? SymbolIdentity.Aliases(method).Select(Id).OfType<string>()
            : [];
        return [.. aliases.Prepend(member).Distinct(StringComparer.Ordinal)
            .SelectMany(id => _callers.TryGetValue(id, out var sites) ? sites : [])
            .Distinct()
            .OrderBy(s => s, Comparer<CallSite>.Create(Compare))];
    }

    /// <summary>What a member uses directly; a use of an interface member or virtual method also reaches its source implementations.</summary>
    public IReadOnlyList<CallSite> CalleesOf(string member) =>
        _callees.TryGetValue(member, out var sites) ? sites : [];

    /// <summary>The source members an interface member or virtual method dispatches to.</summary>
    public IReadOnlyList<string> ImplementationsOf(string member) =>
        _implementations.TryGetValue(member, out var implementations) ? implementations : [];

    /// <summary>The member a node belongs to: lambdas and local functions count for the member around them, accessors for their property.</summary>
    public static ISymbol Member(SemanticModel model, SyntaxNode node)
    {
        var symbol = model.GetEnclosingSymbol(node.SpanStart)!;
        while (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }

        return Normalize(symbol);
    }

    public static string? Id(ISymbol symbol) => Normalize(symbol).OriginalDefinition.GetDocumentationCommentId();

    private static ISymbol Normalize(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { ReducedFrom: { } reduced } => reduced,
        IMethodSymbol { AssociatedSymbol: IPropertySymbol or IEventSymbol } accessor => accessor.AssociatedSymbol!,
        _ => symbol,
    };

    private void Declare(ProjectInfo project, SemanticModel model, SyntaxTree tree)
    {
        foreach (var node in tree.GetRoot().DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or VariableDeclaratorSyntax))
        {
            if (model.GetDeclaredSymbol(node) is not { } symbol || Id(symbol) is not { } id)
            {
                continue;
            }

            _declared.TryAdd(id, (symbol, project));
            if (symbol is IMethodSymbol method)
            {
                foreach (var alias in SymbolIdentity.Aliases(method).Select(Id).OfType<string>())
                {
                    if (!_implementations.TryGetValue(alias, out var list))
                    {
                        _implementations[alias] = list = [];
                    }

                    if (!list.Contains(id, StringComparer.Ordinal))
                    {
                        list.Add(id);
                        list.Sort(StringComparer.Ordinal);
                    }
                }
            }
        }
    }

    private void Uses(ProjectInfo project, SemanticModel model, SyntaxTree tree)
    {
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var callee = node switch
            {
                BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax => AuditEngine.Bound(model, node),
                SimpleNameSyntax name when !IsDeclarationName(name) => AuditEngine.Bound(model, name),
                _ => null,
            };
            if (callee is not (IMethodSymbol or IPropertySymbol or IFieldSymbol { IsConst: false }) || Id(callee) is not { } calleeId)
            {
                continue;
            }

            var member = Member(model, node);
            if (Id(member) is not { } callerId || callerId == calleeId)
            {
                continue;
            }

            var site = new CallSite(callerId, calleeId, Normalize(callee), node, model, project);
            Add(_callers, calleeId, site);
            Add(_callees, callerId, site);
        }
    }

    /// <summary>A name that is the target of a creation, attribute, or qualified name's left side, not a use of its own.</summary>
    private static bool IsDeclarationName(SimpleNameSyntax name) =>
        name.Parent is QualifiedNameSyntax { Left: var left } && left == name
        || name.Parent is BaseObjectCreationExpressionSyntax or AttributeSyntax;

    private static void Add(Dictionary<string, List<CallSite>> index, string key, CallSite site)
    {
        if (!index.TryGetValue(key, out var list))
        {
            index[key] = list = [];
        }

        list.Add(site);
    }

    private static int Compare(CallSite a, CallSite b)
    {
        var byCaller = string.CompareOrdinal(a.Caller, b.Caller);
        if (byCaller != 0)
        {
            return byCaller;
        }

        var byFile = string.CompareOrdinal(a.Node.SyntaxTree.FilePath, b.Node.SyntaxTree.FilePath);
        return byFile != 0 ? byFile : a.Node.SpanStart.CompareTo(b.Node.SpanStart);
    }
}
