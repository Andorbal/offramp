using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.Moves;

namespace Offramp.Ide;

/// <summary>
/// The editor's settings (docs/spec/commands/ide.md#configuration): what the shell sends as the
/// server's initialization options, or what <c>ide check</c> takes as flags. A null value leaves
/// <c>offramp.yml</c>'s in effect.
/// </summary>
public sealed record IdeSettings
{
    /// <summary><c>auto</c>, <c>on</c>, or <c>off</c>.</summary>
    public string Enabled { get; init; } = "auto";

    /// <summary>Entries consulted before <c>offramp.yml</c>'s <c>projectMap</c>.</summary>
    public IReadOnlyList<ProjectMapEntry> ProjectMap { get; init; } = [];

    public IdeNewCodeSettings NewCode { get; init; } = new();

    public bool? ImplicitCounterparts { get; init; }

    public bool CodeLens { get; init; } = true;

    /// <summary>True when the shell registers the <c>offramp.*</c> commands itself (the server then does not list them).</summary>
    public bool ClientCommands { get; init; }
}

public sealed record IdeNewCodeSettings
{
    public string? Base { get; init; }

    public string? Scope { get; init; }
}

/// <summary>Whether an editor would show anything in the repository, and why.</summary>
/// <param name="Mode"><c>auto</c>, <c>on</c>, or <c>off</c>.</param>
/// <param name="Enabled">The outcome.</param>
/// <param name="Reason"><c>setting-on</c>, <c>setting-off</c>, <c>state-directory</c> (auto, <c>.offramp/</c> exists), or <c>no-state-directory</c>.</param>
public sealed record IdeEnablement(string Mode, bool Enabled, string Reason);

/// <summary>What counts as new code: the ref asked for, the commit compared with (null outside git), and the scope.</summary>
public sealed record IdeBase(string Ref, string? Commit, string Scope);

/// <summary>A .NET Framework-only project and the portable projects its code could move to.</summary>
public sealed record ProjectCounterparts
{
    public required string Project { get; init; }

    public required IReadOnlyList<string> Counterparts { get; init; }

    /// <summary><c>map</c> (a project map entry), <c>referenced</c> (implicit), or <c>none</c>.</summary>
    public required string Source { get; init; }
}

/// <summary>A finding of the <c>audit api</c> rules on a new line.</summary>
public sealed record IdeFinding
{
    public required string Code { get; init; }

    public required Severity Severity { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    public required int EndLine { get; init; }

    public required int EndColumn { get; init; }

    public required string Symbol { get; init; }

    public required string Message { get; init; }

    public required string Recommendation { get; init; }

    public SortedDictionary<string, string> Details { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>A top-level type a file declares; the position is its name's.</summary>
public sealed record IdeType
{
    public required string Name { get; init; }

    /// <summary><c>class</c>, <c>record</c>, <c>struct</c>, <c>interface</c>, <c>enum</c>, or <c>delegate</c>.</summary>
    public required string Kind { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    public required int EndLine { get; init; }

    public required int EndColumn { get; init; }

    /// <summary>True when the name is on a new line.</summary>
    public required bool New { get; init; }
}

/// <summary>What an editor shows in one file (docs/spec/commands/ide.md#offramp-ide-check). Positions are 1-based.</summary>
public sealed record IdeFileReport
{
    public required string File { get; init; }

    /// <summary>The project that compiles the file, or null (OFR6006).</summary>
    public string? Project { get; init; }

    /// <summary>True for a C# file of a .NET Framework-only project.</summary>
    public required bool Applies { get; init; }

    /// <summary>New lines as inclusive ranges, sorted.</summary>
    public IReadOnlyList<IReadOnlyList<int>> NewLines { get; init; } = [];

    public IReadOnlyList<IdeFinding> Findings { get; init; } = [];

    public IReadOnlyList<IdeType> Types { get; init; } = [];

    /// <summary>One entry per counterpart, in counterpart order.</summary>
    public IReadOnlyList<MoveAssessment> Moves { get; init; } = [];

    /// <summary>The type a move lens sits on: the one named like the file, else the first; null when no move is possible.</summary>
    public IdeType? LensType() =>
        Moves.Any(m => m.Movable) && Types.Count > 0
            ? Types.FirstOrDefault(t => string.Equals(ShortName(t.Name), Path.GetFileNameWithoutExtension(File).Split('.')[0], StringComparison.Ordinal)) ?? Types[0]
            : null;

    private static string ShortName(string name)
    {
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        var bare = generic < 0 ? name : name[..generic];
        return bare[(bare.LastIndexOf('.') + 1)..];
    }
}

public sealed record IdeSummary
{
    public required int Files { get; init; }

    public required int NewLines { get; init; }

    public required int Findings { get; init; }

    /// <summary>New types in files that can move (OFR6001).</summary>
    public required int NewMovableTypes { get; init; }

    /// <summary>Files that can move to at least one counterpart.</summary>
    public required int MovableFiles { get; init; }
}

/// <summary>The <c>result</c> of <c>offramp ide check</c> (<c>schemas/v1/ide-check.json</c>).</summary>
public sealed record IdeCheckResult
{
    public required IdeBase Base { get; init; }

    public required IdeEnablement Enablement { get; init; }

    public required IReadOnlyList<ProjectCounterparts> Counterparts { get; init; }

    public required IReadOnlyList<IdeFileReport> Files { get; init; }

    public required IdeSummary Summary { get; init; }
}

/// <summary>The result of the <c>offramp.move</c> command: the plan, and what applying it did (null when it was not applied).</summary>
public sealed record IdeMoveResult
{
    public required string File { get; init; }

    public required string To { get; init; }

    public MovePlanDocument? Plan { get; init; }

    public MoveApplyResult? Apply { get; init; }

    /// <summary>Why nothing was moved, or null.</summary>
    public string? Refused { get; init; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}
