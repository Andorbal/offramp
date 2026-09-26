using System.Text.Json;
using System.Text.Json.Nodes;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Progress;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;
using Offramp.Analysis.Rules;
using Offramp.NuGet.Rules;

namespace Offramp.NuGet.Audit;

public sealed record DepsAuditRequest
{
    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required IPackageFeeds Feeds { get; init; }

    public required ICache Cache { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary>Audit only this package.</summary>
    public string? Package { get; init; }

    /// <summary>Audit only what this project (a model id) uses.</summary>
    public string? Project { get; init; }
}

/// <summary>
/// <c>offramp deps audit</c> (docs/spec/commands/deps.md): for every package in use,
/// which versions support the target, what to move to, and what blocks the move.
/// </summary>
public static class DepsAuditor
{
    public static async Task<DepsAuditResult> RunAsync(DepsAuditRequest request, CancellationToken cancellationToken)
    {
        var target = NuGetFramework.Parse(request.Config.TargetFramework);
        var packageMap = new PackageMap(request.Config.Deps.PackageMap);
        var ignored = request.Config.Deps.Ignore.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = request.Model.Packages
            .Where(p => !ignored.Contains(p.Key))
            .Where(p => request.Package is null || string.Equals(p.Key, request.Package, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Id: p.Key, Versions: Restrict(p.Value, request.Project)))
            .Where(p => p.Versions.Count > 0)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var audits = new List<PackageAudit>();
        var unreachable = new SortedSet<string>(StringComparer.Ordinal);
        using (var phase = request.Progress.BeginPhase("Auditing packages", 1, 1))
        {
            for (var i = 0; i < packages.Count; i++)
            {
                phase.Report(i, packages.Count, packages[i].Id);
                var audit = await AuditAsync(request, packages[i].Id, packages[i].Versions, target, packageMap, unreachable, cancellationToken);
                audits.Add(audit);
            }

            phase.Report(packages.Count, packages.Count);
        }

        foreach (var source in unreachable)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1006, $"The feed {source} could not be queried; packages that depend on it are marked unknown.",
                data: [KeyValuePair.Create<string, JsonNode?>("source", source)]);
        }

