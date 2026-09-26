using System.Text.Json.Serialization;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.NuGet.Feeds;
using Offramp.Analysis.Rules;
using Offramp.NuGet.Rules;

namespace Offramp.NuGet.Audit;

[JsonConverter(typeof(CamelCaseEnumConverter<PackageStatus>))]
public enum PackageStatus
{
    /// <summary>Every in-use version supports the target.</summary>
    Ok,

    /// <summary>Some in-use version does not, but a newer one does.</summary>
    Upgrade,

    /// <summary>No version supports the target, and a known successor exists.</summary>
    Replace,

    /// <summary>No version supports the target and there is no known successor.</summary>
    Blocked,

    /// <summary>The feeds could not answer (package not found, or feeds unreachable).</summary>
    Unknown,
}

public sealed record InUseVersion
{
    public required string Version { get; init; }

    public required IReadOnlyList<string> Projects { get; init; }

    /// <summary>True when an <c>offramp.yml</c> pin holds this version in one of its projects.</summary>
    public bool Pinned { get; init; }

    public PackageDeprecation? Deprecated { get; init; }
}

public sealed record TargetSupportSummary
{
    /// <summary>In-use version → supports the target (null when the feeds could not provide it).</summary>
    public required SortedDictionary<string, bool?> InUseVersions { get; init; }
}

public sealed record PackageAudit
{
    public required string Id { get; init; }

    public required IReadOnlyList<InUseVersion> InUse { get; init; }

    public required string Target { get; init; }

    public required TargetSupportSummary SupportsTarget { get; init; }

    public string? LowestSupporting { get; init; }

    public string? NewestSupporting { get; init; }

    public string? Newest { get; init; }

    public bool NoVersionSupports { get; init; }

    public bool WindowsOnly { get; init; }

    /// <summary>The assembly and the reason, when <see cref="WindowsOnly"/>.</summary>
    public string? WindowsOnlyEvidence { get; init; }

    /// <summary>Deprecation of the newest version, which is how a feed deprecates a whole package.</summary>
    public PackageDeprecation? Deprecated { get; init; }

    public PackageReplacement? Replacement { get; init; }

    public required PackageStatus Status { get; init; }
}

/// <summary>A non-package reference: a framework assembly (with its modern equivalent) or a file.</summary>
public sealed record AssemblyReferenceAudit
{
    public required string Project { get; init; }

    public required string Name { get; init; }

    public required AssemblyReferenceKind Kind { get; init; }

    public string? HintPath { get; init; }

    /// <summary>For framework references; file references are resolved by <c>deps resolve-dlls</c>.</summary>
    public FrameworkAssemblyMapping? Mapping { get; init; }
}

public sealed record AuditSummary(int Ok, int Upgrade, int Replace, int Blocked, int Unknown);

/// <summary>The result of <c>offramp deps audit</c> (<c>schemas/v1/deps-audit.json</c>).</summary>
public sealed record DepsAuditResult
{
    public required string Target { get; init; }

    public required IReadOnlyList<string> Sources { get; init; }

    public required IReadOnlyList<PackageAudit> Packages { get; init; }

    public required IReadOnlyList<AssemblyReferenceAudit> AssemblyReferences { get; init; }

    public required AuditSummary Summary { get; init; }

    /// <summary>True when a feed could not be reached; some answers are missing.</summary>
    public bool Partial { get; init; }
}
