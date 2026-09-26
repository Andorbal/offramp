using System.Text.Json;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Json;
using Offramp.NuGet.Feeds;

namespace Offramp.NuGet.Inspection;

/// <summary>
/// Package inspections by id and version: from memory, from <c>.offramp/cache/packages/</c>
/// (a published version never changes), or by downloading and inspecting the nupkg.
/// </summary>
public sealed class PackageInspections(IPackageFeeds feeds, ICache cache)
{
    private readonly Dictionary<(string, NuGetVersion), PackageInspection?> _known = [];

    /// <summary>The inspection, or null when no feed has the version.</summary>
    public async Task<PackageInspection?> GetAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var key = (id.ToLowerInvariant(), version);
        if (_known.TryGetValue(key, out var known))
        {
            return known;
        }

        var ns = "packages/" + id.ToLowerInvariant();
        var entry = version.ToNormalizedString().ToLowerInvariant();
        PackageInspection? inspection = null;
        if (cache.TryGet(ns, entry, out var cached))
        {
            try
            {
                inspection = JsonSerializer.Deserialize(cached, NuGetJsonContext.Default.PackageInspection) is { Format: PackageInspection.CurrentFormat } parsed ? parsed : null;
            }
            catch (JsonException)
            {
                inspection = null;
            }
        }

        if (inspection is null && await feeds.DownloadAsync(id, version, cancellationToken) is { } nupkg)
        {
            inspection = PackageInspector.Inspect(nupkg);
            cache.Set(ns, entry, OfframpJson.Serialize(inspection, NuGetJsonContext.Default.PackageInspection));
        }

        _known[key] = inspection;
        return inspection;
    }
}
