using Offramp.Core.Model;
using Offramp.Reporting.Graph;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

namespace Offramp.Reporting.Report;

/// <summary>
/// Builds the report's data: the series from ledger snapshots, everything else
/// from the current model, which is always the series' last point
/// (<c>docs/decisions/0016-report.md</c>).
/// </summary>
public static class ReportBuilder
{
    /// <summary>The wire names of the framework classes, in stacking order (framework at the bottom).</summary>
    public static readonly IReadOnlyList<string> Classes = ["framework", "dual", "standard", "modern"];

    private static readonly ProjectKind[] ApplicationKinds = [ProjectKind.Console, ProjectKind.Service, ProjectKind.Web, ProjectKind.Winforms, ProjectKind.Wpf];

    /// <param name="model">The current model: the last point of the series, and the source of areas, applications, and the frontier.</param>
    /// <param name="snapshots">Ledger snapshots in any order.</param>
    /// <param name="title">The report's title.</param>
    /// <param name="since">Inclusive lower bound compared with each snapshot's <c>createdAt</c>: a date (<c>2026-09-01</c>) or a UTC timestamp.</param>
    public static ReportData Build(WorkspaceModel model, IReadOnlyList<LedgerSnapshot> snapshots, string title, string? since)
    {
        var series = Series(model, snapshots, since);
        var standings = Readiness.Compute(model);
        var applications = Applications(model, standings);
        var frontier = model.Projects
            .Where(p => standings[p.Id].Readiness == ProjectReadiness.Ready)
            .Select(p => new ReportFrontierProject(p.Id, p.Name, p.Loc, standings[p.Id].Dependents))
            .OrderByDescending(p => p.Dependents)
            .ThenBy(p => p.Project, StringComparer.Ordinal)
            .ToList();
        var now = series[^1];
        var framework = now.ByFrameworkClass["framework"];
        return new ReportData
        {
            Title = title,
            AsOf = model.CreatedAt,
            Since = since,
            Headline = new ReportHeadline
            {
                Projects = now.Projects,
                Loc = now.Loc,
                FrameworkProjects = framework.Projects,
                FrameworkLoc = framework.Loc,
                PortablePercent = now.Loc == 0 ? 0 : Math.Round(100.0 * (now.Loc - framework.Loc) / now.Loc, 1, MidpointRounding.AwayFromZero),
                FrameworkLocChange = framework.Loc - series[0].ByFrameworkClass["framework"].Loc,
                Applications = applications.Count,
                ApplicationsDone = applications.Count(a => a.Status == ProjectReadiness.Done),
                Ready = frontier.Count,
            },
            Series = series,
            Areas = Areas(model),
            Applications = applications,
            Frontier = frontier,
        };
    }

    /// <summary>Snapshots at or after <paramref name="since"/> and not newer than the model, then the model itself.</summary>
    private static List<ReportPoint> Series(WorkspaceModel model, IReadOnlyList<LedgerSnapshot> snapshots, string? since)
    {
        var current = Ledger.Snapshot(model);
        var points = snapshots
            .Where(s => since is null || string.CompareOrdinal(s.CreatedAt, since) >= 0)
            .Where(s => string.CompareOrdinal(s.CreatedAt, current.CreatedAt) < 0)
            .OrderBy(s => s.CreatedAt, StringComparer.Ordinal)
            .Append(current)
            .Select(Point)
            .ToList();

        // Scans on the same second are one point; the later one in ledger order wins.
        return [.. points.GroupBy(p => p.CreatedAt, StringComparer.Ordinal).Select(g => g.Last())];
    }

    private static ReportPoint Point(LedgerSnapshot snapshot) => new()
    {
        CreatedAt = snapshot.CreatedAt,
        Projects = snapshot.Totals.Projects,
        Loc = snapshot.Totals.Loc,
        ByFrameworkClass = Complete(snapshot.Totals.ByFrameworkClass),
        ByKind = new SortedDictionary<string, int>(snapshot.Totals.ByKind, StringComparer.Ordinal),
    };

    private static List<ReportArea> Areas(WorkspaceModel model) =>
        [.. model.Projects
            .GroupBy(p => GraphView.Directory(p.Id), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ReportArea(
                g.Key,
                g.Count(),
                g.Sum(p => p.Loc),
                Complete(new SortedDictionary<string, ClassTotals>(
                    g.GroupBy(p => Wire(p.FrameworkClass)).ToDictionary(c => c.Key, c => new ClassTotals(c.Count(), c.Sum(p => p.Loc))),
                    StringComparer.Ordinal))))];

    private static List<ReportApplication> Applications(WorkspaceModel model, IReadOnlyDictionary<string, ProjectStanding> standings)
    {
        var byId = model.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var dependencies = model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        return
        [
            .. model.Projects
                .Where(p => ApplicationKinds.Contains(p.Kind))
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p =>
                {
                    var closure = Closure(p.Id, dependencies);
                    var remaining = closure
                        .Where(id => byId.TryGetValue(id, out var d) && d.FrameworkClass == FrameworkClass.Framework)
                        .Order(StringComparer.Ordinal)
                        .ToList();
                    return new ReportApplication
                    {
                        Project = p.Id,
                        Name = p.Name,
                        Kind = p.Kind,
                        FrameworkClass = p.FrameworkClass,
                        Status = Status(p.Id, remaining),
                        Closure = closure.Count,
                        Remaining = remaining.Count,
                        RemainingLoc = remaining.Sum(id => byId[id].Loc),
                        Next = [.. remaining.Where(id => standings[id].Readiness == ProjectReadiness.Ready)],
                    };
                }),
        ];
    }

    private static ProjectReadiness Status(string application, List<string> remaining) => remaining switch
    {
        [] => ProjectReadiness.Done,
        [var only] when only == application => ProjectReadiness.Ready,
        _ => ProjectReadiness.Blocked,
    };

    private static HashSet<string> Closure(string start, ILookup<string, string> dependencies)
    {
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

    private static SortedDictionary<string, ClassTotals> Complete(SortedDictionary<string, ClassTotals> totals)
    {
        var result = new SortedDictionary<string, ClassTotals>(StringComparer.Ordinal);
        foreach (var name in Classes)
        {
            result[name] = totals.TryGetValue(name, out var value) ? value : new ClassTotals(0, 0);
        }

        return result;
    }

    internal static string Wire(FrameworkClass value)
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
