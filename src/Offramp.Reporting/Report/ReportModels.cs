using System.Text.Json.Serialization;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

namespace Offramp.Reporting.Report;

[JsonConverter(typeof(CamelCaseEnumConverter<ReportFormat>))]
public enum ReportFormat
{
    Html,
    Json,
    Markdown,
}

/// <summary>One point of the burn-down: a ledger snapshot's totals.</summary>
public sealed record ReportPoint
{
    /// <summary>The snapshot's creation time (UTC, ISO 8601).</summary>
    public required string CreatedAt { get; init; }

    public required int Projects { get; init; }

    public required int Loc { get; init; }

    /// <summary>Always the four classes, zeros included, so every point has the same shape.</summary>
    public required SortedDictionary<string, ClassTotals> ByFrameworkClass { get; init; }

    public required SortedDictionary<string, int> ByKind { get; init; }
}

/// <summary>Projects and lines by framework class for one area: the directory holding project folders, as in <c>graph</c>.</summary>
public sealed record ReportArea(string Area, int Projects, int Loc, SortedDictionary<string, ClassTotals> ByFrameworkClass);

/// <summary>An application and what must be ported before it runs on the target.</summary>
public sealed record ReportApplication
{
    public required string Project { get; init; }

    public required string Name { get; init; }

    public required ProjectKind Kind { get; init; }

    public required FrameworkClass FrameworkClass { get; init; }

    /// <summary>done: nothing framework-only in its closure; ready: only the application itself remains; blocked: otherwise.</summary>
    public required ProjectReadiness Status { get; init; }

    /// <summary>The application and every project it depends on.</summary>
    public required int Closure { get; init; }

    /// <summary>Framework-only projects in the closure, the application included.</summary>
    public required int Remaining { get; init; }

    public required int RemainingLoc { get; init; }

    /// <summary>Framework-only projects in the closure that can be ported today, sorted.</summary>
    public required IReadOnlyList<string> Next { get; init; }
}

/// <summary>A framework-only project that can be ported today, and how many projects depend on it.</summary>
public sealed record ReportFrontierProject(string Project, string Name, int Loc, int Dependents);

public sealed record ReportHeadline
{
    public required int Projects { get; init; }

    public required int Loc { get; init; }

    public required int FrameworkProjects { get; init; }

    public required int FrameworkLoc { get; init; }

    /// <summary>Lines in standard, modern, and dual projects as a share of all lines (0 to 100, one decimal).</summary>
    public required double PortablePercent { get; init; }

    /// <summary>Framework-class lines now minus at the first point of the series; negative is progress.</summary>
    public required int FrameworkLocChange { get; init; }

    public required int Applications { get; init; }

    public required int ApplicationsDone { get; init; }

    /// <summary>Framework-only projects that can be ported today (the frontier).</summary>
    public required int Ready { get; init; }
}

/// <summary>The data behind every rendering of the report (<c>report --format json</c>).</summary>
public sealed record ReportData
{
    public required string Title { get; init; }

    /// <summary>When the workspace model was built; the report never reads the clock.</summary>
    public required string AsOf { get; init; }

    /// <summary>The <c>--since</c> bound as given, or null.</summary>
    public string? Since { get; init; }

    public required ReportHeadline Headline { get; init; }

    /// <summary>Ledger snapshots in range, oldest first, ending with the current model.</summary>
    public required IReadOnlyList<ReportPoint> Series { get; init; }

    public required IReadOnlyList<ReportArea> Areas { get; init; }

    public required IReadOnlyList<ReportApplication> Applications { get; init; }

    public required IReadOnlyList<ReportFrontierProject> Frontier { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp report</c> (<c>schemas/v1/report.json</c>).</summary>
public sealed record ReportResult
{
    /// <summary>The rendering format, or null when only the data was requested (the envelope carries it).</summary>
    public ReportFormat? Format { get; init; }

    /// <summary>Repository-relative path of the written file, or null.</summary>
    public string? Output { get; init; }

    public required ReportData Report { get; init; }

    /// <summary>The rendering when it was not written to a file, else null.</summary>
    public string? Content { get; init; }
}
