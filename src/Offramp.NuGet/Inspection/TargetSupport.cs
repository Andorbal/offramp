using NuGet.Frameworks;

namespace Offramp.NuGet.Inspection;

/// <summary>Whether a package version supports a target framework, decided by NuGet.Frameworks' compatibility rules.</summary>
public static class TargetSupport
{
    public static bool Supports(PackageInspection package, NuGetFramework target)
    {
        var frameworks = package.AssetFrameworks.Count > 0 ? package.AssetFrameworks : package.DependencyFrameworks;
        if (frameworks.Count == 0)
        {
            // No assets and no dependency groups: nothing framework-specific to be incompatible with.
            return true;
        }

        return frameworks.Select(Parse).Any(f => f.IsAny || DefaultCompatibilityProvider.Instance.IsCompatible(target, f));
    }

    /// <summary>
    /// True when the version has managed assemblies (lib, ref, runtimes/*/lib). A version without
    /// them never replaces one with them: an old content-only or tools-only release "supports"
    /// every target only because it has nothing to compile against.
    /// </summary>
    public static bool HasAssemblies(PackageInspection package) => package.Assemblies.Count > 0;

    /// <summary>
    /// True when the version has nothing for any framework: no framework-specific assets, no
    /// dependency groups, no assemblies, and no native code (build or tools scripts only, such as
    /// Microsoft.Bcl.Build). It "supports" every target only because it has nothing to judge.
    /// </summary>
    public static bool HasNothingForAnyFramework(PackageInspection package) =>
        package.AssetFrameworks.Count == 0 && package.DependencyFrameworks.Count == 0 && package.Assemblies.Count == 0 && package.NativeAssets.Count == 0;

    /// <summary>
    /// Why the assemblies NuGet would pick for <paramref name="target"/> (the nearest lib
    /// folder, else ref) only work on Windows, or null. A package with no managed assemblies
    /// whose native code (<c>runtimes/&lt;rid&gt;/native/</c>) is all for Windows runtime
    /// identifiers is Windows-only whatever the target.
    /// </summary>
    public static string? WindowsOnly(PackageInspection package, NuGetFramework target)
    {
        foreach (var root in new[] { "lib/", "ref/" })
        {
            var inRoot = package.Assemblies.Where(a => a.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList();
            var nearest = NuGetFrameworkUtility.GetNearest(inRoot.Select(a => a.Framework).Distinct(StringComparer.Ordinal), target, Parse);
            if (nearest is null)
            {
                continue;
            }

            var evidence = inRoot
                .Where(a => a.Framework == nearest && a.WindowsOnly is not null)
                .OrderBy(a => a.Path, StringComparer.Ordinal)
                .Select(a => $"{a.Path}: {a.WindowsOnly}")
                .FirstOrDefault();
            return evidence;
        }

        return WindowsNativeOnly(package) is { } native ? $"{native}: native code for Windows only" : null;
    }

    /// <summary>
    /// The first native asset of a package without managed assemblies whose native assets are all
    /// for Windows runtime identifiers (<c>win</c>, <c>win-x64</c>, <c>win10-arm64</c>, ...); null otherwise.
    /// </summary>
    public static string? WindowsNativeOnly(PackageInspection package) =>
        !HasAssemblies(package) && package.NativeAssets.Count > 0 && package.NativeAssets.All(IsWindowsRuntime)
            ? package.NativeAssets.Order(StringComparer.Ordinal).First()
            : null;

    private static bool IsWindowsRuntime(string nativeAsset) =>
        nativeAsset.Split('/') is [_, var rid, ..] && rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    /// <summary>The dependencies NuGet would use for <paramref name="target"/>: the nearest group's, or none.</summary>
    public static IReadOnlyList<InspectedDependency> Dependencies(PackageInspection package, NuGetFramework target)
    {
        var nearest = NuGetFrameworkUtility.GetNearest(package.DependencyGroups, target, g => Parse(g.Framework));
        return nearest?.Dependencies ?? [];
    }

    public static NuGetFramework Parse(string shortName) =>
        shortName == "any" ? NuGetFramework.AnyFramework : NuGetFramework.ParseFolder(shortName);
}
