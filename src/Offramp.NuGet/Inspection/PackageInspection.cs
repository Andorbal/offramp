namespace Offramp.NuGet.Inspection;

/// <summary>
/// What a package version contains, as far as target support is concerned. Cached
/// under <c>.offramp/cache/packages/&lt;id&gt;/&lt;version&gt;.json</c>; a published version
/// never changes.
/// </summary>
public sealed record PackageInspection
{
    /// <summary>Bumped when inspection changes, so older cache entries are recomputed.</summary>
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public required string Id { get; init; }

    public required string Version { get; init; }

    /// <summary>Frameworks with assets (lib, ref, runtimes/*/lib, build, buildTransitive, contentFiles), short names, sorted.</summary>
    public required IReadOnlyList<string> AssetFrameworks { get; init; }

    /// <summary>Frameworks of the dependency groups, used when the package has no assets (a meta-package).</summary>
    public required IReadOnlyList<string> DependencyFrameworks { get; init; }

    /// <summary>Managed assemblies under lib, ref, and runtimes/*/lib, with Windows-only evidence.</summary>
    public required IReadOnlyList<InspectedAssembly> Assemblies { get; init; }
}

/// <summary>An assembly in a package and why it only works on Windows, if it does.</summary>
public sealed record InspectedAssembly(string Path, string Framework, string? WindowsOnly);
