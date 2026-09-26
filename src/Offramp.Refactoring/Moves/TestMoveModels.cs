using System.Text.Json.Serialization;
using Offramp.Analysis.TestCode;
using Offramp.Core.Json;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Moves;

/// <summary>A file the move takes, and where.</summary>
public sealed record MovedFile
{
    public required string File { get; init; }

    public required string To { get; init; }

    public required TestFileKind Kind { get; init; }

    public required TestConfidence Confidence { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];
}

/// <summary>A file that looked like test code but stays, with the diagnostic that says why.</summary>
public sealed record SkippedFile
{
    public required string File { get; init; }

    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>For example the first compiler errors in the destination.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];
}

/// <summary>A medium-confidence helper below <c>--include-helpers</c>, listed for review and not moved.</summary>
public sealed record CandidateFile(string File, TestConfidence Confidence, IReadOnlyList<string> Reasons);

[JsonConverter(typeof(CamelCaseEnumConverter<ProjectEditKind>))]
public enum ProjectEditKind
{
    CreateProject,
    AddToSolution,
    AddProjectReference,
    AddPackageReference,
    AddPackageVersion,
    RemovePackageReference,
    AddInternalsVisibleTo,
    AddCompile,
    RemoveCompile,
}

/// <summary>One change to a project or solution file (the file itself is edited, never a moved file).</summary>
public sealed record ProjectEdit
{
    /// <summary>The project or solution file edited (or created).</summary>
    public required string Project { get; init; }

    public required ProjectEditKind Kind { get; init; }

    /// <summary>A package id, an assembly name, or a project or file path, depending on the kind.</summary>
    public string? Value { get; init; }

    public string? Version { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp move tests</c> (<c>schemas/v1/move-tests.json</c>).</summary>
public sealed record MoveTestsResult
{
    public required string Project { get; init; }

    /// <summary>The destination test project, or null when none could be chosen.</summary>
    public string? To { get; init; }

    public bool Created { get; init; }

    /// <summary>The test framework the moved tests use (the most used one when several).</summary>
    public string? Framework { get; init; }

    public required IReadOnlyList<MovedFile> Moves { get; init; }

    public required IReadOnlyList<SkippedFile> Skipped { get; init; }

    public required IReadOnlyList<CandidateFile> Candidates { get; init; }

    public required IReadOnlyList<ProjectEdit> ProjectEdits { get; init; }

    /// <summary>Test-framework packages the source no longer needs (<c>OFR2210</c>).</summary>
    public required IReadOnlyList<string> Prunable { get; init; }

    public bool Applied { get; init; }

    /// <summary>The journal of an applied move, repository-relative; <c>move rollback --journal</c> undoes it.</summary>
    public string? Journal { get; init; }

    /// <summary>A failed verification undid the move.</summary>
    public bool RolledBack { get; init; }

    /// <summary>The dry run's unified diff of project files and list of renames; null once applied.</summary>
    public string? Preview { get; init; }

    public VerifyResult? Verify { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp move rollback</c> (<c>schemas/v1/move-rollback.json</c>).</summary>
public sealed record MoveRollbackResult
{
    public required string Journal { get; init; }

    /// <summary>The command that wrote the journal.</summary>
    public required string Command { get; init; }

    /// <summary>Steps undone: renames reversed, files restored, new files deleted.</summary>
    public required int Undone { get; init; }

    /// <summary>Files changed since the move, which stopped the rollback (<c>OFR2151</c>); empty when it ran.</summary>
    public required IReadOnlyList<string> Changed { get; init; }
}
