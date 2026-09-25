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

public sealed record ReferenceAssembliesResult(ReferenceAssembliesState State, string? Detail);

/// <summary>Can <c>net4x</c> targets compile here? Replaced by a fake in tests.</summary>
public interface IReferenceAssembliesProbe
{
    Task<ReferenceAssembliesResult> ProbeAsync(string repositoryRoot, CancellationToken cancellationToken);
}

/// <summary>
/// Looks for <c>Microsoft.NETFramework.ReferenceAssemblies.net48</c> in the global
/// packages folder, then the installed targeting pack, then the feeds of the
/// repository's <c>nuget.config</c>.
/// </summary>
public sealed class ReferenceAssembliesProbe : IReferenceAssembliesProbe
{
    public const string PackageId = "Microsoft.NETFramework.ReferenceAssemblies.net48";
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(20);

    public async Task<ReferenceAssembliesResult> ProbeAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var settings = Settings.LoadDefaultSettings(repositoryRoot);
        var globalPackages = SettingsUtility.GetGlobalPackagesFolder(settings);
        var cached = FindCachedVersion(globalPackages);
        if (cached is not null)
        {
            return new ReferenceAssembliesResult(ReferenceAssembliesState.Cached, cached);
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
            var pack = Path.Combine(programFiles, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8");
            if (Directory.Exists(pack))
            {
                return new ReferenceAssembliesResult(ReferenceAssembliesState.TargetingPack, "v4.8");
            }
        }

        var sources = new PackageSourceProvider(settings).LoadPackageSources()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var unreachable = new List<string>();
        using var cache = new SourceCacheContext { NoCache = false };
        foreach (var source in sources)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FeedTimeout);
            try
            {
                var repository = Repository.Factory.GetCoreV3(source);
                var resource = await repository.GetResourceAsync<FindPackageByIdResource>(timeout.Token)
                    ?? throw new InvalidOperationException($"Feed {source.Name} has no package-by-id resource.");
                var versions = await resource.GetAllVersionsAsync(PackageId, cache, NullLogger.Instance, timeout.Token);
                if (versions.Any())
                {
                    return new ReferenceAssembliesResult(ReferenceAssembliesState.AvailableFromFeed, source.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                unreachable.Add(source.Name);
            }
        }

        return unreachable.Count > 0
            ? new ReferenceAssembliesResult(ReferenceAssembliesState.FeedUnreachable, string.Join(", ", unreachable))
            : new ReferenceAssembliesResult(ReferenceAssembliesState.NotFound, null);
    }

    private static string? FindCachedVersion(string globalPackagesFolder)
    {
        var packageDir = Path.Combine(globalPackagesFolder, PackageId.ToLowerInvariant());
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
