using Offramp.Core.Model;

namespace Offramp.Workspace.Model;

/// <summary>
/// The project graph: ProjectReference edges plus HintPath references to another
/// project's output, cycles (strongly connected components), and a leaf-first
/// topological order with ordinal tie-breaking.
/// </summary>
public static class GraphBuilder
{
    public static ProjectGraph Build(IReadOnlyList<ProjectInfo> projects)
    {
        var ids = projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var byAssembly = projects
            .Where(p => p.AssemblyName is not null)
            .GroupBy(p => p.AssemblyName!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.OrdinalIgnoreCase);

        var edges = new SortedSet<GraphEdge>(EdgeOrder.Instance);
        foreach (var project in projects)
        {
            foreach (var reference in project.ProjectReferences.Where(ids.Contains))
            {
                edges.Add(new GraphEdge(project.Id, reference, GraphEdgeKind.Project));
            }

            foreach (var reference in project.AssemblyReferences.Where(r => r.Kind == AssemblyReferenceKind.File))
            {
                var name = reference.HintPath is null
                    ? reference.Name
                    : Path.GetFileNameWithoutExtension(reference.HintPath.Replace('\\', '/'));
                if (byAssembly.TryGetValue(name, out var target) && target != project.Id)
                {
                    edges.Add(new GraphEdge(project.Id, target, GraphEdgeKind.Assembly));
                }
            }
        }

        var edgeList = edges.ToList();
        var components = StronglyConnected(projects.Select(p => p.Id).Order(StringComparer.Ordinal).ToList(), edgeList);
        var cycles = components
            .Where(c => c.Count > 1 || edgeList.Any(e => e.From == c[0] && e.To == c[0]))
            .Select(c => (IReadOnlyList<string>)c)
            .OrderBy(c => c[0], StringComparer.Ordinal)
            .ToList();

        return new ProjectGraph
        {
            Edges = edgeList,
            Cycles = cycles,
            TopologicalOrder = TopologicalOrder(components, edgeList),
        };
    }

    /// <summary>A readable path around a cycle, starting and ending at its first member: A → B → A.</summary>
    public static IReadOnlyList<string> CyclePath(IReadOnlyList<string> cycle, IReadOnlyList<GraphEdge> edges)
    {
        var members = cycle.ToHashSet(StringComparer.Ordinal);
        var start = cycle[0];
        var next = edges.Where(e => members.Contains(e.From) && members.Contains(e.To))
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var path = new List<string> { start };
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        return Walk(start) ? path : [.. cycle, start];

        bool Walk(string node)
        {
            foreach (var target in next.GetValueOrDefault(node) ?? [])
            {
                if (target == start)
                {
                    path.Add(start);
                    return true;
                }

                if (visited.Add(target))
                {
                    path.Add(target);
                    if (Walk(target))
                    {
                        return true;
                    }

                    path.RemoveAt(path.Count - 1);
                }
            }

            return false;
        }
    }

    /// <summary>Tarjan's algorithm; each component's members sorted ordinally.</summary>
    private static List<List<string>> StronglyConnected(IReadOnlyList<string> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var adjacency = edges.GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<List<string>>();

        foreach (var node in nodes)
        {
            if (!indexes.ContainsKey(node))
            {
                Visit(node);
            }
        }

        return result;

        void Visit(string node)
        {
            // Iterative DFS keeps deep graphs (thousands of projects in a chain) off the call stack.
            var work = new Stack<(string Node, int Next)>();
            work.Push((node, 0));
            indexes[node] = lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            while (work.Count > 0)
            {
                var (current, next) = work.Pop();
                var targets = adjacency.GetValueOrDefault(current) ?? [];
                if (next < targets.Count)
                {
                    work.Push((current, next + 1));
                    var target = targets[next];
                    if (!indexes.TryGetValue(target, out var targetIndex))
                    {
                        indexes[target] = lowLinks[target] = index++;
                        stack.Push(target);
                        onStack.Add(target);
                        work.Push((target, 0));
                    }
                    else if (onStack.Contains(target))
                    {
                        lowLinks[current] = Math.Min(lowLinks[current], targetIndex);
                    }

                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    lowLinks[parent] = Math.Min(lowLinks[parent], lowLinks[current]);
                }

                if (lowLinks[current] == indexes[current])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (member != current);
                    component.Sort(StringComparer.Ordinal);
                    result.Add(component);
                }
            }
        }
    }

    /// <summary>Dependencies before dependents; components in ordinal order of their first member when free to choose.</summary>
    private static List<string> TopologicalOrder(List<List<string>> components, IReadOnlyList<GraphEdge> edges)
    {
        var componentOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < components.Count; i++)
        {
            foreach (var member in components[i])
            {
                componentOf[member] = i;
            }
        }

        // dependsOn[c] = components c depends on (edge c -> d means c depends on d).
        var pending = new int[components.Count];
        var dependents = new Dictionary<int, HashSet<int>>();
        foreach (var edge in edges)
        {
            if (!componentOf.TryGetValue(edge.From, out var from) || !componentOf.TryGetValue(edge.To, out var to) || from == to)
            {
                continue;
            }

            if (!dependents.TryGetValue(to, out var set))
            {
                dependents[to] = set = [];
            }

            if (set.Add(from))
            {
                pending[from]++;
            }
        }

        var ready = new SortedSet<(string Key, int Component)>(
            Enumerable.Range(0, components.Count).Where(c => pending[c] == 0).Select(c => (components[c][0], c)));
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            order.AddRange(components[next.Component]);
            foreach (var dependent in dependents.GetValueOrDefault(next.Component) ?? [])
            {
                if (--pending[dependent] == 0)
                {
                    ready.Add((components[dependent][0], dependent));
                }
            }
        }

        return order;
    }

    private sealed class EdgeOrder : IComparer<GraphEdge>
    {
        public static readonly EdgeOrder Instance = new();

        public int Compare(GraphEdge? x, GraphEdge? y)
        {
            var result = string.CompareOrdinal(x!.From, y!.From);
            if (result != 0) return result;
            result = string.CompareOrdinal(x.To, y.To);
            return result != 0 ? result : x.Kind.CompareTo(y.Kind);
        }
    }
}
