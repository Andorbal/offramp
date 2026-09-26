using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Core.Progress;

namespace Offramp.Workspace.Scanning;

public sealed record ScanRequest
{
    public required string RepositoryRoot { get; init; }

    public required OfframpConfig Config { get; init; }

    /// <summary>Absolute path where the model is written.</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>Absolute path of a binary log to ingest instead of building.</summary>
    public string? BinlogPath { get; init; }

    /// <summary>Absolute path of a compiler log (alone, or with <see cref="BinlogPath"/>).</summary>
    public string? ComplogPath { get; init; }

    /// <summary>Reuse the previous scan's binary log instead of building.</summary>
    public bool NoBuild { get; init; }

    /// <summary>Only rescan when the existing model is stale.</summary>
    public bool IfStale { get; init; }

    public required IProcessRunner Processes { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed record WindowsOnlyProject(string Project, IReadOnlyList<string> Steps);

public sealed record NotLoadedProject(string Project, string Reason);

/// <summary>The <c>result</c> of <c>offramp scan</c> (<c>schemas/v1/scan.json</c>).</summary>
public sealed record ScanResult
{
    /// <summary>Repository-relative path of the workspace model.</summary>
    public required string Model { get; init; }

    /// <summary>True when <c>--if-stale</c> found the model fresh and nothing was rescanned.</summary>
    public bool UpToDate { get; init; }

    public required WorkspaceSource Source { get; init; }

    public string? Solution { get; init; }

    /// <summary>True or false when the scan built or read a build log; null for a compiler log alone.</summary>
    public bool? BuildSucceeded { get; init; }

    public int Projects { get; init; }

    public int Loc { get; init; }

    public SortedDictionary<string, int> ByFrameworkClass { get; init; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, int> ByKind { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; } = [];

    public IReadOnlyList<WindowsOnlyProject> WindowsOnlyBuildSteps { get; init; } = [];

    /// <summary>Projects whose kind could not be determined.</summary>
    public IReadOnlyList<string> Unrecognized { get; init; } = [];

    public IReadOnlyList<NotLoadedProject> NotLoaded { get; init; } = [];

    /// <summary>Projects with target frameworks that have no compiler call.</summary>
    public IReadOnlyList<string> Partial { get; init; } = [];

    /// <summary>Repository-relative path of the ledger snapshot written by this scan.</summary>
    public string? LedgerSnapshot { get; init; }
}

public enum ScanFailure
{
    None,
    Usage,
    Environment,
}

public sealed record ScanOutcome(ScanResult? Result, WorkspaceModel? Model, ScanFailure Failure);
