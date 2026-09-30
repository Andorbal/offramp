namespace Offramp.Scaffolding.Csproj;

/// <summary>A package reference that replaces a packages.config entry.</summary>
public sealed record ModernizedPackage(string Id, string Version, bool DevelopmentDependency);

/// <summary>The compile-set comparison of one converted project (<c>OFR4303</c> when it fails).</summary>
public sealed record ModernizeVerification
{
    public required bool Passed { get; init; }

    /// <summary>One entry per target framework the original project compiled.</summary>
    public IReadOnlyList<CompileSetDifference> Targets { get; init; } = [];

    /// <summary>Target frameworks the converted project compiles and the original did not (not compared).</summary>
    public IReadOnlyList<string> AddedTargets { get; init; } = [];

    /// <summary>True when the converted project built, false when it did not, null when it was not built (the scan's log is gone).</summary>
    public bool? Built { get; init; }

    /// <summary>How many distinct errors the converted build reported; 0 when it built.</summary>
    public int BuildErrorCount { get; init; }

    /// <summary>The converted build's errors by code, the most frequent first (then by code).</summary>
    public IReadOnlyList<BuildErrorCode> BuildErrorCodes { get; init; } = [];

    /// <summary>The first 10 errors of a converted build that failed, with paths relative to the repository; empty when it built.</summary>
    public IReadOnlyList<string> BuildErrors { get; init; } = [];
}

/// <summary>How many of a failed converted build's errors have one code (<c>CS0246</c>, <c>MSB4019</c>).</summary>
public sealed record BuildErrorCode(string Code, int Count);

/// <summary>What <c>csproj modernize</c> does to one project.</summary>
public sealed record ModernizedProject
{
    public required string Project { get; init; }

    /// <summary><c>legacy</c> (converted to SDK style) or <c>sdk</c> (already SDK style).</summary>
    public required string Style { get; init; }

    /// <summary>True when the project file changes.</summary>
    public bool Changed { get; init; }

    /// <summary>Why a legacy project is not converted (OFR4304), else null.</summary>
    public string? Skipped { get; init; }

    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary><c>globbed</c> or <c>explicit</c> (OFR4301); null for SDK-style projects.</summary>
    public string? CompileItems { get; init; }

    /// <summary>packages.config entries that became PackageReference items.</summary>
    public IReadOnlyList<ModernizedPackage> Packages { get; init; } = [];

    /// <summary>Project properties that replace AssemblyInfo attributes the SDK generates, and <c>GenerateAssembly…Attribute</c>=false for the attributes a shared or generated file keeps (OFR4306).</summary>
    public IReadOnlyList<Refactoring.Codemods.CodemodPropertyEdit> Properties { get; init; } = [];

    /// <summary>What the SDK makes unnecessary and was left out, in document order.</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];

    /// <summary>Other files changed or removed: AssemblyInfo files, packages.config, Directory.Packages.props.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public ModernizeVerification? Verification { get; init; }
}

/// <summary>The result of <c>offramp csproj modernize</c>.</summary>
public sealed record ModernizeResult
{
    public IReadOnlyList<ModernizedProject> Projects { get; init; } = [];

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}
