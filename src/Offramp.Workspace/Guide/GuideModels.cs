using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Workspace.Guide;

/// <summary>What running a step's command writes.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<GuideWrites>))]
public enum GuideWrites
{
    /// <summary>Nothing (read-only).</summary>
    Nothing,

    /// <summary>Only Offramp's state directory.</summary>
    State,

    /// <summary><c>offramp.yml</c> and <c>.gitignore</c>, as <c>init</c> always does.</summary>
    Config,

    /// <summary>Project files or code: a dry run unless <c>--apply</c>.</summary>
    Repository,
}

[JsonConverter(typeof(CamelCaseEnumConverter<GuideStepStatus>))]
public enum GuideStepStatus
{
    Done,
    Skipped,
    Open,

    /// <summary>Waiting on a required step, an earlier stage, or the workspace model.</summary>
    Blocked,

    /// <summary>The fact that makes the step useful is false for this repository.</summary>
    NotApplicable,
}

[JsonConverter(typeof(CamelCaseEnumConverter<GuideStageStatus>))]
public enum GuideStageStatus
{
    /// <summary>No step is open or blocked.</summary>
    Done,

    /// <summary>The first stage with an open step.</summary>
    Current,

    Upcoming,
}

/// <summary>What the progress file remembers about a step (and project).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<GuideRecordStatus>))]
public enum GuideRecordStatus
{
    Done,
    Skipped,

    /// <summary>A dry run was shown; nothing changed yet.</summary>
    Previewed,

    /// <summary>The last run exited with a failure.</summary>
    Failed,
}

/// <summary>One entry of the progress file: a step, optionally for one project.</summary>
public sealed record GuideRecord
{
    public required string Step { get; init; }

    /// <summary>The project for a step done per project; null for the step as a whole.</summary>
    public string? Project { get; init; }

    public required GuideRecordStatus Status { get; init; }

    /// <summary>The command's exit code when the guide ran it; null when recorded by hand.</summary>
    public int? ExitCode { get; init; }
}

/// <summary><c>&lt;state&gt;/guide.json</c> (<c>schemas/v1/guide-state.json</c>).</summary>
public sealed record GuideState
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/guide-state.json";

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaUri;

    [JsonPropertyOrder(-1)]
    public int Version { get; init; } = 1;

    /// <summary>Sorted by step, then project (null first); one per step and project.</summary>
    public IReadOnlyList<GuideRecord> Records { get; init; } = [];

    public GuideRecord? Find(string step, string? project) =>
        Records.FirstOrDefault(r => r.Step == step && r.Project == project);

    /// <summary>
    /// Adds or replaces the record for its step and project. A record for a whole step
    /// replaces the step's per-project records too, so it covers every project.
    /// </summary>
    public GuideState With(GuideRecord record) => this with
    {
        Records = Sorted(Records
            .Where(r => r.Step != record.Step || (record.Project is not null && r.Project != record.Project))
            .Append(record)),
    };

    /// <summary>Forgets a step's records: one project's, or with a null project all of them.</summary>
    public GuideState Without(string step, string? project) => this with
    {
        Records = Sorted(Records.Where(r => r.Step != step || (project is not null && r.Project != project))),
    };

    private static List<GuideRecord> Sorted(IEnumerable<GuideRecord> records) =>
        [.. records.OrderBy(r => r.Step, StringComparer.Ordinal).ThenBy(r => r.Project ?? "", StringComparer.Ordinal)];
}

/// <summary>A candidate project of a step done per project.</summary>
public sealed record GuideProjectReport
{
    public required string Project { get; init; }

    /// <summary><c>done</c>, <c>skipped</c>, or <c>open</c>.</summary>
    public required GuideStepStatus Status { get; init; }

    public required string Command { get; init; }

    public string? Note { get; init; }
}

public sealed record GuideStepReport
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Why the step matters, for someone new to the migration.</summary>
    public required string Why { get; init; }

    /// <summary>The command, with <c>{project}</c> for steps done per project.</summary>
    public required string Command { get; init; }

    public required GuideWrites Writes { get; init; }

    public required IReadOnlyList<string> Requires { get; init; }

    public required GuideStepStatus Status { get; init; }

    public string? Note { get; init; }

    /// <summary>Candidates of a step done per project, sorted by path; empty otherwise.</summary>
    public IReadOnlyList<GuideProjectReport> Projects { get; init; } = [];
}

public sealed record GuideStageReport
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required GuideStageStatus Status { get; init; }

    public required IReadOnlyList<GuideStepReport> Steps { get; init; }
}

public sealed record GuideCounts(int Steps, int Done, int Skipped, int Open, int Blocked, int NotApplicable);

/// <summary>A step the guide ran.</summary>
public sealed record GuideRun
{
    public required string Step { get; init; }

    public string? Project { get; init; }

    /// <summary>The command as typed, <c>--apply</c> included when it was applied.</summary>
    public required string Command { get; init; }

    public required int ExitCode { get; init; }

    /// <summary><c>done</c>, <c>previewed</c>, or <c>failed</c>.</summary>
    public required GuideRecordStatus Outcome { get; init; }

    /// <summary>The command's JSON envelope when the guide ran with <c>--json</c>.</summary>
    public JsonNode? Envelope { get; init; }
}

/// <summary>Where the migration stands: every stage and step with its status, and the choices.</summary>
public sealed record GuideStatus
{
    public required IReadOnlyList<GuideStageReport> Stages { get; init; }

    /// <summary>The first stage with an open step; null when nothing is open.</summary>
    public string? Stage { get; init; }

    /// <summary>The current stage's open steps, in catalog order.</summary>
    public required IReadOnlyList<string> Next { get; init; }

    public required GuideCounts Counts { get; init; }

    public GuideStepReport Step(string id) => Stages.SelectMany(s => s.Steps).Single(s => s.Id == id);
}

/// <summary>The <c>result</c> of <c>offramp guide</c> (<c>schemas/v1/guide.json</c>).</summary>
public sealed record GuideResult
{
    public required string StateFile { get; init; }

    /// <summary>This run created the progress file.</summary>
    public required bool Started { get; init; }

    /// <summary>A session ran on a terminal.</summary>
    public required bool Interactive { get; init; }

    public required string Target { get; init; }

    public required IReadOnlyList<GuideRun> Ran { get; init; }

    public string? Stage { get; init; }

    public required IReadOnlyList<string> Next { get; init; }

    public required IReadOnlyList<GuideStageReport> Stages { get; init; }

    public required GuideCounts Counts { get; init; }
}
