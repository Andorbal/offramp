using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Refactoring.Dependencies.Consolidation;

[JsonConverter(typeof(CamelCaseEnumConverter<ConstraintKind>))]
public enum ConstraintKind
{
    /// <summary>A project's own PackageReference: never go below it.</summary>
    Direct,

    /// <summary>A resolved package's dependency range, in some project's graph.</summary>
    Transitive,

    /// <summary>The range a selected version of another consolidated package asks for.</summary>
    Selected,

    /// <summary>A pin from offramp.yml (exact version).</summary>
    Pin,
}

/// <summary>Something a package's version must satisfy, and where it comes from.</summary>
public sealed record VersionConstraint
{
    public required ConstraintKind Kind { get; init; }

    /// <summary>The project whose graph imposes it; null for a global pin or a selected version's range.</summary>
    public string? Project { get; init; }

    /// <summary>NuGet range syntax (<c>[13.0.3, )</c>).</summary>
    public required string Range { get; init; }

    /// <summary>How it arises, outermost first: <c>Contoso.Serialization 2.0.0</c>, <c>Newtonsoft.Json &gt;= 13.0.3</c>.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [];
}

/// <summary>A version in use and the projects referencing it directly.</summary>
public sealed record VersionInUse(string Version, IReadOnlyList<string> Projects);

[JsonConverter(typeof(CamelCaseEnumConverter<ConsolidationChangeKind>))]
public enum ConsolidationChangeKind
{
    /// <summary>A PackageReference's Version changes.</summary>
    SetVersion,

    /// <summary>A PackageReference loses its Version (central package management provides it).</summary>
    RemoveVersion,

    /// <summary>A PackageReference keeps a different version under central management.</summary>
    VersionOverride,

    /// <summary>A PackageVersion item in the central file.</summary>
    PackageVersion,
}

/// <summary>One edit consolidation makes for a package.</summary>
public sealed record ConsolidationChange(string File, ConsolidationChangeKind Kind, string? From, string? To);

/// <summary>The decision for one package.</summary>
public sealed record PackageConsolidation
{
    public required string Id { get; init; }

    /// <summary>The family (<c>deps.families</c>) the package is aligned with, or null.</summary>
    public string? Family { get; init; }

    public required IReadOnlyList<VersionInUse> Current { get; init; }

    /// <summary>The one version, or null when none satisfies every constraint.</summary>
    public string? Selected { get; init; }

    /// <summary>Why this version, in a sentence.</summary>
    public required string Reason { get; init; }

    /// <summary>The constraint that decided the version (the highest lower bound), when there is one.</summary>
    public VersionConstraint? Deciding { get; init; }

    public required IReadOnlyList<VersionConstraint> Constraints { get; init; }

    /// <summary>Projects kept on another version by a pin (they get <c>VersionOverride</c> under central management).</summary>
    public IReadOnlyList<VersionInUse> Pinned { get; init; } = [];

    public IReadOnlyList<ConsolidationChange> Changes { get; init; } = [];
}

/// <summary>A package left as it is because no version satisfies its constraints.</summary>
public sealed record UnsatisfiableConsolidation
{
    public required string Id { get; init; }

    public required string Code { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<string> Chain { get; init; } = [];
}

/// <summary>A NuGet warning restore reported for the proposed project files.</summary>
public sealed record RestoreWarning(string Code, string? Project, string Message);

/// <summary>How the proposal was checked before applying.</summary>
public sealed record ConsolidationVerification
{
    /// <summary><c>restore</c> or <c>build</c>.</summary>
    public required string Mode { get; init; }

    public required bool Passed { get; init; }

    /// <summary>NU1605, NU1107, NU1608, and NU1010 warnings the proposal adds (ones restore reported before it are not counted).</summary>
    public IReadOnlyList<RestoreWarning> Warnings { get; init; } = [];
}

/// <summary>Where central package versions go, when they do.</summary>
public sealed record CentralPackageManagement
{
    /// <summary><c>existing</c> (the projects already use it) or <c>convert</c> (<c>--cpm</c>).</summary>
    public required string Mode { get; init; }

    /// <summary>The props file holding PackageVersion items, repository-relative.</summary>
    public required string File { get; init; }

    /// <summary>Projects (or the shared props file) given ManagePackageVersionsCentrally and DirectoryPackagesPropsPath, for a non-default file name.</summary>
    public IReadOnlyList<string> OptIn { get; init; } = [];
}

/// <summary>A CPM preflight finding (<c>OFR1301</c>–<c>OFR1303</c>).</summary>
public sealed record CpmFinding(string Code, string Path, string Message);

/// <summary>The <c>result</c> of <c>offramp deps consolidate</c> (<c>schemas/v1/deps-consolidate.json</c>).</summary>
public sealed record ConsolidateResult
{
    public required string Target { get; init; }

    /// <summary><c>lowest</c> or <c>newest</c>.</summary>
    public required string Prefer { get; init; }

    public required IReadOnlyList<PackageConsolidation> Packages { get; init; }

    public required IReadOnlyList<UnsatisfiableConsolidation> Unsatisfiable { get; init; }

    /// <summary>Null when versions stay in the project files.</summary>
    public CentralPackageManagement? Cpm { get; init; }

    public IReadOnlyList<CpmFinding> Hazards { get; init; } = [];

    /// <summary>Null when not verified (<c>--verify none</c>, or nothing to change).</summary>
    public ConsolidationVerification? Verification { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    /// <summary>The unified diff of the proposed files; null once applied.</summary>
    public string? Preview { get; init; }
}
