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

    /// <summary>Framework-only, with at least one framework-only dependency.</summary>
    Blocked,

    /// <summary>Already portable: standard, modern, or dual.</summary>
    Done,
}

/// <summary>
/// A project's readiness, the framework-only dependencies blocking it, how many projects depend on
/// it, and the project references outside the model that block it (its own and those of the
/// framework-only projects it depends on).
/// </summary>
public sealed record ProjectStanding(ProjectReadiness Readiness, IReadOnlyList<string> Blockers, int Dependents, IReadOnlyList<string> UnresolvedReferences);

/// <summary>
/// Structural readiness from the project graph (docs/spec/commands/workspace.md#plan):
/// a framework-only project is blocked by every framework-only project it depends on,
/// directly or transitively, and by every project reference outside the model on itself
/// or on one of those. A reference outside the model on a project that is already portable
/// does not block anything: that project builds for the target with it.
/// </summary>
public static class Readiness
{
    public static IReadOnlyDictionary<string, ProjectStanding> Compute(WorkspaceModel model)
    {
        var classes = model.Projects.ToDictionary(p => p.Id, p => p.FrameworkClass, StringComparer.Ordinal);
        var unresolvedOf = model.Projects.ToDictionary(p => p.Id, p => p.UnresolvedReferences.Select(r => r.Path), StringComparer.Ordinal);
        var dependencies = Adjacency(model.Graph.Edges, reverse: false);
        var dependents = Adjacency(model.Graph.Edges, reverse: true);

        var result = new SortedDictionary<string, ProjectStanding>(StringComparer.Ordinal);
        foreach (var project in model.Projects)
        {
            var blockers = Reach(project.Id, dependencies)
                .Where(id => classes.TryGetValue(id, out var c) && c == FrameworkClass.Framework)
                .Order(StringComparer.Ordinal)
                .ToList();
            var framework = project.FrameworkClass == FrameworkClass.Framework;
            var unresolved = !framework
                ? []
                : blockers.Prepend(project.Id)
                    .SelectMany(id => unresolvedOf.GetValueOrDefault(id) ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            var readiness = !framework
                ? ProjectReadiness.Done
                : blockers.Count == 0 && unresolved.Count == 0 ? ProjectReadiness.Ready : ProjectReadiness.Blocked;
            result[project.Id] = new ProjectStanding(readiness, blockers, Reach(project.Id, dependents).Count, unresolved);
        }

        return result;
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
