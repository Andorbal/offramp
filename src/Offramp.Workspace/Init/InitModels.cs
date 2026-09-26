using Offramp.Core.Configuration;

namespace Offramp.Workspace.Init;

/// <summary>The values <c>init</c> writes; detected, then optionally edited in the interview.</summary>
public sealed record InitValues
{
    public int Target { get; init; } = 10;

    public string? Solution { get; init; }

    public string VerifyMode { get; init; } = "build";

    public string CpmFile { get; init; } = "Directory.Packages.props";

    public IReadOnlyList<PackagePin> Pins { get; init; } = [];
}

/// <summary>What <c>init</c> found in the repository before asking anything.</summary>
public sealed record InitDetection
{
    public required InitValues Values { get; init; }

    /// <summary>Every <c>.sln</c>, <c>.slnx</c>, and <c>.slnf</c> found, repository-relative and sorted.</summary>
    public IReadOnlyList<string> SolutionCandidates { get; init; } = [];

    public bool ConfigExists { get; init; }
}

public sealed record GitignoreChange
{
    public required string File { get; init; }

    /// <summary>Entries that were (or with <c>--dry-run</c> would be) appended.</summary>
    public IReadOnlyList<string> Added { get; init; } = [];

    /// <summary>Entries already present.</summary>
    public IReadOnlyList<string> Present { get; init; } = [];
}

/// <summary>The <c>result</c> of <c>offramp init</c> (<c>schemas/v1/init.json</c>).</summary>
public sealed record InitResult
{
    /// <summary>Repository-relative path of the configuration file.</summary>
    public required string ConfigFile { get; init; }

    /// <summary>True when the file was written in this run.</summary>
    public required bool Written { get; init; }

    public required bool DryRun { get; init; }

    /// <summary>True when an existing file was replaced (<c>--force</c>).</summary>
    public bool Replaced { get; init; }

    public required bool Interactive { get; init; }

    public required InitValues Values { get; init; }

    public IReadOnlyList<string> SolutionCandidates { get; init; } = [];

    public required GitignoreChange Gitignore { get; init; }

    /// <summary>The YAML that was (or would be) written.</summary>
    public required string Content { get; init; }

    /// <summary>The compile-only block, when the interview offered it (the model has Windows-only build steps).</summary>
    public Doctor.CompileOnlyFix? CompileOnlyFix { get; init; }
}
