using System.Text.Json.Serialization;
using Offramp.Core.Json;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Codemods;

/// <summary>A package a codemod adds (docs/spec/commands/codemod.md), for <c>all</c> targets, <c>framework</c> (.NET Framework) targets, or <c>modern</c> ones.</summary>
public sealed record CodemodPackageInfo(string Id, string Version, string Targets);

/// <summary>One codemod of the catalog, as <c>codemod list</c> shows it.</summary>
public sealed record CodemodInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>Changes behavior on purpose: runs only when named.</summary>
    public bool OptIn { get; init; }

    /// <summary>Needs <c>--experimental</c>.</summary>
    public bool Experimental { get; init; }

    /// <summary>False when the codemod's change is a package reference and no code is rewritten.</summary>
    public bool RewritesCode { get; init; }

    public IReadOnlyList<CodemodPackageInfo> Packages { get; init; } = [];
}

/// <summary>The result of <c>offramp codemod list</c>.</summary>
public sealed record CodemodListResult
{
    public IReadOnlyList<CodemodInfo> Codemods { get; init; } = [];
}

/// <summary>A site a codemod reported: rewritten, or skipped with the reason.</summary>
public sealed record CodemodSite
{
    /// <summary>The codemod's short name.</summary>
    public required string Codemod { get; init; }

    public required string File { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    public required CodemodSiteOutcome Outcome { get; init; }

    /// <summary>Why the site is skipped (<c>OFR4501</c>); null otherwise.</summary>
    public string? Reason { get; init; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<CodemodSiteOutcome>))]
public enum CodemodSiteOutcome
{
    /// <summary>The fixer rewrote the site.</summary>
    Rewritten,

    /// <summary>Left as it is, with the reason.</summary>
    Skipped,

    /// <summary>A codemod whose change is a package reference: the code stays, the package is added.</summary>
    Referenced,
}

/// <summary>A package reference a codemod needs, added to a project (or its central versions file).</summary>
public sealed record CodemodPackageEdit
{
    public required string Codemod { get; init; }

    public required string Id { get; init; }

    public required string Version { get; init; }

    /// <summary>The MSBuild condition of the reference, for a package only .NET Framework targets need.</summary>
    public string? Condition { get; init; }

    /// <summary>The files edited: the project, and the central versions file under central package management.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>A project property set from an attribute the SDK now generates (<c>assemblyinfo</c>).</summary>
public sealed record CodemodPropertyEdit(string Name, string Value);

/// <summary>What the codemods do to one project.</summary>
public sealed record CodemodProjectResult
{
    public required string Project { get; init; }

    /// <summary>The target framework whose recorded compilation was analyzed; null in format mode.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Source files the codemods change, repository-relative, sorted.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Every site, in file, line, column, and codemod order.</summary>
    public IReadOnlyList<CodemodSite> Sites { get; init; } = [];

    public IReadOnlyList<CodemodPackageEdit> Packages { get; init; } = [];

    public IReadOnlyList<CodemodPropertyEdit> Properties { get; init; } = [];
}

/// <summary>Totals across projects.</summary>
public sealed record CodemodSummary(int Projects, int FilesChanged, int SitesRewritten, int SitesSkipped, int PackagesAdded);

[JsonConverter(typeof(CamelCaseEnumConverter<CodemodMode>))]
public enum CodemodMode
{
    /// <summary>Offramp runs the fixers over the recorded compilations.</summary>
    Driver,

    /// <summary><c>dotnet format analyzers</c> runs the fixers from the project's Offramp.Analyzers reference.</summary>
    Format,
}

/// <summary>The result of <c>offramp codemod run</c>.</summary>
public sealed record CodemodRunResult
{
    public required CodemodMode Mode { get; init; }

    /// <summary>The codemods run, by short name, in catalog order.</summary>
    public required IReadOnlyList<string> Codemods { get; init; }

    public IReadOnlyList<CodemodProjectResult> Projects { get; init; } = [];

    public required CodemodSummary Summary { get; init; }

    public bool Applied { get; init; }

    /// <summary>The journal of the applied change set (driver mode).</summary>
    public string? Journal { get; init; }

    /// <summary>The unified diff of a dry run (driver mode).</summary>
    public string? Preview { get; init; }

    public VerifyResult? Verify { get; init; }

    /// <summary>True when verification failed and the change set was rolled back (<c>OFR4507</c>).</summary>
    public bool RolledBack { get; init; }
}
