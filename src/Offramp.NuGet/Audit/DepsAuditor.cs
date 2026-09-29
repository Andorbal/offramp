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
        async Task<PackageInspection?> GetAsync(NuGetVersion version)
        {
            if (!inspections.TryGetValue(version, out var inspection))
            {
                inspection = await InspectAsync(request, id, version, cancellationToken);
                inspections[version] = inspection;
            }

            return inspection;
        }

        var supportsInUse = new SortedDictionary<string, bool?>(StringComparer.Ordinal);
        var inUseHasAssemblies = false;
        foreach (var version in inUseVersions.Keys)
        {
            var inspection = await GetAsync(NuGetVersion.Parse(version));
            supportsInUse[version] = inspection is null ? null : TargetSupport.Supports(inspection, target);
            inUseHasAssemblies |= inspection is not null && TargetSupport.HasAssemblies(inspection);
        }

        // A candidate supports the target when its assets do, and it has assemblies if the version in use has:
        // a content-only or tools-only release has nothing to be incompatible with, and nothing to replace a library with.
        async Task<bool?> SupportsAsync(NuGetVersion version) =>
            await GetAsync(version) is { } inspection
                ? TargetSupport.Supports(inspection, target) && (!inUseHasAssemblies || TargetSupport.HasAssemblies(inspection))
                : null;

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

        // Evidence for what the audit recommends: a supporting version older than one in use is not a recommendation.
        var recommended = supportsInUse.Values.All(s => s == true) || IsUpgrade(supportsInUse, newestSupporting) ? newestSupporting : null;
        var windowsEvidence = WindowsEvidence(inspections, inUse, recommended, target);
        var replacement = packageMap.Find(id);
        var deprecated = newest is not null && byVersion.TryGetValue(newest, out var newestInfo) ? newestInfo.Deprecation : null;
        var status = Status(available, supportsInUse, newestSupporting, replacement);
        var forOtherSystems = windowsEvidence is not null && inspections.Values.Any(i => i is not null && TargetSupport.WindowsNativeOnly(i) is not null)
            ? await OtherSystemsPackageAsync(request, id, cancellationToken)
            : null;

        Report(request, id, status, inUseList, supportsInUse, newestSupporting, replacement, deprecated, windowsEvidence, available, forOtherSystems);
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

        if (IsUpgrade(supportsInUse, newestSupporting))
        {
            return PackageStatus.Upgrade;
        }

        return replacement is not null ? PackageStatus.Replace : PackageStatus.Blocked;
    }

    /// <summary>True when a supporting version is newer than every in-use version that does not support the target: a lower one is a downgrade, never an upgrade.</summary>
    private static bool IsUpgrade(SortedDictionary<string, bool?> supportsInUse, NuGetVersion? newestSupporting) =>
        newestSupporting is not null
        && supportsInUse.Where(s => s.Value == false).All(s => newestSupporting > NuGetVersion.Parse(s.Key));

    /// <summary>
    /// For a <c>*.win-x64</c>-style native package (the id ends in a Windows runtime identifier),
    /// the same id for <c>linux-x64</c> when a feed has it; null otherwise.
    /// </summary>
    private static async Task<string?> OtherSystemsPackageAsync(DepsAuditRequest request, string id, CancellationToken cancellationToken)
    {
        var dot = id.LastIndexOf('.');
        var suffix = dot <= 0 ? "" : id[(dot + 1)..];
        var windowsRid = suffix.StartsWith("win", StringComparison.OrdinalIgnoreCase) && (suffix.Length == 3 || suffix[3] == '-' || char.IsAsciiDigit(suffix[3]));
        if (!windowsRid)
        {
            return null;
        }

        var linux = id[..(dot + 1)] + "linux-x64";
        return (await request.Feeds.GetVersionsAsync(linux, cancellationToken)).Found ? linux : null;
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
        NuGetVersion? newestSupporting, PackageReplacement? replacement, PackageDeprecation? deprecated, string? windowsEvidence, PackageVersions available,
        string? forOtherSystems)
    {
        var target = request.Config.TargetFramework;
        KeyValuePair<string, JsonNode?> Package() => KeyValuePair.Create<string, JsonNode?>("package", id);
        if (!available.Found && available.UnreachableSources.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1005, $"{id} was not found on any feed ({string.Join(", ", request.Feeds.Sources)}).", data: [Package()]);
        }

        if ((status is PackageStatus.Replace or PackageStatus.Blocked) && newestSupporting is not null)
        {
            // Only versions older than one in use support the target: moving back is not an upgrade.
            var unsupported = inUse.Where(v => supportsInUse[v.Version] == false).Select(v => v.Version).ToList();
            request.Diagnostics.Report(DiagnosticCatalog.OFR1007,
                $"{id} {string.Join(", ", unsupported)} does not support {target}, and no newer version does; only older ones do (the newest is {newestSupporting.ToNormalizedString()}), and a downgrade is not a way forward"
                    + (replacement is null ? "." : $". Replace it with {replacement.Replacement}."),
                data: [Package(), KeyValuePair.Create<string, JsonNode?>("versions", new JsonArray([.. unsupported.Select(v => (JsonNode?)v)])),
                       KeyValuePair.Create<string, JsonNode?>("newestSupporting", newestSupporting.ToNormalizedString()),
                       KeyValuePair.Create<string, JsonNode?>("replacement", replacement?.Replacement)]);
        }
        else if (status is PackageStatus.Replace or PackageStatus.Blocked)
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
                $"{id} only works on Windows on {target}: {windowsEvidence}" + (forOtherSystems is null ? "." : $"; the feed has {forOtherSystems} for Linux."),
                data: forOtherSystems is null
                    ? [Package(), KeyValuePair.Create<string, JsonNode?>("evidence", windowsEvidence)]
                    : [Package(), KeyValuePair.Create<string, JsonNode?>("evidence", windowsEvidence), KeyValuePair.Create<string, JsonNode?>("linux", forOtherSystems)]);
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
