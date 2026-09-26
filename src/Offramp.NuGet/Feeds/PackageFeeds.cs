using NuGet.Versioning;

namespace Offramp.NuGet.Feeds;

/// <summary>Why a version is deprecated, as the feed reports it.</summary>
public sealed record PackageDeprecation(IReadOnlyList<string> Reasons, string? Message, string? AlternateId, string? AlternateRange);

public sealed record PackageVersionInfo(NuGetVersion Version, bool Listed, PackageDeprecation? Deprecation);

/// <summary>Every version of a package across the configured feeds, and the feeds that could not be asked.</summary>
public sealed record PackageVersions(string Id, IReadOnlyList<PackageVersionInfo> Versions, IReadOnlyList<string> UnreachableSources)
{
    public bool Found => Versions.Count > 0;
}

/// <summary>Package feeds as the repository's NuGet configuration defines them.</summary>
public interface IPackageFeeds
{
    /// <summary>The feed names or URLs in use, for messages.</summary>
    IReadOnlyList<string> Sources { get; }

    Task<PackageVersions> GetVersionsAsync(string id, CancellationToken cancellationToken);

    /// <summary>The .nupkg bytes, from the global packages folder when present, else the first feed that has it; null when none has.</summary>
    Task<byte[]?> DownloadAsync(string id, NuGetVersion version, CancellationToken cancellationToken);
}
