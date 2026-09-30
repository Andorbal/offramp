using Offramp.Analysis.DeadCode;
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
    /// <param name="snapshots">Ledger snapshots in any order; only those of the model's solution make the series.</param>
    /// <param name="title">The report's title.</param>
    /// <param name="since">Inclusive lower bound compared with each snapshot's <c>createdAt</c>: a date (<c>2026-09-01</c>) or a UTC timestamp.</param>
    /// <param name="shipped">Which projects other code uses (ADR 0041); without it the report lists no libraries.</param>
    public static ReportData Build(WorkspaceModel model, IReadOnlyList<LedgerSnapshot> snapshots, string title, string? since, ShippedProjects? shipped = null)
    {
        var series = Series(model, snapshots, since);
        var standings = Readiness.Compute(model);
        var applications = Applications(model, standings);
        var libraries = shipped is null ? [] : Libraries(model, standings, shipped);
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
                Libraries = libraries.Count,
                LibrariesDone = libraries.Count(l => l.Status == ProjectReadiness.Done),
                Ready = frontier.Count,
            },
            Series = series,
            Areas = Areas(model),
            Applications = applications,
            Libraries = libraries,
            Frontier = frontier,
        };
    }

    /// <summary>
    /// The snapshots of the model's solution, and the other solutions the rest were taken of (sorted; null for a
    /// snapshot without a solution). A trend across solutions compares different sets of projects: NHibernate's
    /// report said "down 232" after scans of a solution filter and then of the solution.
    /// </summary>
    public static (IReadOnlyList<LedgerSnapshot> Kept, IReadOnlyList<string?> OtherSolutions) OfModelSolution(
        WorkspaceModel model, IReadOnlyList<LedgerSnapshot> snapshots) =>
        ([.. snapshots.Where(s => SameSolution(s, model))],
            [.. snapshots.Where(s => !SameSolution(s, model)).Select(s => s.Solution).Distinct().Order(StringComparer.Ordinal)]);

    private static bool SameSolution(LedgerSnapshot snapshot, WorkspaceModel model) =>
        string.Equals(snapshot.Solution, model.Solution, StringComparison.Ordinal);

    /// <summary>Snapshots of the model's solution at or after <paramref name="since"/> and not newer than the model, then the model itself.</summary>
    private static List<ReportPoint> Series(WorkspaceModel model, IReadOnlyList<LedgerSnapshot> snapshots, string? since)
    {
        var current = Ledger.Snapshot(model);
        var points = OfModelSolution(model, snapshots).Kept
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
        return
        [
            .. model.Projects
                .Where(p => ApplicationKinds.Contains(p.Kind) && !Hosting.IsHosted(p))
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p =>
                {
                    var closure = Hosting.ApplicationClosure(model, p.Id);
                    var remaining = Remaining(closure, byId);
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
                        Hosted = Hosting.HostedProjects(model, p.Id),
                    };
                }),
        ];
    }

    /// <summary>
    /// The libraries (<c>kind: library</c>) other code uses, and what is left in each one's own closure. A project a
    /// web application hosts belongs to that application (ADR 0055), not to the libraries.
    /// </summary>
    private static List<ReportLibrary> Libraries(WorkspaceModel model, IReadOnlyDictionary<string, ProjectStanding> standings, ShippedProjects shipped)
    {
        var byId = model.Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var dependencies = model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        return
        [
            .. model.Projects
                .Where(p => p.Kind == ProjectKind.Library && !Hosting.IsHosted(p) && shipped.Of(p) is not null)
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .Select(p =>
                {
                    var closure = DependencyClosure(p.Id, dependencies);
                    var remaining = Remaining(closure, byId);
                    return new ReportLibrary
                    {
                        Project = p.Id,
                        Name = p.Name,
                        FrameworkClass = p.FrameworkClass,
                        Shipped = shipped.Of(p)!.Reason,
                        Status = Status(p.Id, remaining),
                        Closure = closure.Count,
                        Remaining = remaining.Count,
                        RemainingLoc = remaining.Sum(id => byId[id].Loc),
                        Next = [.. remaining.Where(id => standings[id].Readiness == ProjectReadiness.Ready)],
                    };
                }),
        ];
    }

    /// <summary>The framework-only projects in a closure, sorted.</summary>
    private static List<string> Remaining(HashSet<string> closure, Dictionary<string, ProjectInfo> byId) =>
        [.. closure
            .Where(id => byId.TryGetValue(id, out var d) && d.FrameworkClass == FrameworkClass.Framework)
            .Order(StringComparer.Ordinal)];

    /// <summary>A project and everything it depends on, directly or not.</summary>
    private static HashSet<string> DependencyClosure(string start, ILookup<string, string> dependencies)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([start]);
        while (pending.TryPop(out var id))
        {
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

    private static ProjectReadiness Status(string application, List<string> remaining) => remaining switch
    {
        [] => ProjectReadiness.Done,
        [var only] when only == application => ProjectReadiness.Ready,
        _ => ProjectReadiness.Blocked,
    };

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
