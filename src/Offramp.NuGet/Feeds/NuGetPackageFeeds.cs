using System.Collections.Concurrent;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Restore;

namespace Offramp.NuGet.Feeds;

/// <summary>
/// Feed access through NuGet.Protocol with the repository's nuget.config (or the
/// sources <c>deps.feeds</c> lists), so private feeds, credentials, and folder
/// feeds work as they do for restore.
/// </summary>
public sealed class NuGetPackageFeeds : IPackageFeeds, IDisposable
{
    /// <summary>How long one feed may take to answer one request before it counts as unreachable.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<SourceRepository> _repositories;
    private readonly string? _globalPackagesFolder;
    private readonly IReadOnlyList<string> _packagesFolders;
    private readonly TimeSpan _timeout;
    private readonly SourceCacheContext _cache = new();
    private readonly ConcurrentDictionary<string, bool> _unreachable = new(StringComparer.Ordinal);

    private NuGetPackageFeeds(IReadOnlyList<PackageSource> sources, string? globalPackagesFolder, IReadOnlyList<string>? packagesFolders, TimeSpan? timeout)
    {
        _repositories = [.. sources.Select(s => Repository.Factory.GetCoreV3(s))];
        _globalPackagesFolder = globalPackagesFolder;
        _packagesFolders = packagesFolders ?? [];
        _timeout = timeout ?? DefaultTimeout;
        Sources = [.. sources.Select(s => s.Name == s.Source ? s.Source : $"{s.Name} ({s.Source})")];
    }

    public IReadOnlyList<string> Sources { get; }

    /// <summary>
    /// Feeds for a repository: <paramref name="feeds"/> when given, else the enabled sources of its
    /// NuGet configuration. A package is read from the global packages folder, then from
    /// <paramref name="packagesFolders"/> (packages.config folders, <c>&lt;Id&gt;.&lt;Version&gt;/</c> with the
    /// .nupkg, as <c>scan</c> and <c>nuget restore</c> write them), before a feed is asked.
    /// </summary>
    public static NuGetPackageFeeds ForRepository(string repositoryRoot, IReadOnlyList<string>? feeds, IReadOnlyList<string>? packagesFolders = null, TimeSpan? timeout = null)
    {
        var settings = Settings.LoadDefaultSettings(repositoryRoot);
        var sources = feeds is { Count: > 0 }
            ? Explicit(repositoryRoot, feeds)
            : new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        return new NuGetPackageFeeds(sources, SettingsUtility.GetGlobalPackagesFolder(settings), packagesFolders, timeout);
    }

    /// <summary>
    /// Feeds for a scanned repository: <see cref="ForRepository"/>, reading packages from the
    /// folders its packages.config projects restore into (<c>repositoryPath</c> from nuget.config,
    /// else <c>packages</c> beside the solution, and <c>packages</c> at the root) before any feed.
    /// </summary>
    public static NuGetPackageFeeds ForWorkspace(string repositoryRoot, IReadOnlyList<string>? feeds, WorkspaceModel model)
    {
        if (!model.Projects.Any(p => p.PackagesConfig))
        {
            return ForRepository(repositoryRoot, feeds);
        }

        var solutionDirectory = model.Solution is null ? repositoryRoot : Path.GetDirectoryName(RepoPaths.ToAbsolute(repositoryRoot, model.Solution)) ?? repositoryRoot;
        var folders = new[] { PackagesConfigRestorer.PackagesFolder(solutionDirectory, Settings.LoadDefaultSettings(repositoryRoot)), Path.Combine(repositoryRoot, "packages") };
        return ForRepository(repositoryRoot, feeds, [.. folders.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Exactly these sources, and no global packages folder: every answer comes from them.</summary>
    public static NuGetPackageFeeds ForSources(string repositoryRoot, IReadOnlyList<string> feeds, IReadOnlyList<string>? packagesFolders = null, TimeSpan? timeout = null) =>
        new(Explicit(repositoryRoot, feeds), globalPackagesFolder: null, packagesFolders, timeout);

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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                var resource = await repository.GetResourceAsync<PackageMetadataResource>(timeout.Token)
                    ?? throw new InvalidOperationException($"{repository.PackageSource.Source} has no package metadata resource.");
                found = await resource.GetMetadataAsync(id, includePrerelease: true, includeUnlisted: true, _cache, NullLogger.Instance, timeout.Token);
            }
            catch (Exception ex) when (ex is FatalProtocolException or HttpRequestException or IOException or InvalidOperationException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
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

        if (FromPackagesFolder(id, version) is { } installed)
        {
            return await File.ReadAllBytesAsync(installed, cancellationToken);
        }

        foreach (var repository in _repositories.Where(r => !_unreachable.ContainsKey(r.PackageSource.Source)))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(timeout.Token);
                if (resource is null)
                {
                    continue;
                }

                using var stream = new MemoryStream();
                if (await resource.CopyNupkgToStreamAsync(id, version, stream, _cache, NullLogger.Instance, timeout.Token))
                {
                    return stream.ToArray();
                }
            }
            catch (Exception ex) when (ex is FatalProtocolException or HttpRequestException or IOException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _unreachable[repository.PackageSource.Source] = true;
            }
        }

        return null;
    }

    /// <summary>
    /// The .nupkg in a packages.config folder: <c>&lt;Id&gt;.&lt;Version&gt;/&lt;Id&gt;.&lt;Version&gt;.nupkg</c>, the
    /// version spelled as normalized or as written with four parts; null when none has it.
    /// </summary>
    private string? FromPackagesFolder(string id, NuGetVersion version)
    {
        foreach (var folder in _packagesFolders)
        {
            foreach (var spelling in new[] { version.ToNormalizedString(), version.ToFullString(), version.OriginalVersion }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = $"{id}.{spelling}";
                var candidate = Path.Combine(folder, name, name + ".nupkg");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public void Dispose() => _cache.Dispose();
}
