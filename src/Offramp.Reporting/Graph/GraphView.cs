using Offramp.Core.Model;
using Offramp.Workspace.Model;
using ModelEdge = Offramp.Core.Model.GraphEdge;

namespace Offramp.Reporting.Graph;

/// <summary>
/// Builds the graph document for a view of the model: kind filters, a focus
/// subgraph, the edge filter, clusters, and highlights. Readiness, blockers,
/// and dependents always come from the whole model, so a filtered view never
/// hides why a project is blocked.
/// </summary>
public static class GraphView
{
    /// <summary>How many blockers <c>--highlight blockers</c> marks.</summary>
    public const int TopBlockers = 10;

    public static GraphDocument Build(WorkspaceModel model, GraphViewOptions options)
    {
        var standings = Readiness.Compute(model);
        var edges = model.Graph.Edges
            .Where(e => options.Edges == GraphEdgeFilter.All || e.Kind == GraphEdgeKind.Project)
            .ToList();

        var shown = model.Projects
            .Where(p => options.IncludeKinds.Count == 0 || options.IncludeKinds.Contains(p.Kind))
            .Where(p => !options.ExcludeKinds.Contains(p.Kind))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (options.Focus is not null)
        {
            shown.IntersectWith(Around(options.Focus, edges, options.Depth, options.Direction));
            shown.Add(options.Focus);
        }

        var shownEdges = edges.Where(e => shown.Contains(e.From) && shown.Contains(e.To)).ToList();
        var cycles = model.Graph.Cycles
            .Select(c => (IReadOnlyList<string>)[.. c.Where(shown.Contains)])
            .Where(c => c.Count > 1 || (c.Count == 1 && shownEdges.Any(e => e.From == c[0] && e.To == c[0])))
            .ToList();
        var inCycle = model.Graph.Cycles.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);

        var nodes = model.Projects
            .Where(p => shown.Contains(p.Id))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p =>
            {
                var standing = standings[p.Id];
                var directory = Directory(p.Id);
                return new GraphNode
                {
                    Id = p.Id,
                    Name = p.Name,
                    Kind = p.Kind,
                    FrameworkClass = p.FrameworkClass,
                    TargetFrameworks = p.TargetFrameworks,
                    Loc = p.Loc,
                    PackageCount = p.PackageReferences.Count,
                    Readiness = standing.Readiness,
                    Blockers = standing.Blockers,
                    Dependents = standing.Dependents,
                    Directory = directory,
                    Cluster = options.Cluster switch
                    {
                        GraphClusterMode.Directory => directory,
                        GraphClusterMode.Kind => Wire(p.Kind),
                        _ => null,
                    },
                    InCycle = inCycle.Contains(p.Id),
                };
            })
            .ToList();

        return new GraphDocument
        {
            Nodes = nodes,
            Edges = [.. shownEdges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Kind)],
            Cycles = cycles,
            Highlight = new GraphHighlight(options.Highlight, Highlighted(options.Highlight, nodes, standings)),
            View = options,
        };
    }

    /// <summary>"src" for src/Foo/Foo.csproj, "src/Area" for src/Area/Foo/Foo.csproj, "." for a project at the root or one level down.</summary>
    public static string Directory(string projectId)
    {
        var parts = projectId.Split('/');
        return parts.Length <= 2 ? "." : string.Join('/', parts[..^2]);
    }

    private static List<string> Highlighted(GraphHighlightMode mode, List<GraphNode> nodes, IReadOnlyDictionary<string, ProjectStanding> standings) => mode switch
    {
        GraphHighlightMode.Cycles => [.. nodes.Where(n => n.InCycle).Select(n => n.Id)],
        GraphHighlightMode.Frontier => [.. nodes.Where(n => n.Readiness == ProjectReadiness.Ready).Select(n => n.Id)],
        GraphHighlightMode.Blockers => TopBlockersOf(nodes, standings),
        _ => [],
    };

    /// <summary>Framework-only projects ranked by how many projects they block (most first, then by id).</summary>
    private static List<string> TopBlockersOf(List<GraphNode> nodes, IReadOnlyDictionary<string, ProjectStanding> standings)
    {
        var blocks = standings.Values
            .SelectMany(s => s.Blockers)
            .GroupBy(b => b, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return
        [
            .. nodes
                .Where(n => blocks.ContainsKey(n.Id))
                .OrderByDescending(n => blocks[n.Id])
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .Take(TopBlockers)
                .Select(n => n.Id),
        ];
    }

    private static HashSet<string> Around(string focus, List<ModelEdge> edges, int? depth, GraphDirection direction)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { focus };
        if (direction != GraphDirection.Dependents)
        {
            result.UnionWith(Walk(focus, edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal), depth));
        }

        if (direction != GraphDirection.Dependencies)
        {
            result.UnionWith(Walk(focus, edges.ToLookup(e => e.To, e => e.From, StringComparer.Ordinal), depth));
        }

        return result;
    }

    private static HashSet<string> Walk(string start, ILookup<string, string> next, int? depth)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var frontier = new List<string> { start };
        for (var level = 0; frontier.Count > 0 && (depth is null || level < depth); level++)
        {
            frontier = [.. frontier.SelectMany(n => next[n]).Where(seen.Add)];
        }

        return seen;
    }

    private static string Wire(ProjectKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
