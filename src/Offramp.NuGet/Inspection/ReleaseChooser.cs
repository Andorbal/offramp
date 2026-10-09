using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.NuGet.Feeds;

namespace Offramp.NuGet.Inspection;

/// <summary>A release chosen from a range, with its package, or why there is none.</summary>
public sealed record ChosenRelease(NuGetVersion? Version, byte[]? Package, IReadOnlyList<string> UnreachableSources, string? Problem);

/// <summary>Chooses the newest stable, listed release of a package in a range that supports every given framework.</summary>
public static class ReleaseChooser
{
    public static async Task<ChosenRelease> NewestSupportingAsync(
        IPackageFeeds feeds, string id, VersionRange range, IReadOnlyCollection<NuGetFramework> frameworks, CancellationToken cancellationToken)
    {
        var versions = await feeds.GetVersionsAsync(id, cancellationToken).ConfigureAwait(false);
        var candidates = versions.Versions
            .Where(v => v.Listed && !v.Version.IsPrerelease && range.Satisfies(v.Version))
            .Select(v => v.Version)
            .OrderByDescending(v => v)
            .ToList();
        foreach (var version in candidates)
        {
            if (await feeds.DownloadAsync(id, version, cancellationToken).ConfigureAwait(false) is not { } package)
            {
                continue;
            }

            var inspection = PackageInspector.Inspect(package);
            if (frameworks.All(f => TargetSupport.Supports(inspection, f)))
            {
                return new ChosenRelease(version, package, versions.UnreachableSources, null);
            }
        }

        return new ChosenRelease(null, null, versions.UnreachableSources, candidates.Count == 0
            ? $"no stable release of {id} in {range.PrettyPrint()} on {string.Join(", ", feeds.Sources)}"
            : $"no release of {id} in {range.PrettyPrint()} supports {string.Join(", ", frameworks.Select(f => f.GetShortFolderName()).Order(StringComparer.Ordinal))}");
    }

    /// <summary>One exact release: its package, or why it could not be had.</summary>
    public static async Task<ChosenRelease> ExactAsync(IPackageFeeds feeds, string id, NuGetVersion version, CancellationToken cancellationToken) =>
        await feeds.DownloadAsync(id, version, cancellationToken).ConfigureAwait(false) is { } package
            ? new ChosenRelease(version, package, [], null)
            : new ChosenRelease(version, null, [], $"{id} {version} is in neither the global packages folder nor {string.Join(", ", feeds.Sources)}");
}
