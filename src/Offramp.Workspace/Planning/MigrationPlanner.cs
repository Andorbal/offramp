using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Planning;

/// <summary>
/// Structural migration order from the project graph (docs/spec/commands/workspace.md#plan):
/// waves of framework-only projects, each depending only on earlier waves and on
/// projects already portable (<c>docs/decisions/0017-plan-and-verify.md</c>).
/// </summary>
public static class MigrationPlanner
{
    /// <param name="model">The workspace model; readiness and blast radius always come from all of it.</param>
    /// <param name="forProject">A resolved project path: list only the framework-only projects it needs, itself included.</param>
    /// <param name="frontierOnly">List only projects that can be ported today.</param>
    /// <param name="excludeKinds">Kinds to leave out of the listing.</param>
    public static PlanResult Plan(WorkspaceModel model, string? forProject, bool frontierOnly, IReadOnlyList<ProjectKind> excludeKinds)
    {
        var standings = Readiness.Compute(model);
        var waves = Waves(model, standings);
        var cycleOf = CycleMembership(model);
        var needed = forProject is null ? null : Closure(model, forProject);

        var order = model.Projects
            .Where(p => !excludeKinds.Contains(p.Kind))
            .Where(p => needed is null || (needed.Contains(p.Id) && p.FrameworkClass == FrameworkClass.Framework))
            .Where(p => !frontierOnly || standings[p.Id].Readiness == ProjectReadiness.Ready)
            .Select(p => new PlanEntry
            {
                Project = p.Id,
                Name = p.Name,
                Kind = p.Kind,
                FrameworkClass = p.FrameworkClass,
                Wave = waves[p.Id],
                BlastRadius = standings[p.Id].Dependents,
                Blockers = standings[p.Id].Blockers,
                Readiness = standings[p.Id].Readiness,
                InCycle = cycleOf.ContainsKey(p.Id),
            })
            .OrderBy(e => e.Wave)
            .ThenByDescending(e => e.BlastRadius)
            .ThenBy(e => e.Project, StringComparer.Ordinal)
            .ToList();

        var listed = order.Select(e => e.Project).ToHashSet(StringComparer.Ordinal);
        return new PlanResult
        {
            For = forProject,
            Frontier = frontierOnly,
            ExcludeKinds = [.. excludeKinds.Distinct().Order()],
            Order = order,
            Cycles = [.. model.Graph.Cycles.Where(c => c.Any(listed.Contains))],
            Counts = new PlanCounts(
                order.Count,
                order.Count(e => e.Readiness == ProjectReadiness.Done),
                order.Count(e => e.Readiness == ProjectReadiness.Ready),
                order.Count(e => e.Readiness == ProjectReadiness.Blocked),
                order.Count == 0 ? 0 : order.Max(e => e.Wave)),
        };
    }

    /// <summary>
    /// 0 for portable projects; for a framework-only project, one more than the latest wave
    /// among its framework-only blockers outside its own cycle.
    /// </summary>
    internal static Dictionary<string, int> Waves(WorkspaceModel model, IReadOnlyDictionary<string, ProjectStanding> standings)
    {
        var cycleOf = CycleMembership(model);
        var waves = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in model.Projects)
        {
            WaveOf(project.Id);
        }

        return waves;

        int WaveOf(string id)
        {
            if (waves.TryGetValue(id, out var known))
            {
                return known;
            }

            var standing = standings[id];
            if (standing.Readiness == ProjectReadiness.Done)
            {
                return waves[id] = 0;
            }

            // Blockers are transitive, so the condensation is acyclic once cycle-mates are skipped.
            var own = cycleOf.GetValueOrDefault(id, -1);
            var wave = 1 + standing.Blockers
                .Where(b => own < 0 || cycleOf.GetValueOrDefault(b, -1) != own)
                .Select(WaveOf)
                .DefaultIfEmpty(0)
                .Max();
            return waves[id] = wave;
        }
    }

    private static Dictionary<string, int> CycleMembership(WorkspaceModel model)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < model.Graph.Cycles.Count; i++)
        {
            foreach (var member in model.Graph.Cycles[i])
            {
                result.TryAdd(member, i);
            }
        }

        return result;
    }

    /// <summary>The project and everything it depends on.</summary>
    private static HashSet<string> Closure(WorkspaceModel model, string start)
    {
        var dependencies = model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([start]);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (seen.Add(id))
            {
                foreach (var next in dependencies[id])
                {
                    pending.Push(next);
                }
            }
        }

        return seen;
    }
}
