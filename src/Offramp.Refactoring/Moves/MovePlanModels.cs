using System.Text.Json.Serialization;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Moves;

/// <summary>One file the plan moves, and the hash it must still have when the plan is applied.</summary>
public sealed record PlannedMove
{
    public required string File { get; init; }

    public required string To { get; init; }

    /// <summary>The file whose move required this one (co-move closure, a partial type, or a resource pair); null when asked for.</summary>
    public string? CoMoveOf { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>The other moved files this one needs in the destination; <c>move apply</c> never verifies it without them.</summary>
    public IReadOnlyList<string> Needs { get; init; } = [];
}

/// <summary>A file left where it is, with the diagnostic that says why.</summary>
public sealed record ExcludedMove
{
    public required string File { get; init; }

    public required string Code { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<string> Details { get; init; } = [];
}

/// <summary>A reference the move would need that closes a cycle: the destination back to itself.</summary>
public sealed record PlanCycle(string File, IReadOnlyList<string> Path);

/// <summary>
/// A move plan (<c>schemas/v1/move-plan.json</c>): deterministic and reviewable; agents may
/// remove entries before <c>move apply</c>. Project edits are descriptions, applied to the
/// project files as they are at apply time.
/// </summary>
public sealed record MovePlanDocument
{
    public const string SchemaUri = "https://offramp.dev/schemas/v1/move-plan.json";

    [JsonPropertyName("$schema")]
    [JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaUri;

    [JsonPropertyOrder(-1)]
    public int Version { get; init; } = 1;

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>The migration target (<c>net10.0</c>).</summary>
    public required string Target { get; init; }

    /// <summary>The workspace model the plan was made from; apply refuses a different one (<c>OFR0002</c>) unless forced.</summary>
    public required string WorkspaceHash { get; init; }

    public required IReadOnlyList<PlannedMove> Moves { get; init; }

    public required IReadOnlyList<ProjectEdit> ProjectEdits { get; init; }

    public required IReadOnlyList<ExcludedMove> Excluded { get; init; }

    public required IReadOnlyList<PlanCycle> Cycles { get; init; }

    /// <summary>The verification policy apply uses unless told otherwise (<c>move.verify</c>).</summary>
    public required string Verify { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp move plan</c> (<c>schemas/v1/move-plan-result.json</c>).</summary>
public sealed record MovePlanResult
{
    public required MovePlanDocument Plan { get; init; }

    /// <summary>The plan file written with <c>--out</c>, repository-relative, or null.</summary>
    public string? Output { get; init; }

    /// <summary>The unified diff of project files and the renames the plan would make.</summary>
    public required string Preview { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp move apply</c> (<c>schemas/v1/move-apply.json</c>).</summary>
public sealed record MoveApplyResult
{
    /// <summary>The plan applied, repository-relative.</summary>
    public required string Plan { get; init; }

    public required bool Applied { get; init; }

    /// <summary>The journal, repository-relative; <c>move rollback --journal</c> undoes it.</summary>
    public string? Journal { get; init; }

    /// <summary>Files moved (after any rollback: none).</summary>
    public required IReadOnlyList<string> Moved { get; init; }

    /// <summary>Planned files that changed since the plan and were left (<c>OFR2150</c>).</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>The project files edited, repository-relative.</summary>
    public required IReadOnlyList<string> Edited { get; init; }

    public bool RolledBack { get; init; }

    /// <summary>True when an interrupted journal was completed (<c>--resume</c>).</summary>
    public bool Resumed { get; init; }

    /// <summary>One entry per verification run, in order (batch policies verify several times).</summary>
    public IReadOnlyList<VerifyResult> Verifications { get; init; } = [];
}
