using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Seams;

/// <summary>An unportable use found by <c>audit api</c>: the file and line, and the symbol.</summary>
public sealed record UnportableUse(string File, int Line, string Symbol);

public sealed record SeamsRequest
{
    public required string RepositoryRoot { get; init; }

    public required ProjectInfo Project { get; init; }

    /// <summary><c>audit</c> (the uses <c>audit api</c> found, in <see cref="Uses"/>) or <c>list</c> (<see cref="Symbols"/> only).</summary>
    public required string UnportableFrom { get; init; }

    /// <summary>Namespaces or types (prefixes of fully qualified names) that cannot port.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>The unportable uses <c>audit api</c> found in the project.</summary>
    public IReadOnlyList<UnportableUse> Uses { get; init; } = [];

    /// <summary>Report no seam when the minimum cut has more edges than this.</summary>
    public int? MaxCut { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>
/// <c>seams</c>: the smallest boundary around the code that cannot port.
/// <list type="number">
/// <item>The type reference graph of the project: an edge A → B when members of A reference
/// B (weight: how many members of A do).</item>
/// <item>Taint: types that use unportable symbols, then types that inherit from a tainted type
/// or expose one in a public or protected signature (constructor parameters aside: that is
/// where <c>extract interface</c> puts the interface). Calls never taint.</item>
/// <item>Strongly connected components with a tainted type are tainted whole.</item>
/// <item>The minimum cut between clean entry points (clean types nothing in the project
/// references) and the tainted set, closest to the taint among minimum cuts, over the
/// component DAG. Cut edges grouped by their tainted type are the seams.</item>
/// <item>A boundary type is an articulation point when no clean type reaches the taint
/// without it.</item>
/// </list>
/// </summary>
public static class SeamsAnalyzer
{
    public static SeamsResult? Analyze(SeamsRequest request, Compilation compilation)
    {
        var graph = TypeGraph.Build(request.RepositoryRoot, compilation);
        var tainted = Taint(request, graph, compilation);
        if (tainted.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4001, $"Nothing in {request.Project.Id} uses the unportable symbols, so there is no seam to find.", new DiagnosticLocation(request.Project.Id));
            return Result(request, graph, tainted, [], [], []);
        }

        var components = Components(graph, tainted);
        var cut = MinimumCut(graph, tainted);
        if (cut.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4001,
                $"No clean entry point of {request.Project.Id} reaches the unportable code through a type the project could put an interface on: the taint reaches the project's entry points directly.",
                new DiagnosticLocation(request.Project.Id));
            return Result(request, graph, tainted, components, [], cut);
        }

        if (request.MaxCut is { } max && cut.Count > max)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4001,
                $"The smallest boundary around the unportable code in {request.Project.Id} crosses {cut.Count} references, more than --max-cut {max}.",
                new DiagnosticLocation(request.Project.Id));
            return Result(request, graph, tainted, components, [], cut);
        }

        var seams = Seams(request, graph, compilation, tainted, cut);
        return Result(request, graph, tainted, components, seams, cut);
    }

    // ----- the type graph -----

    /// <summary>Named types declared in the project and the references between them.</summary>
    internal sealed class TypeGraph
    {
        public SortedDictionary<string, INamedTypeSymbol> Types { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Loc { get; } = new(StringComparer.Ordinal);

        /// <summary>From → to → the members of From that reference To.</summary>
        public SortedDictionary<string, SortedDictionary<string, HashSet<string>>> Edges { get; } = new(StringComparer.Ordinal);

        /// <summary>Every reference site: from type, from member, to type, the referenced member, and the node.</summary>
        public List<(string From, string FromMember, string To, ISymbol Target, SyntaxNode Node)> Sites { get; } = [];

        /// <summary>From type → the symbols it uses (for taint by symbol list).</summary>
        public Dictionary<string, List<(ISymbol Symbol, SyntaxNode Node)>> Uses { get; } = new(StringComparer.Ordinal);

        public static TypeGraph Build(string root, Compilation compilation)
        {
            var graph = new TypeGraph();
            var trees = AuditEngine.Sources(compilation);
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol type)
                    {
                        var name = Name(type);
                        graph.Types[name] = type;
                        var lines = declaration.GetLocation().GetLineSpan();
                        graph.Loc[name] = graph.Loc.GetValueOrDefault(name) + lines.EndLinePosition.Line - lines.StartLinePosition.Line + 1;
                    }
                }
            }

            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    if (node is not (SimpleNameSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax))
                    {
                        continue;
                    }

                    var owner = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
                    if (owner is null || model.GetDeclaredSymbol(owner) is not INamedTypeSymbol from || AuditEngine.Bound(model, node) is not { } target || target is INamespaceSymbol)
                    {
                        continue;
                    }

                    var fromName = Name(from);
                    var member = MemberOf(node, owner, model) ?? fromName;
                    (graph.Uses.TryGetValue(fromName, out var uses) ? uses : graph.Uses[fromName] = []).Add((target, node));
                    var targetType = target as INamedTypeSymbol ?? target.ContainingType;
                    if (targetType is null || !graph.Types.ContainsKey(Name(targetType)))
                    {
                        continue;
                    }

                    var toName = Name(targetType);
                    if (toName == fromName)
                    {
                        continue;
                    }

                    var targets = graph.Edges.TryGetValue(fromName, out var existing) ? existing : graph.Edges[fromName] = new(StringComparer.Ordinal);
                    (targets.TryGetValue(toName, out var members) ? members : targets[toName] = new HashSet<string>(StringComparer.Ordinal)).Add(member);
                    graph.Sites.Add((fromName, member, toName, target, node));
                }
            }

            return graph;
        }

        public IEnumerable<string> Targets(string from) => Edges.TryGetValue(from, out var targets) ? targets.Keys : [];

        public int Weight(string from, string to) => Edges.TryGetValue(from, out var targets) && targets.TryGetValue(to, out var members) ? members.Count : 0;

        private static string? MemberOf(SyntaxNode node, SyntaxNode owner, SemanticModel model)
        {
            var member = node.Ancestors().TakeWhile(a => a != owner).OfType<MemberDeclarationSyntax>().LastOrDefault();
            var symbol = member switch
            {
                null => null,
                BaseFieldDeclarationSyntax field => model.GetDeclaredSymbol(field.Declaration.Variables[0]),
                _ => model.GetDeclaredSymbol(member),
            };
            return symbol is null ? null : symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        }
    }

    private static string Name(ISymbol symbol) => AuditEngine.Name(symbol is INamedTypeSymbol type ? type.OriginalDefinition : symbol);

    // ----- taint -----

    private static SortedDictionary<string, List<string>> Taint(SeamsRequest request, TypeGraph graph, Compilation compilation)
    {
        var tainted = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string type, string reason)
        {
            var reasons = tainted.TryGetValue(type, out var list) ? list : tainted[type] = [];
            if (!reasons.Contains(reason, StringComparer.Ordinal))
            {
                reasons.Add(reason);
            }
        }

        // Audit findings and listed symbols both taint (seams.unportableSymbols applies to either source).
        foreach (var use in request.Uses)
        {
            if (TypeAt(request.RepositoryRoot, compilation, use.File, use.Line) is { } type && graph.Types.ContainsKey(type))
            {
                Add(type, use.Symbol);
            }
        }

        foreach (var (type, uses) in graph.Uses)
        {
            foreach (var (symbol, _) in uses)
            {
                var full = FullName(symbol);
                if (request.Symbols.Any(s => full == s || full.StartsWith(s + ".", StringComparison.Ordinal)))
                {
                    Add(type, TypeName(symbol));
                }
            }
        }

        // Structural taint, to a fixed point: inheritance and exposure in public signatures.
        bool changed;
        do
        {
            changed = false;
            foreach (var (name, type) in graph.Types)
            {
                if (tainted.ContainsKey(name))
                {
                    continue;
                }

                if (type.BaseType is { } baseType && tainted.ContainsKey(Name(baseType)))
                {
                    Add(name, $"inherits {Name(baseType)}");
                    changed = true;
                    continue;
                }

                if (Exposed(type).FirstOrDefault(e => tainted.ContainsKey(e.Type)) is { Type: not null } exposed)
                {
                    Add(name, $"exposes {exposed.Type} in {exposed.Member}");
                    changed = true;
                }
            }
        }
        while (changed);

        return tainted;
    }

    /// <summary>Types in the public or protected signatures of a type (fields, properties, method parameters and returns; not constructors).</summary>
    private static IEnumerable<(string Type, string Member)> Exposed(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
        {
            IEnumerable<ITypeSymbol> types = member switch
            {
                IFieldSymbol field => [field.Type],
                IPropertySymbol property => [property.Type, .. property.Parameters.Select(p => p.Type)],
                IMethodSymbol { MethodKind: MethodKind.Ordinary } method => [method.ReturnType, .. method.Parameters.Select(p => p.Type)],
                IEventSymbol @event => [@event.Type],
                _ => [],
            };
            foreach (var mentioned in types.SelectMany(Mentioned))
            {
                yield return (Name(mentioned), member.Name);
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> Mentioned(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var inner in Mentioned(array.ElementType))
                {
                    yield return inner;
                }

                break;
            case INamedTypeSymbol named:
                yield return named;
                foreach (var argument in named.TypeArguments.SelectMany(Mentioned))
                {
                    yield return argument;
                }

                break;
        }
    }

    private static string? TypeAt(string root, Compilation compilation, string file, int line)
    {
        var tree = compilation.SyntaxTrees.FirstOrDefault(t => Path.IsPathRooted(t.FilePath) && RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(t.FilePath)) == file);
        if (tree is null || line < 1 || line > tree.GetText().Lines.Count)
        {
            return null;
        }

        var position = tree.GetText().Lines[line - 1].Start;
        var node = tree.GetRoot().FindToken(position).Parent;
        var declaration = node?.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return declaration is not null && compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration) is INamedTypeSymbol type ? Name(type) : null;
    }

    private static string FullName(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => Name(type),
        _ when symbol.ContainingType is { } containing => Name(containing) + "." + symbol.Name,
        _ => symbol.ToDisplayString(),
    };

    private static string TypeName(ISymbol symbol) => symbol is INamedTypeSymbol type ? Name(type) : symbol.ContainingType is { } containing ? Name(containing) : symbol.ToDisplayString();

    // ----- components and the cut -----

    /// <summary>Strongly connected components (Tarjan, in name order); a component with a tainted type is tainted whole.</summary>
    private static List<List<string>> Components(TypeGraph graph, SortedDictionary<string, List<string>> tainted)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<List<string>>();

        void Visit(string node)
        {
            indices[node] = low[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var next in graph.Targets(node))
            {
                if (!indices.TryGetValue(next, out var seen))
                {
                    Visit(next);
                    low[node] = Math.Min(low[node], low[next]);
                }
                else if (onStack.Contains(next))
                {
                    low[node] = Math.Min(low[node], seen);
                }
            }

            if (low[node] == indices[node])
            {
                var component = new List<string>();
                string member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                }
                while (member != node);

                components.Add([.. component.Order(StringComparer.Ordinal)]);
            }
        }

        foreach (var node in graph.Types.Keys)
        {
            if (!indices.ContainsKey(node))
            {
                Visit(node);
            }
        }

        foreach (var component in components.Where(c => c.Count > 1 && c.Any(tainted.ContainsKey)))
        {
            var reason = $"in a reference cycle with {string.Join(", ", component.Where(tainted.ContainsKey))}";
            foreach (var type in component.Where(t => !tainted.ContainsKey(t)))
            {
                tainted[type] = [reason];
            }
        }

        return components;
    }

    /// <summary>
    /// The minimum cut between clean entry points and the tainted set (Edmonds–Karp over type
    /// edges, capacities = referencing members), taking among minimum cuts the one closest to
    /// the taint: the tainted side is what still reaches the sink in the residual graph.
    /// </summary>
    private static List<(string From, string To)> MinimumCut(TypeGraph graph, SortedDictionary<string, List<string>> tainted)
    {
        var incoming = graph.Edges.Values.SelectMany(t => t.Keys).ToHashSet(StringComparer.Ordinal);
        var sources = graph.Types.Keys.Where(t => !tainted.ContainsKey(t) && !incoming.Contains(t) && Reaches(graph, t, tainted)).ToList();
        if (sources.Count == 0)
        {
            return [];
        }

        const string Source = "\u0001source";
        const string Sink = "\u0001sink";
        const int Infinite = int.MaxValue / 4;
        var capacity = new Dictionary<(string, string), int>();
        var neighbors = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Edge(string from, string to, int value)
        {
            capacity[(from, to)] = capacity.GetValueOrDefault((from, to)) + value;
            capacity.TryAdd((to, from), 0);
            (neighbors.TryGetValue(from, out var a) ? a : neighbors[from] = new(StringComparer.Ordinal)).Add(to);
            (neighbors.TryGetValue(to, out var b) ? b : neighbors[to] = new(StringComparer.Ordinal)).Add(from);
        }

        foreach (var source in sources)
        {
            Edge(Source, source, Infinite);
        }

        foreach (var type in tainted.Keys)
        {
            Edge(type, Sink, Infinite);
        }

        foreach (var (from, targets) in graph.Edges)
        {
            if (tainted.ContainsKey(from))
            {
                continue; // the tainted side flows to the sink already
            }

            foreach (var (to, members) in targets)
            {
                Edge(from, to, members.Count);
            }
        }

        // Edmonds–Karp: shortest augmenting paths, neighbors in name order.
        while (true)
        {
            var parent = new Dictionary<string, string>(StringComparer.Ordinal) { [Source] = Source };
            var queue = new Queue<string>([Source]);
            while (queue.Count > 0 && !parent.ContainsKey(Sink))
            {
                var node = queue.Dequeue();
                foreach (var next in neighbors.GetValueOrDefault(node) ?? [])
                {
                    if (!parent.ContainsKey(next) && capacity[(node, next)] > 0)
                    {
                        parent[next] = node;
                        queue.Enqueue(next);
                    }
                }
            }

            if (!parent.ContainsKey(Sink))
            {
                break;
            }

            var flow = Infinite;
            for (var node = Sink; node != Source; node = parent[node])
            {
                flow = Math.Min(flow, capacity[(parent[node], node)]);
            }

            for (var node = Sink; node != Source; node = parent[node])
            {
                capacity[(parent[node], node)] -= flow;
                capacity[(node, parent[node])] += flow;
            }
        }

        // The sink side: nodes that can still reach the sink through residual capacity.
        var sinkSide = new HashSet<string>(StringComparer.Ordinal) { Sink };
        var pending = new Queue<string>([Sink]);
        while (pending.TryDequeue(out var node))
        {
            foreach (var previous in neighbors.GetValueOrDefault(node) ?? [])
            {
                if (!sinkSide.Contains(previous) && capacity[(previous, node)] > 0)
                {
                    sinkSide.Add(previous);
                    pending.Enqueue(previous);
                }
            }
        }

        return [.. graph.Edges
            .Where(e => !sinkSide.Contains(e.Key) && !tainted.ContainsKey(e.Key))
            .SelectMany(e => e.Value.Keys.Where(sinkSide.Contains).Select(to => (e.Key, to)))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ThenBy(e => e.to, StringComparer.Ordinal)];
    }

    private static bool Reaches(TypeGraph graph, string start, SortedDictionary<string, List<string>> tainted, string? without = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>([start]);
        while (queue.TryDequeue(out var node))
        {
            foreach (var next in graph.Targets(node))
            {
                if (next == without || !seen.Add(next))
                {
                    continue;
                }

                if (tainted.ContainsKey(next))
                {
                    return true;
                }

                queue.Enqueue(next);
            }
        }

        return false;
    }

    // ----- seams -----

    private static List<Seam> Seams(SeamsRequest request, TypeGraph graph, Compilation compilation, SortedDictionary<string, List<string>> tainted, List<(string From, string To)> cut)
    {
        var incoming = graph.Edges.Values.SelectMany(t => t.Keys).ToHashSet(StringComparer.Ordinal);
        var cleanRoots = graph.Types.Keys.Where(t => !tainted.ContainsKey(t) && !incoming.Contains(t)).ToList();
        var seams = new List<Seam>();
        foreach (var group in cut.GroupBy(c => c.To, StringComparer.Ordinal))
        {
            var boundary = group.Key;
            var callers = group.Select(c => c.From).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var members = graph.Sites
                .Where(s => s.To == boundary && callers.Contains(s.From) && s.Target is not INamedTypeSymbol && s.Target is not IMethodSymbol { MethodKind: MethodKind.Constructor })
                .GroupBy(s => s.Target.OriginalDefinition, SymbolEqualityComparer.Default)
                .Select(g => Member((ISymbol)g.Key!, g.Count()))
                .OrderBy(m => m.Signature, StringComparer.Ordinal)
                .ToList();

            foreach (var member in members.Where(m => !m.WireFriendly))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR4002, $"{boundary}: {member.Signature} is not wire-friendly ({string.Join("; ", member.Problems)}).",
                    new DiagnosticLocation(request.Project.Id), [KeyValuePair.Create<string, JsonNode?>("member", member.Signature)]);
            }

            foreach (var member in members.Where(m => m.Static))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR4003, $"{boundary}: {member.Signature} is static; an interface needs an instance wrapper for it.",
                    new DiagnosticLocation(request.Project.Id), [KeyValuePair.Create<string, JsonNode?>("member", member.Signature)]);
            }

            var articulation = cleanRoots.All(root => !Reaches(graph, root, tainted, without: boundary));
            var friendly = members.Count == 0 ? 1.0 : (double)members.Count(m => m.WireFriendly && !m.Static) / members.Count;
            var score = Math.Round(friendly / (1 + (0.1 * Math.Max(0, members.Count - 1))) * (articulation ? 1.0 : 0.8), 2);
            var type = graph.Types[boundary];
            seams.Add(new Seam
            {
                Id = "",
                BoundaryType = boundary,
                Callers = callers,
                Members = members,
                ProposedInterface = type.TypeKind == TypeKind.Interface ? type.Name : "I" + type.Name,
                Score = score,
                ArticulationPoint = articulation,
            });
        }

        return [.. seams
            .OrderBy(s => s.Members.Count)
            .ThenByDescending(s => s.Callers.Count)
            .ThenByDescending(s => s.Members.Count(m => m.WireFriendly))
            .ThenBy(s => s.BoundaryType, StringComparer.Ordinal)
            .Select((s, i) => s with { Id = $"seam-{i + 1}" })];
    }

    private static SeamMember Member(ISymbol symbol, int callSites)
    {
        var problems = WireFriendliness.Problems(symbol).ToList();
        return new SeamMember
        {
            Signature = Signature(symbol),
            CallSites = callSites,
            WireFriendly = problems.Count == 0,
            Static = symbol.IsStatic,
            Problems = problems,
        };
    }

    /// <summary>A member as C# declares it, fully qualified: <c>Accounts.Directory.DirectoryEntryInfo FindUser(string samAccountName)</c>.</summary>
    public static string Signature(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => $"{(method.IsStatic ? "static " : "")}{Type(method.ReturnType)} {method.Name}({string.Join(", ", method.Parameters.Select(p => $"{RefKind(p)}{Type(p.Type)} {p.Name}"))})",
        IPropertySymbol property => $"{(property.IsStatic ? "static " : "")}{Type(property.Type)} {property.Name} {{ {(property.GetMethod is not null ? "get; " : "")}{(property.SetMethod is not null ? "set; " : "")}}}",
        IFieldSymbol field => $"{(field.IsStatic ? "static " : "")}{Type(field.Type)} {field.Name}",
        IEventSymbol @event => $"event {Type(@event.Type)} {@event.Name}",
        _ => symbol.ToDisplayString(),
    };

    private static string RefKind(IParameterSymbol parameter) => parameter.RefKind switch
    {
        Microsoft.CodeAnalysis.RefKind.Ref => "ref ",
        Microsoft.CodeAnalysis.RefKind.Out => "out ",
        Microsoft.CodeAnalysis.RefKind.In => "in ",
        _ => "",
    };

    private static string Type(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    private static SeamsResult Result(
        SeamsRequest request,
        TypeGraph graph,
        SortedDictionary<string, List<string>> tainted,
        List<List<string>> components,
        List<Seam> seams,
        List<(string From, string To)> cut)
    {
        var partitions = components
            .Where(c => c.Any(tainted.ContainsKey))
            .OrderBy(c => c[0], StringComparer.Ordinal)
            .Select((c, i) => new SeamPartition(i + 1, c, c.Sum(t => graph.Loc.GetValueOrDefault(t))))
            .ToList();
        var cutSet = cut.ToHashSet();
        var extraction = tainted.Count == 0 ? null : new SeamExtraction(
            (request.Project.AssemblyName ?? request.Project.Name) + ".Windows",
            [.. tainted.Keys],
            tainted.Keys.Sum(t => graph.Loc.GetValueOrDefault(t)));
        return new SeamsResult
        {
            Project = request.Project.Id,
            UnportableFrom = request.UnportableFrom,
            Tainted = [.. tainted.Select(t => new TaintedType(t.Key, [.. t.Value.Order(StringComparer.Ordinal)], graph.Loc.GetValueOrDefault(t.Key)))],
            Partitions = partitions,
            Seams = seams,
            Extraction = extraction,
            Types = [.. graph.Types.Keys],
            Edges = [.. graph.Edges.SelectMany(e => e.Value.Select(t => new SeamEdge(e.Key, t.Key, t.Value.Count, cutSet.Contains((e.Key, t.Key)))))],
        };
    }
}
