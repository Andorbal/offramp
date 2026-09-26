namespace Offramp.NuGet.Inspection;

/// <summary>
/// What a package version contains, as far as target support is concerned. Cached
/// under <c>.offramp/cache/packages/&lt;id&gt;/&lt;version&gt;.json</c>; a published version
/// never changes.
/// </summary>
public sealed record PackageInspection
{
    /// <summary>Bumped when inspection changes, so older cache entries are recomputed.</summary>
    public const int CurrentFormat = 2;

    public int Format { get; init; } = CurrentFormat;

    public required string Id { get; init; }

    public required string Version { get; init; }

    /// <summary>Frameworks with assets (lib, ref, runtimes/*/lib, build, buildTransitive, contentFiles), short names, sorted.</summary>
    public required IReadOnlyList<string> AssetFrameworks { get; init; }

    /// <summary>Frameworks of the dependency groups, used when the package has no assets (a meta-package).</summary>
    public required IReadOnlyList<string> DependencyFrameworks { get; init; }

    /// <summary>Managed assemblies under lib, ref, and runtimes/*/lib, with Windows-only evidence.</summary>
    public required IReadOnlyList<InspectedAssembly> Assemblies { get; init; }

    /// <summary>The nuspec's dependency groups (framework short name, <c>any</c> for none), in framework order.</summary>
    public IReadOnlyList<InspectedDependencyGroup> DependencyGroups { get; init; } = [];
}

/// <summary>A package's dependencies for one target framework.</summary>
public sealed record InspectedDependencyGroup(string Framework, IReadOnlyList<InspectedDependency> Dependencies);

/// <summary>A dependency and the version range the package asks for (NuGet range syntax).</summary>
public sealed record InspectedDependency(string Id, string Range);

/// <summary>An assembly in a package, its identity, and why it only works on Windows, if it does.</summary>
public sealed record InspectedAssembly(string Path, string Framework, string? WindowsOnly)
{
    /// <summary>The assembly name, or null when the file has no assembly metadata.</summary>
    public string? Name { get; init; }

    /// <summary>The assembly version (<c>13.0.0.0</c>).</summary>
    public string? Version { get; init; }

    /// <summary>The public key token (hex), or null for an unsigned assembly.</summary>
    public string? PublicKeyToken { get; init; }
}
