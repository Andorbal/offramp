using System.Collections.Concurrent;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Offramp.NuGet.Feeds;

/// <summary>
/// Feed access through NuGet.Protocol with the repository's nuget.config (or the
/// sources <c>deps.feeds</c> lists), so private feeds, credentials, and folder
/// feeds work as they do for restore.
/// </summary>
public sealed class NuGetPackageFeeds : IPackageFeeds, IDisposable
{
    private readonly IReadOnlyList<SourceRepository> _repositories;
    private readonly string? _globalPackagesFolder;
    private readonly SourceCacheContext _cache = new();
    private readonly ConcurrentDictionary<string, bool> _unreachable = new(StringComparer.Ordinal);

    private NuGetPackageFeeds(IReadOnlyList<PackageSource> sources, string? globalPackagesFolder)
    {
        _repositories = [.. sources.Select(s => Repository.Factory.GetCoreV3(s))];
        _globalPackagesFolder = globalPackagesFolder;
        Sources = [.. sources.Select(s => s.Name == s.Source ? s.Source : $"{s.Name} ({s.Source})")];
    }

    public IReadOnlyList<string> Sources { get; }

    /// <summary>Feeds for a repository: <paramref name="feeds"/> when given, else the enabled sources of its NuGet configuration.</summary>
    public static NuGetPackageFeeds ForRepository(string repositoryRoot, IReadOnlyList<string>? feeds)
    {
        var settings = Settings.LoadDefaultSettings(repositoryRoot);
        var sources = feeds is { Count: > 0 }
            ? Explicit(repositoryRoot, feeds)
            : new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        return new NuGetPackageFeeds(sources, SettingsUtility.GetGlobalPackagesFolder(settings));
    }

    /// <summary>Exactly these sources, and no global packages folder: every answer comes from them.</summary>
    public static NuGetPackageFeeds ForSources(string repositoryRoot, IReadOnlyList<string> feeds) =>
        new(Explicit(repositoryRoot, feeds), globalPackagesFolder: null);

    private static List<PackageSource> Explicit(string repositoryRoot, IReadOnlyList<string> feeds) =>
        [.. feeds.Select((f, i) => new PackageSource(
            Path.IsPathRooted(f) || f.Contains("://", StringComparison.Ordinal) ? f : Path.GetFullPath(f, repositoryRoot),
            "feed" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))];

    public async Task<PackageVersions> GetVersionsAsync(string id, CancellationToken cancellationToken)
    {
        var versions = new SortedDictionary<NuGetVersion, PackageVersionInfo>();
        var unreachable = new List<string>();
        foreach (var repository in _repositories)
        {
            IEnumerable<IPackageSearchMetadata> found;
            try
            {
                var resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken)
                    ?? throw new InvalidOperationException($"{repository.PackageSource.Source} has no package metadata resource.");
                found = await resource.GetMetadataAsync(id, includePrerelease: true, includeUnlisted: true, _cache, NullLogger.Instance, cancellationToken);
            }
            catch (Exception ex) when (ex is FatalProtocolException or HttpRequestException or IOException or InvalidOperationException)
            {
                unreachable.Add(repository.PackageSource.Source);
                _unreachable[repository.PackageSource.Source] = true;
                continue;
            }

            foreach (var metadata in found)
            {
                var version = metadata.Identity.Version;
                var deprecation = await metadata.GetDeprecationMetadataAsync();
                var info = new PackageVersionInfo(version, metadata.IsListed, deprecation is null ? null : new PackageDeprecation(
                    [.. deprecation.Reasons.Order(StringComparer.Ordinal)],
                    deprecation.Message,
                    deprecation.AlternatePackage?.PackageId,
                    deprecation.AlternatePackage?.Range?.ToNormalizedString()));
                versions[version] = versions.TryGetValue(version, out var existing)
                    ? existing with { Listed = existing.Listed || info.Listed, Deprecation = existing.Deprecation ?? info.Deprecation }
                    : info;
            }
        }

        return new PackageVersions(id, [.. versions.Values], unreachable);
    }

    public async Task<byte[]?> DownloadAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        if (_globalPackagesFolder is not null)
        {
            var lower = id.ToLowerInvariant();
            var normalized = version.ToNormalizedString().ToLowerInvariant();
            var cached = Path.Combine(_globalPackagesFolder, lower, normalized, $"{lower}.{normalized}.nupkg");
            if (File.Exists(cached))
            {
                return await File.ReadAllBytesAsync(cached, cancellationToken);
            }
        }

        foreach (var repository in _repositories.Where(r => !_unreachable.ContainsKey(r.PackageSource.Source)))
        {
            try
            {
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                if (resource is null)
                {
                    continue;
                }

                using var stream = new MemoryStream();
                if (await resource.CopyNupkgToStreamAsync(id, version, stream, _cache, NullLogger.Instance, cancellationToken))
                {
                    return stream.ToArray();
                }
            }
            catch (Exception ex) when (ex is FatalProtocolException or HttpRequestException or IOException)
            {
                _unreachable[repository.PackageSource.Source] = true;
            }
        }

        return null;
    }

    public void Dispose() => _cache.Dispose();
}
