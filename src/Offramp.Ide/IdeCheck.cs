using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;

namespace Offramp.Ide;

/// <summary>
/// <c>offramp ide check</c> (docs/spec/commands/ide.md#offramp-ide-check): file reports for the
/// files asked for, or for every C# file changed since the new-code base, with each finding on
/// new code and each new type that could move reported as a diagnostic.
/// </summary>
public static class IdeCheck
{
    public static async Task<IdeCheckResult> RunAsync(IdeEngine engine, IReadOnlyList<string>? files, DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        diagnostics.AddRange(engine.Diagnostics.ToSortedList());
        var selected = files is { Count: > 0 }
            ? files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : [.. (await engine.NewCode.ChangedFilesAsync(cancellationToken)).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !LiveWorkspace.IsGenerated("/" + f))];
        var reports = new List<IdeFileReport>();
        var withoutCounterpart = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = await engine.ReportAsync(file, cancellationToken);
            reports.Add(report);
            if (report.Project is null)
            {
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && engine.Workspace.CurrentText(file) is not null)
                {
                    diagnostics.Report(DiagnosticCatalog.OFR6006, $"{file} is in no project of the workspace model; run `offramp scan` if its project is new.", new DiagnosticLocation(null, file));
                }

                continue;
            }

            if (!report.Applies)
            {
                continue;
            }

            foreach (var finding in report.Findings)
            {
                if (DiagnosticCatalog.Find(finding.Code) is { } descriptor)
                {
                    diagnostics.Report(descriptor, finding.Message, new DiagnosticLocation(report.Project, report.File, finding.Line, finding.Column),
                        [KeyValuePair.Create<string, JsonNode?>("symbol", finding.Symbol)], finding.Severity);
                }
            }

            if (engine.CounterpartsOf(report.Project).Count == 0 && report.NewLines.Count > 0)
            {
                withoutCounterpart.Add(report.Project);
            }

            foreach (var type in NewMovableTypes(report))
            {
                diagnostics.Report(DiagnosticCatalog.OFR6001, Message(report, type), new DiagnosticLocation(report.Project, report.File, type.Line, type.Column),
                    [KeyValuePair.Create<string, JsonNode?>("counterparts", new JsonArray([.. report.Moves.Where(m => m.Movable).Select(m => (JsonNode?)m.To)]))]);
            }
        }

        foreach (var project in withoutCounterpart)
        {
            diagnostics.Report(DiagnosticCatalog.OFR6005,
                $"{project} has new code but no counterpart to suggest moving it to; add a projectMap entry for it.", new DiagnosticLocation(project));
        }

        return new IdeCheckResult
        {
            Base = engine.NewCode.Base,
            Enablement = engine.Enablement,
            Counterparts = engine.Counterparts,
            Files = reports,
            Summary = new IdeSummary
            {
                Files = reports.Count,
                NewLines = reports.Sum(r => r.NewLines.Sum(range => range[1] - range[0] + 1)),
                Findings = reports.Sum(r => r.Findings.Count),
                NewMovableTypes = reports.Sum(r => NewMovableTypes(r).Count()),
                MovableFiles = reports.Count(r => r.Moves.Any(m => m.Movable)),
            },
        };
    }

    /// <summary>The new types of a file that can move (OFR6001).</summary>
    public static IEnumerable<IdeType> NewMovableTypes(IdeFileReport report) =>
        report.Moves.Any(m => m.Movable) ? report.Types.Where(t => t.New) : [];

    /// <summary>The OFR6001 message for a new type.</summary>
    public static string Message(IdeFileReport report, IdeType type)
    {
        var name = ShortName(type.Name);
        var file = Path.GetFileName(report.File);
        var targets = report.Moves.Where(m => m.Movable).ToList();
        var first = targets[0];
        var where = ProjectName(first.To) + (first.Referenced ? $", which {ProjectName(report.Project!)} references" : "")
            + (targets.Count > 1 ? $" (or {string.Join(", ", targets.Skip(1).Select(t => ProjectName(t.To)))})" : "");
        return $"{name} needs nothing from .NET Framework: {file} can move to {where}, and would not need migrating.";
    }

    public static string ProjectName(string project) => Path.GetFileNameWithoutExtension(project);

    /// <summary>A type's name without namespace or type parameters: <c>Foo.Box&lt;T&gt;</c> is <c>Box</c>.</summary>
    public static string ShortName(string name)
    {
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        var bare = generic < 0 ? name : name[..generic];
        return bare[(bare.LastIndexOf('.') + 1)..];
    }
}