        return new DepsAuditResult
        {
            Target = request.Config.TargetFramework,
            Sources = request.Feeds.Sources,
            Packages = audits,
            AssemblyReferences = AssemblyReferences(request.Model, request.Project),
            Summary = new AuditSummary(
                audits.Count(a => a.Status == PackageStatus.Ok),
                audits.Count(a => a.Status == PackageStatus.Upgrade),
                audits.Count(a => a.Status == PackageStatus.Replace),
                audits.Count(a => a.Status == PackageStatus.Blocked),
                audits.Count(a => a.Status == PackageStatus.Unknown)),
            Partial = unreachable.Count > 0,
        };
    }

    private static async Task<PackageAudit> AuditAsync(
        DepsAuditRequest request, string id, SortedDictionary<string, IReadOnlyList<string>> inUseVersions, NuGetFramework target,
        PackageMap packageMap, SortedSet<string> unreachable, CancellationToken cancellationToken)
    {
        var available = await request.Feeds.GetVersionsAsync(id, cancellationToken);
        unreachable.UnionWith(available.UnreachableSources);
        var byVersion = available.Versions.ToDictionary(v => v.Version);
        var inUse = inUseVersions.Keys.Select(NuGetVersion.Parse).ToHashSet();
        var includePrerelease = request.Config.Deps.IncludePrerelease;
        var candidates = available.Versions
            .Where(v => inUse.Contains(v.Version) || (v.Listed && (includePrerelease || !v.Version.IsPrerelease)))
            .Select(v => v.Version)
            .Order()
            .ToList();

        var inspections = new Dictionary<NuGetVersion, PackageInspection?>();
        async Task<bool?> SupportsAsync(NuGetVersion version)
        {
            if (!inspections.TryGetValue(version, out var inspection))
            {
                inspection = await InspectAsync(request, id, version, cancellationToken);
                inspections[version] = inspection;
            }

            return inspection is null ? null : TargetSupport.Supports(inspection, target);
        }

        var supportsInUse = new SortedDictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var version in inUseVersions.Keys)
        {
            supportsInUse[version] = await SupportsAsync(NuGetVersion.Parse(version));
        }

        // Newest supporting: walk down from the newest candidate.
        NuGetVersion? newestSupporting = null;
        var newestIndex = -1;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            if (await SupportsAsync(candidates[i]) == true)
            {
                newestSupporting = candidates[i];
                newestIndex = i;
                break;
            }
        }

        // Lowest supporting: binary search below the newest supporting version, assuming
        // support, once added, is kept. Every returned version was inspected and supports.
        NuGetVersion? lowestSupporting = null;
        if (newestIndex >= 0)
        {
            int low = 0, high = newestIndex;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (await SupportsAsync(candidates[middle]) == true)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            lowestSupporting = candidates[high];
        }

        var newest = candidates.LastOrDefault(v => byVersion.TryGetValue(v, out var info) && info.Listed && (includePrerelease || !v.IsPrerelease));
        var pins = request.Config.Deps.Pins.Where(p => string.Equals(p.Package, id, StringComparison.OrdinalIgnoreCase)).ToList();
        var inUseList = inUseVersions
            .OrderBy(v => NuGetVersion.Parse(v.Key))
            .Select(v => new InUseVersion
            {
                Version = v.Key,
                Projects = v.Value,
                Pinned = pins.Any(p => p.Project is null || v.Value.Contains(RepoPath(p.Project), StringComparer.OrdinalIgnoreCase)),
                Deprecated = byVersion.TryGetValue(NuGetVersion.Parse(v.Key), out var info) ? info.Deprecation : null,
            })
            .ToList();

        var windowsEvidence = WindowsEvidence(inspections, inUse, newestSupporting, target);
        var replacement = packageMap.Find(id);
        var deprecated = newest is not null && byVersion.TryGetValue(newest, out var newestInfo) ? newestInfo.Deprecation : null;
        var status = Status(available, supportsInUse, newestSupporting, replacement);

        Report(request, id, status, inUseList, supportsInUse, newestSupporting, replacement, deprecated, windowsEvidence, available);
        return new PackageAudit
        {
            Id = id,
            InUse = inUseList,
            Target = request.Config.TargetFramework,
            SupportsTarget = new TargetSupportSummary { InUseVersions = supportsInUse },
            LowestSupporting = lowestSupporting?.ToNormalizedString(),
            NewestSupporting = newestSupporting?.ToNormalizedString(),
            Newest = newest?.ToNormalizedString(),
            NoVersionSupports = available.Found && newestSupporting is null,
            WindowsOnly = windowsEvidence is not null,
            WindowsOnlyEvidence = windowsEvidence,
            Deprecated = deprecated,
            Replacement = replacement,
            Status = status,
        };
    }

    private static PackageStatus Status(
        PackageVersions available, SortedDictionary<string, bool?> supportsInUse, NuGetVersion? newestSupporting, PackageReplacement? replacement)
    {
        if (!available.Found)
        {
            return PackageStatus.Unknown;
        }

        if (supportsInUse.Values.All(s => s == true))
        {
            return PackageStatus.Ok;
        }

        if (newestSupporting is not null)
        {
            return PackageStatus.Upgrade;
        }

        return replacement is not null ? PackageStatus.Replace : PackageStatus.Blocked;
    }

    /// <summary>Windows-only evidence for what the audit recommends: the newest supporting version, else the in-use ones.</summary>
    private static string? WindowsEvidence(
        Dictionary<NuGetVersion, PackageInspection?> inspections, HashSet<NuGetVersion> inUse, NuGetVersion? newestSupporting, NuGetFramework target)
    {
        var order = new List<NuGetVersion>();
        if (newestSupporting is not null)
        {
            order.Add(newestSupporting);
        }

        order.AddRange(inUse.Order());
        foreach (var version in order)
        {
            if (inspections.GetValueOrDefault(version) is { } inspection
                && TargetSupport.Supports(inspection, target)
                && TargetSupport.WindowsOnly(inspection, target) is { } evidence)
            {
                return $"{version.ToNormalizedString()} {evidence}";
            }
        }

        return null;
    }

    private static void Report(
        DepsAuditRequest request, string id, PackageStatus status, IReadOnlyList<InUseVersion> inUse, SortedDictionary<string, bool?> supportsInUse,
        NuGetVersion? newestSupporting, PackageReplacement? replacement, PackageDeprecation? deprecated, string? windowsEvidence, PackageVersions available)
    {
        var target = request.Config.TargetFramework;
        KeyValuePair<string, JsonNode?> Package() => KeyValuePair.Create<string, JsonNode?>("package", id);
        if (!available.Found && available.UnreachableSources.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1005, $"{id} was not found on any feed ({string.Join(", ", request.Feeds.Sources)}).", data: [Package()]);
        }

        if (status is PackageStatus.Replace or PackageStatus.Blocked)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1001,
                $"No version of {id} supports {target}" + (replacement is null ? "; there is no known successor." : $"; replace it with {replacement.Replacement}."),
                data: [Package(), KeyValuePair.Create<string, JsonNode?>("replacement", replacement?.Replacement)]);
        }
        else if (status == PackageStatus.Upgrade)
        {
            foreach (var version in inUse.Where(v => supportsInUse[v.Version] == false))
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR1002,
                    $"{id} {version.Version} does not support {target}; {newestSupporting!.ToNormalizedString()} does.",
                    version.Projects.Count == 1 ? new DiagnosticLocation(Project: version.Projects[0]) : default,
                    [Package(), KeyValuePair.Create<string, JsonNode?>("version", version.Version),
                     KeyValuePair.Create<string, JsonNode?>("projects", new JsonArray([.. version.Projects.Select(p => (JsonNode?)p)]))]);
            }
        }

        foreach (var version in inUse.Where(v => v.Deprecated is not null))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1003,
                $"{id} {version.Version} is deprecated ({string.Join(", ", version.Deprecated!.Reasons)})"
                    + (version.Deprecated.AlternateId is null ? "." : $"; the feed suggests {version.Deprecated.AlternateId}."),
                data: [Package(), KeyValuePair.Create<string, JsonNode?>("version", version.Version),
                       KeyValuePair.Create<string, JsonNode?>("alternate", version.Deprecated.AlternateId)]);
        }

        if (deprecated is not null && inUse.All(v => v.Deprecated is null))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1003,
                $"{id} is deprecated ({string.Join(", ", deprecated.Reasons)})" + (deprecated.AlternateId is null ? "." : $"; the feed suggests {deprecated.AlternateId}."),
                data: [Package(), KeyValuePair.Create<string, JsonNode?>("alternate", deprecated.AlternateId)]);
        }

        if (windowsEvidence is not null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1004,
                $"{id} only works on Windows on {target}: {windowsEvidence}.",
                data: [Package(), KeyValuePair.Create<string, JsonNode?>("evidence", windowsEvidence)]);
        }
    }

    private static async Task<PackageInspection?> InspectAsync(DepsAuditRequest request, string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var ns = "packages/" + id.ToLowerInvariant();
        var key = version.ToNormalizedString().ToLowerInvariant();
        if (request.Cache.TryGet(ns, key, out var cached))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(cached, NuGetJsonContext.Default.PackageInspection);
                if (parsed is { Format: PackageInspection.CurrentFormat })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // A corrupt entry is recomputed.
            }
        }

        var nupkg = await request.Feeds.DownloadAsync(id, version, cancellationToken);
        if (nupkg is null)
        {
            return null;
        }

        var inspection = PackageInspector.Inspect(nupkg);
        request.Cache.Set(ns, key, OfframpJson.Serialize(inspection, NuGetJsonContext.Default.PackageInspection));
        return inspection;
    }

    private static SortedDictionary<string, IReadOnlyList<string>> Restrict(PackageUsage usage, string? project)
    {
        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (version, projects) in usage.Versions)
        {
            var kept = project is null ? projects : [.. projects.Where(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase))];
            if (kept.Count > 0)
            {
                result[version] = kept;
            }
        }

        return result;
    }

    private static IReadOnlyList<AssemblyReferenceAudit> AssemblyReferences(WorkspaceModel model, string? project) =>
        [.. model.Projects
            .Where(p => project is null || string.Equals(p.Id, project, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .SelectMany(p => p.AssemblyReferences.Select(r => new AssemblyReferenceAudit
            {
                Project = p.Id,
                Name = r.Name,
                Kind = r.Kind,
                HintPath = r.HintPath,
                Mapping = r.Kind == AssemblyReferenceKind.Framework ? FrameworkAssemblyMap.Find(r.Name) : null,
            }))];

    private static string RepoPath(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}
