using NuGet.Versioning;
using Offramp.NuGet.Feeds;

namespace Offramp.Fixtures.Feeds;

/// <summary>
/// A feed answering from a recording, including what folder feeds cannot express:
/// listing state and deprecation. Counts downloads so tests can check the search is lazy.
/// </summary>
public sealed class RecordedPackageFeeds(FeedRecording recording) : IPackageFeeds
{
    public int Downloads { get; private set; }

    public IReadOnlyList<string> Sources { get; } = ["recorded"];

    public Task<PackageVersions> GetVersionsAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(new PackageVersions(id,
            [.. recording.Packages
                .Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                .Select(p => new PackageVersionInfo(NuGetVersion.Parse(p.Version), p.Listed, p.Deprecation is null ? null
                    : new PackageDeprecation(p.Deprecation.Reasons, p.Deprecation.Message, p.Deprecation.AlternateId, p.Deprecation.AlternateRange)))
                .OrderBy(v => v.Version)],
            []));

    public Task<byte[]?> DownloadAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var package = recording.Packages.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase) && NuGetVersion.Parse(p.Version) == version);
        if (package is not null)
        {
            Downloads++;
        }

        return Task.FromResult(package is null ? null : FeedMaterializer.Nupkg(package));
    }
}
