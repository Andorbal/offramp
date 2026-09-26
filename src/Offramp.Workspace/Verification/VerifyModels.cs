using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Workspace.Verification;

[JsonConverter(typeof(CamelCaseEnumConverter<VerifyMode>))]
public enum VerifyMode
{
    Build,
    Command,
    None,
}

[JsonConverter(typeof(CamelCaseEnumConverter<VerifyStatus>))]
public enum VerifyStatus
{
    Passed,
    Failed,
    TimedOut,
    Skipped,
}

[JsonConverter(typeof(CamelCaseEnumConverter<VerifyProjectStatus>))]
public enum VerifyProjectStatus
{
    Passed,
    Failed,

    /// <summary>The run failed without an error in this project: a dependency failed, the build stopped, or it timed out.</summary>
    NotVerified,
}

/// <summary>One error from the build or the verification command, with repository-relative paths.</summary>
public sealed record VerifyError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? Project { get; init; }

    public string? File { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }
}

/// <summary>Errors sharing a code: how many, and the first one in path order.</summary>
public sealed record VerifyErrorGroup(string Code, int Count, VerifyError First);

public sealed record VerifyProject(string Project, VerifyProjectStatus Status, int Errors);

/// <summary>One process Offramp ran to verify.</summary>
public sealed record VerifyInvocation
{
    /// <summary>The command line, with repository-relative paths.</summary>
    public required string CommandLine { get; init; }

    /// <summary>The binary log written by a build, repository-relative, or null.</summary>
    public string? Binlog { get; init; }

    public required int ExitCode { get; init; }

    public required bool TimedOut { get; init; }
}

/// <summary>How the errors compare with the recorded baseline.</summary>
public sealed record VerifyBaselineComparison
{
    /// <summary>The baseline file, repository-relative.</summary>
    public required string Path { get; init; }

    /// <summary>Errors the baseline lists that still occur.</summary>
    public required int Known { get; init; }

    /// <summary>Errors the baseline does not list.</summary>
    public required int New { get; init; }

    /// <summary>Baseline errors that no longer occur.</summary>
    public required int Fixed { get; init; }

    /// <summary>Codes among the new errors that the baseline has no error for (<c>OFR5010</c>).</summary>
    public required IReadOnlyList<string> NewCodes { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp verify</c> (<c>schemas/v1/verify.json</c>).</summary>
public sealed record VerifyResult
{
    public required VerifyMode Mode { get; init; }

    public required VerifyStatus Status { get; init; }

    public bool Passed => Status is VerifyStatus.Passed or VerifyStatus.Skipped;

    /// <summary>What was verified: every project, or the selection and why it was chosen.</summary>
    public required string Scope { get; init; }

    public required IReadOnlyList<VerifyProject> Projects { get; init; }

    /// <summary>Errors that count (all of them without a baseline; the new ones with one), grouped by code.</summary>
    public required IReadOnlyList<VerifyErrorGroup> Errors { get; init; }

    public required int ErrorCount { get; init; }

    /// <summary>The comparison with the baseline (the one just recorded, with <c>--baseline</c>), or null without one.</summary>
    public VerifyBaselineComparison? Baseline { get; init; }

    /// <summary>The baseline file written by <c>--baseline</c>, or null.</summary>
    public string? BaselineRecorded { get; init; }

    public required IReadOnlyList<VerifyInvocation> Invocations { get; init; }

    /// <summary>The last lines of a failed verification command's output (command mode), else empty.</summary>
    public IReadOnlyList<string> OutputTail { get; init; } = [];
}

/// <summary>The recorded error set (<c>.offramp/verify/baseline.json</c>), compared without line numbers so edits elsewhere do not invalidate it.</summary>
public sealed record VerifyBaseline
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/verify-baseline.json";

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaUri;

    [JsonPropertyOrder(-1)]
    public int Version { get; init; } = 1;

    public required IReadOnlyList<VerifyBaselineError> Errors { get; init; }
}

public sealed record VerifyBaselineError(string Code, string? Project, string? File, string Message);
