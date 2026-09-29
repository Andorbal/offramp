using System.Text.Json.Serialization;
using Offramp.Core.Json;
using Offramp.Core.Model;

namespace Offramp.Workspace.Model;

/// <summary>Whether a project can be ported today.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ProjectReadiness>))]
public enum ProjectReadiness
{
    /// <summary>Framework-only, and every dependency is already portable.</summary>
    Ready,

    /// <summary>
    /// Depends on at least one framework-only project: framework-only itself, or standard,
    /// modern, or dual with a framework-only project behind its portable targets.
    /// </summary>
    Blocked,

    /// <summary>Portable: standard, modern, or dual, with no framework-only project behind its portable targets.</summary>
    Done,
}

/// <summary>A project's readiness, the framework-only dependencies blocking it, and how many projects depend on it.</summary>
public sealed record ProjectStanding(ProjectReadiness Readiness, IReadOnlyList<string> Blockers, int Dependents);

/// <summary>
/// Structural readiness from the project graph (docs/spec/commands/workspace.md#plan):
/// a project is blocked by every framework-only project it depends on, directly or
/// transitively, through the references its portable targets use.
/// </summary>
public static class Readiness
{
    public static IReadOnlyDictionary<string, ProjectStanding> Compute(WorkspaceModel model)
    {
        var classes = model.Projects.ToDictionary(p => p.Id, p => p.FrameworkClass, StringComparer.Ordinal);
        var dependencies = Adjacency(UsedEdges(model.Projects, model.Graph), reverse: false);
        var dependents = Adjacency(model.Graph.Edges, reverse: true);

        var result = new SortedDictionary<string, ProjectStanding>(StringComparer.Ordinal);
        foreach (var project in model.Projects)
        {
            var blockers = Reach(project.Id, dependencies)
                .Where(id => classes.TryGetValue(id, out var c) && c == FrameworkClass.Framework)
                .Order(StringComparer.Ordinal)
                .ToList();
            var readiness = blockers.Count > 0 ? ProjectReadiness.Blocked
                : project.FrameworkClass == FrameworkClass.Framework ? ProjectReadiness.Ready
                : ProjectReadiness.Done;
            result[project.Id] = new ProjectStanding(readiness, blockers, Reach(project.Id, dependents).Count);
        }

        return result;
    }

    /// <summary>
    /// References from a standard, modern, or dual project's portable targets to a framework-only
    /// project (<c>OFR0121</c>). They build only when the referenced project skips NuGet's
    /// compatibility check, as a legacy project does, and fail at run time.
    /// </summary>
    public static IReadOnlyList<GraphEdge> FrameworkOnlyReferences(IReadOnlyList<ProjectInfo> projects, ProjectGraph graph)
    {
        var classes = projects.ToDictionary(p => p.Id, p => p.FrameworkClass, StringComparer.Ordinal);
        return [.. UsedEdges(projects, graph)
            .Where(e => classes.TryGetValue(e.From, out var from) && from != FrameworkClass.Framework
                && classes.TryGetValue(e.To, out var to) && to == FrameworkClass.Framework)];
    }

    /// <summary>
    /// The graph's edges, without the project references of a dual project that only its
    /// .NET Framework targets use. A framework-only project does not block a dual project
    /// through those: they stay on .NET Framework with it.
    /// </summary>
    private static IEnumerable<GraphEdge> UsedEdges(IReadOnlyList<ProjectInfo> projects, ProjectGraph graph)
    {
        var modern = projects
            .Where(p => p.ModernProjectReferences is not null)
            .ToDictionary(p => p.Id, p => p.ModernProjectReferences!.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        return graph.Edges.Where(e => e.Kind != GraphEdgeKind.Project
            || !modern.TryGetValue(e.From, out var used)
            || used.Contains(e.To));
    }

    private static Dictionary<string, List<string>> Adjacency(IEnumerable<GraphEdge> edges, bool reverse) =>
        edges
            .GroupBy(e => reverse ? e.To : e.From, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => reverse ? e.From : e.To).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

    /// <summary>Everything reachable from <paramref name="start"/>, excluding itself.</summary>
    private static HashSet<string> Reach(string start, Dictionary<string, List<string>> adjacency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(adjacency.GetValueOrDefault(start) ?? []);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (seen.Add(node))
            {
                foreach (var next in adjacency.GetValueOrDefault(node) ?? [])
                {
                    pending.Push(next);
                }
            }
        }

        seen.Remove(start);
        return seen;
    }
}
