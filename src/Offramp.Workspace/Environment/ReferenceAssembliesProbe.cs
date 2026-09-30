using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace Offramp.Workspace.Environment;

public enum ReferenceAssembliesState
{
    /// <summary>In the NuGet global packages folder.</summary>
    Cached,

    /// <summary>The .NET Framework targeting pack is installed (Windows).</summary>
    TargetingPack,

    /// <summary>Not cached, but a configured feed has it; the first build downloads it.</summary>
    AvailableFromFeed,

    /// <summary>Every enabled feed answered and none has it.</summary>
    NotFound,

    /// <summary>Not cached and at least one feed could not be queried.</summary>
    FeedUnreachable,
}

/// <summary>
/// What the probe found. <see cref="Frameworks"/> are the targets the state is about (all of them when cached or
/// provided by a targeting pack; the ones not cached otherwise), as package-folder short names (<c>net461</c>).
/// </summary>
public sealed record ReferenceAssembliesResult(ReferenceAssembliesState State, string? Detail)
{
    public IReadOnlyList<string> Frameworks { get; init; } = [];
}

/// <summary>Can <c>net4x</c> targets compile here? Replaced by a fake in tests.</summary>
public interface IReferenceAssembliesProbe
{
    /// <param name="repositoryRoot">Where <c>nuget.config</c> is looked up.</param>
    /// <param name="frameworks">The .NET Framework targets to probe (<c>net40</c>, <c>net461</c>); none probes <see cref="DefaultFramework"/>.</param>
    /// <param name="cancellationToken">Cancels feed queries.</param>
    Task<ReferenceAssembliesResult> ProbeAsync(string repositoryRoot, IReadOnlyList<string> frameworks, CancellationToken cancellationToken);

    /// <summary>The target probed when the projects' targets are unknown.</summary>
    public const string DefaultFramework = "net48";
}

/// <summary>
/// Looks for <c>Microsoft.NETFramework.ReferenceAssemblies.&lt;tfm&gt;</c> of every target asked about in the global
/// packages folder, then the installed targeting packs, then the feeds of the repository's <c>nuget.config</c>.
/// The worst answer wins: not found, then feed unreachable, then available from a feed.
/// </summary>
public sealed class ReferenceAssembliesProbe : IReferenceAssembliesProbe
{
    /// <summary>The package id prefix; the target's short name completes it.</summary>
    public const string PackagePrefix = "Microsoft.NETFramework.ReferenceAssemblies.";

    public const string PackageId = PackagePrefix + IReferenceAssembliesProbe.DefaultFramework;
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(20);

    public async Task<ReferenceAssembliesResult> ProbeAsync(string repositoryRoot, IReadOnlyList<string> frameworks, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> wanted = frameworks.Count == 0 ? [IReferenceAssembliesProbe.DefaultFramework] : [.. frameworks.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var settings = Settings.LoadDefaultSettings(repositoryRoot);
        var globalPackages = SettingsUtility.GetGlobalPackagesFolder(settings);
        var cached = wanted.Select(tfm => (Tfm: tfm, Version: FindCachedVersion(globalPackages, PackagePrefix + tfm))).ToList();
        var missing = cached.Where(c => c.Version is null).Select(c => c.Tfm).ToList();
        if (missing.Count == 0)
        {
            return new ReferenceAssembliesResult(ReferenceAssembliesState.Cached, string.Join(", ", cached.Select(c => $"{c.Tfm} {c.Version}"))) { Frameworks = wanted };
        }

        if (OperatingSystem.IsWindows() && missing.All(HasTargetingPack))
        {
            return new ReferenceAssembliesResult(ReferenceAssembliesState.TargetingPack, string.Join(", ", missing.Select(PackVersion))) { Frameworks = wanted };
        }

        var sources = new PackageSourceProvider(settings).LoadPackageSources()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var notFound = new List<string>();
        var unknown = new List<string>();
        var providers = new SortedSet<string>(StringComparer.Ordinal);
        var unreachable = new SortedSet<string>(StringComparer.Ordinal);
        using var cache = new SourceCacheContext { NoCache = false };
        foreach (var tfm in missing)
        {
            var found = false;
            var failed = false;
            foreach (var source in sources.Where(s => !unreachable.Contains(s.Name)))
            {
                switch (await HasPackageAsync(source, PackagePrefix + tfm, cache, cancellationToken))
                {
                    case true:
                        found = true;
                        providers.Add(source.Name);
                        break;
                    case null:
                        failed = true;
                        unreachable.Add(source.Name);
                        break;
                }

                if (found)
                {
                    break;
                }
            }

            if (!found)
            {
                (failed || unreachable.Count > 0 ? unknown : notFound).Add(tfm);
            }
        }

        return notFound.Count > 0 ? new ReferenceAssembliesResult(ReferenceAssembliesState.NotFound, null) { Frameworks = notFound }
            : unknown.Count > 0 ? new ReferenceAssembliesResult(ReferenceAssembliesState.FeedUnreachable, string.Join(", ", unreachable)) { Frameworks = unknown }
            : new ReferenceAssembliesResult(ReferenceAssembliesState.AvailableFromFeed, string.Join(", ", providers)) { Frameworks = missing };
    }

    /// <summary>True when the feed has the package, false when it answered without it, null when it could not be queried.</summary>
    private static async Task<bool?> HasPackageAsync(PackageSource source, string packageId, SourceCacheContext cache, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            var repository = Repository.Factory.GetCoreV3(source);
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>(timeout.Token)
                ?? throw new InvalidOperationException($"Feed {source.Name} has no package-by-id resource.");
            var versions = await resource.GetAllVersionsAsync(packageId, cache, NullLogger.Instance, timeout.Token);
            return versions.Any();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The targeting pack folder name of a target (<c>net461</c> is <c>v4.6.1</c>).</summary>
    private static string PackVersion(string tfm)
    {
        var version = NuGet.Frameworks.NuGetFramework.Parse(tfm).Version;
        return version.Build > 0 ? $"v{version.Major}.{version.Minor}.{version.Build}" : $"v{version.Major}.{version.Minor}";
    }

    private static bool HasTargetingPack(string tfm)
    {
        var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        return Directory.Exists(Path.Combine(programFiles, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", PackVersion(tfm)));
    }

    private static string? FindCachedVersion(string globalPackagesFolder, string packageId)
    {
        var packageDir = Path.Combine(globalPackagesFolder, packageId.ToLowerInvariant());
        if (!Directory.Exists(packageDir))
        {
            return null;
        }

        return Directory.EnumerateDirectories(packageDir)
            .Where(d => File.Exists(Path.Combine(d, ".nupkg.metadata")))
            .Select(Path.GetFileName)
            .OrderBy(v => v, StringComparer.Ordinal)
            .LastOrDefault();
    }
}
