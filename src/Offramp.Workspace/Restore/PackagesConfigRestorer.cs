using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Offramp.Workspace.Restore;

/// <summary>A package that could not be restored, and why.</summary>
public sealed record PackagesConfigRestoreFailure(string Id, string Version, string Reason);

/// <summary>What <see cref="PackagesConfigRestorer"/> did.</summary>
/// <param name="PackagesFolder">Absolute path of the folder packages were restored into.</param>
/// <param name="Restored">The <c>Id.Version</c> folders written, sorted.</param>
/// <param name="Failed">Packages that could not be restored, sorted.</param>
public sealed record PackagesConfigRestoreResult(
    string PackagesFolder, IReadOnlyList<string> Restored, IReadOnlyList<PackagesConfigRestoreFailure> Failed);

/// <summary>
/// Restores what the projects' <c>packages.config</c> files list into the solution's packages
/// folder, laid out as <c>nuget restore</c> lays it out (<c>packages/&lt;Id&gt;.&lt;Version&gt;/</c>,
/// named from the package's nuspec), because <c>dotnet restore</c> skips <c>packages.config</c>
/// and <c>NuGet.exe</c> does not run outside Windows (docs/decisions/0037-legacy-projects-outside-windows.md).
/// A package already in the folder, in any letter case, is left alone; nothing is overwritten.
/// Packages come from the NuGet global packages folder when it has them, else from the feeds
/// of the solution's <c>nuget.config</c>.
/// </summary>
public static class PackagesConfigRestorer
{
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The folder <c>nuget restore</c> uses: <c>repositoryPath</c> from nuget.config, else <c>packages</c> beside the solution.</summary>
    public static string PackagesFolder(string solutionDirectory, ISettings settings) =>
        SettingsUtility.GetRepositoryPath(settings) ?? Path.Combine(solutionDirectory, "packages");

    /// <param name="solutionPath">Absolute path of the solution.</param>
    /// <param name="projectPaths">Absolute paths of its projects; a <c>packages.config</c> beside one is read.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    /// <returns>Null when no project has a <c>packages.config</c>.</returns>
    public static async Task<PackagesConfigRestoreResult?> RestoreAsync(
        string solutionPath, IEnumerable<string> projectPaths, CancellationToken cancellationToken)
    {
        var wanted = Wanted(projectPaths);
        if (wanted.Count == 0)
        {
            return null;
        }

        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var settings = Settings.LoadDefaultSettings(solutionDirectory);
        var folder = PackagesFolder(solutionDirectory, settings);
        var existing = Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var restored = new List<string>();
        var failed = new List<PackagesConfigRestoreFailure>();
        var feeds = new Lazy<IReadOnlyList<PackageSource>>(() =>
            [.. new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled)]);
        using var cache = new SourceCacheContext();
        var extraction = new PackageExtractionContext(
            PackageSaveMode.Defaultv2, XmlDocFileSaveMode.None, ClientPolicyContext.GetClientPolicy(settings, NullLogger.Instance), NullLogger.Instance);
        var globalPackages = SettingsUtility.GetGlobalPackagesFolder(settings);

        foreach (var (id, spelled) in wanted)
        {
            if (!NuGetVersion.TryParse(spelled, out var version))
            {
                failed.Add(new PackagesConfigRestoreFailure(id, spelled, $"'{spelled}' is not a valid version."));
                continue;
            }

            if (existing.Contains($"{id}.{spelled}") || existing.Contains($"{id}.{version.ToNormalizedString()}"))
            {
                continue;
            }

            var (stream, source, reason) = await OpenAsync(id, version, globalPackages, feeds, cache, cancellationToken);
            if (stream is null)
            {
                failed.Add(new PackagesConfigRestoreFailure(id, spelled, reason!));
                continue;
            }

            await using (stream)
            {
                Directory.CreateDirectory(folder);
                var resolver = new PackagePathResolver(folder, useSideBySidePaths: true);
                await PackageExtractor.ExtractPackageAsync(source!, stream, resolver, extraction, cancellationToken);
                stream.Position = 0;
                using var reader = new PackageArchiveReader(stream, leaveStreamOpen: true);
                var name = resolver.GetPackageDirectoryName(reader.GetIdentity());
                restored.Add(name);
                existing.Add(name);
            }
        }

        return new PackagesConfigRestoreResult(
            folder,
            [.. restored.Order(StringComparer.Ordinal)],
            [.. failed.OrderBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Version, StringComparer.Ordinal)]);
    }

    /// <summary>Every (id, version) the packages.config files list, once, in id then version order.</summary>
    private static List<(string Id, string Version)> Wanted(IEnumerable<string> projectPaths) =>
        [.. projectPaths
            .Select(p => Path.Combine(Path.GetDirectoryName(p)!, "packages.config"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .SelectMany(p => Model.PackagesConfigReader.Read(p) ?? [])
            .Select(p => (p.Id, p.Version))
            .DistinctBy(p => (p.Id.ToLowerInvariant(), p.Version.ToLowerInvariant()))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.Ordinal)];

    /// <summary>The package's .nupkg from the global packages folder, else from the first feed that has it.</summary>
    private static async Task<(Stream? Stream, string? Source, string? Reason)> OpenAsync(
        string id, NuGetVersion version, string globalPackages, Lazy<IReadOnlyList<PackageSource>> feeds,
        SourceCacheContext cache, CancellationToken cancellationToken)
    {
        var cached = new VersionFolderPathResolver(globalPackages).GetPackageFilePath(id, version);
        if (File.Exists(cached))
        {
            return (new MemoryStream(await File.ReadAllBytesAsync(cached, cancellationToken)), cached, null);
        }

        var problems = new List<string>();
        foreach (var feed in feeds.Value)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FeedTimeout);
            try
            {
                var resource = await Repository.Factory.GetCoreV3(feed).GetResourceAsync<FindPackageByIdResource>(timeout.Token);
                var stream = new MemoryStream();
                if (resource is not null && await resource.CopyNupkgToStreamAsync(id, version, stream, cache, NullLogger.Instance, timeout.Token))
                {
                    stream.Position = 0;
                    return (stream, feed.Source, null);
                }

                await stream.DisposeAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                problems.Add($"{feed.Name} could not be read ({ex.Message.Split('\n')[0].Trim()})");
            }
        }

        var names = string.Join(", ", feeds.Value.Select(f => f.Name));
        return (null, null, problems.Count > 0
            ? $"not in the global packages folder, and {string.Join("; ", problems)}."
            : feeds.Value.Count == 0
                ? "not in the global packages folder, and nuget.config enables no feed."
                : $"not in the global packages folder or on {names}.");
    }
}
