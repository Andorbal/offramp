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
    /// Why the assemblies NuGet would pick for <paramref name="target"/> (the nearest lib
    /// folder, else ref) only work on Windows, or null.
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

        return null;
    }

    /// <summary>The dependencies NuGet would use for <paramref name="target"/>: the nearest group's, or none.</summary>
    public static IReadOnlyList<InspectedDependency> Dependencies(PackageInspection package, NuGetFramework target)
    {
        var nearest = NuGetFrameworkUtility.GetNearest(package.DependencyGroups, target, g => Parse(g.Framework));
        return nearest?.Dependencies ?? [];
    }

    public static NuGetFramework Parse(string shortName) =>
        shortName == "any" ? NuGetFramework.AnyFramework : NuGetFramework.ParseFolder(shortName);
}
