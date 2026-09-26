using System.Text.Json.Nodes;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.NuGet.Audit;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;

namespace Offramp.NuGet.Tests;

/// <summary>
/// Roadmap M2 acceptance: `deps audit` on `versions` identifies lowest and newest
/// supporting versions correctly against a recorded feed (a local folder feed, so the
/// answers never depend on nuget.org).
/// </summary>
public sealed class DepsAuditTests
{
    private static async Task<(DepsAuditResult Result, DiagnosticBag Diagnostics)> AuditWithFolderFeedAsync(Func<DepsAuditRequest, DepsAuditRequest>? customize = null)
    {
        var scanned = await ScannedFixtures.GetAsync("versions");
        using var feed = new ScratchDirectory("feed");
        VersionsFeed.WriteFolderFeed(feed.Path);
        using var feeds = NuGetPackageFeeds.ForSources(scanned.Root, [feed.Path]);
        var bag = new DiagnosticBag();
        var request = new DepsAuditRequest
        {
            Model = scanned.Outcome.Model!,
            Config = Config(scanned.Root),
            Feeds = feeds,
            Cache = NullCache.Instance,
            Diagnostics = bag,
        };
        var result = await DepsAuditor.RunAsync(customize?.Invoke(request) ?? request, TestContext.Current.CancellationToken);
        return (result, bag);
    }

    private static OfframpConfig Config(string root) => ConfigLoader.Load(new ConfigSources { RepositoryRoot = root }).Config;

    [Fact]
    [ProducesDiagnostic("OFR1001")]
    [ProducesDiagnostic("OFR1002")]
    [ProducesDiagnostic("OFR1004")]
    public async Task Versions_fixture_against_the_recorded_folder_feed()
    {
        var (result, bag) = await AuditWithFolderFeedAsync();
        var byId = result.Packages.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        var newtonsoft = byId["Newtonsoft.Json"];
        Assert.Equal(PackageStatus.Ok, newtonsoft.Status);
        Assert.Equal(["9.0.1", "11.0.2", "12.0.3", "13.0.1"], newtonsoft.InUse.Select(v => v.Version));
        Assert.True(newtonsoft.InUse.Single(v => v.Version == "9.0.1").Pinned);
        Assert.Equal("13.0.3", newtonsoft.NewestSupporting);
        Assert.Equal("13.0.3", newtonsoft.Newest);

        var entityFramework = byId["EntityFramework"];
        Assert.Equal(PackageStatus.Upgrade, entityFramework.Status);
        Assert.False(entityFramework.SupportsTarget.InUseVersions["6.2.0"]);
        Assert.Equal("6.4.4", entityFramework.LowestSupporting);
        Assert.Equal("6.5.1", entityFramework.NewestSupporting);

        var webApi = byId["Microsoft.AspNet.WebApi.Core"];
        Assert.Equal(PackageStatus.Replace, webApi.Status);
        Assert.True(webApi.NoVersionSupports);
        Assert.Null(webApi.LowestSupporting);
        Assert.StartsWith("ASP.NET Core MVC", webApi.Replacement!.Replacement, StringComparison.Ordinal);

        Assert.Equal(PackageStatus.Blocked, byId["Contoso.Legacy.Reports"].Status);
        Assert.True(byId["System.Drawing.Common"].WindowsOnly);
        Assert.Contains("windows6.1", byId["System.Drawing.Common"].WindowsOnlyEvidence, StringComparison.Ordinal);
        Assert.Contains("references System.Windows.Forms", byId["Contoso.Windows.Controls"].WindowsOnlyEvidence, StringComparison.Ordinal);
        Assert.Equal(PackageStatus.Ok, byId["WindowsAzure.Storage"].Status);
        Assert.NotNull(byId["WindowsAzure.Storage"].Replacement);

        var codes = bag.ToSortedList().Select(d => d.Code).ToList();
        Assert.Contains("OFR1001", codes);
        Assert.Contains("OFR1002", codes);
        Assert.Contains("OFR1004", codes);
        Assert.Equal(new AuditSummary(Ok: 7, Upgrade: 1, Replace: 1, Blocked: 1, Unknown: 0), result.Summary);
    }

    /// <summary>The binary search for the lowest supporting version agrees with inspecting every version.</summary>
    [Fact]
    public async Task Lowest_and_newest_supporting_agree_with_inspecting_every_version()
    {
        var (result, _) = await AuditWithFolderFeedAsync();
        var target = NuGetFramework.Parse("net10.0");
        var recording = VersionsFeed.Load();

        foreach (var audit in result.Packages)
        {
            var inUse = audit.InUse.Select(v => NuGetVersion.Parse(v.Version)).ToHashSet();
            var supporting = recording.Packages
                .Where(p => string.Equals(p.Id, audit.Id, StringComparison.OrdinalIgnoreCase))
                .Where(p => inUse.Contains(NuGetVersion.Parse(p.Version)) || (p.Listed && !NuGetVersion.Parse(p.Version).IsPrerelease))
                .Where(p => TargetSupport.Supports(PackageInspector.Inspect(FeedMaterializer.Nupkg(p)), target))
                .Select(p => NuGetVersion.Parse(p.Version))
                .Order()
                .ToList();

            Assert.Equal(supporting.FirstOrDefault()?.ToNormalizedString(), audit.LowestSupporting);
            Assert.Equal(supporting.LastOrDefault()?.ToNormalizedString(), audit.NewestSupporting);
        }
    }

    [Fact]
    public async Task Result_matches_the_snapshot_and_the_schema()
    {
        var (result, _) = await AuditWithFolderFeedAsync();
        var json = JsonNode.Parse(OfframpJson.Serialize(result, NuGetJsonContext.Default.DepsAuditResult))!;
        json["sources"] = new JsonArray("{Feed}");

        SchemaAssert.Valid("deps-audit", json.ToJsonString());
        await Verify(OfframpJson.Format(json), extension: "json");
    }

    [Fact]
    [ProducesDiagnostic("OFR1003")]
    public async Task Deprecation_from_the_feed_is_reported()
    {
        var scanned = await ScannedFixtures.GetAsync("versions");
        var bag = new DiagnosticBag();

        var result = await DepsAuditor.RunAsync(new DepsAuditRequest
        {
            Model = scanned.Outcome.Model!,
            Config = Config(scanned.Root),
            Feeds = new RecordedPackageFeeds(VersionsFeed.Load()),
            Cache = NullCache.Instance,
            Diagnostics = bag,
            Package = "WindowsAzure.Storage",
        }, TestContext.Current.CancellationToken);

        var storage = Assert.Single(result.Packages);
        Assert.Equal(["Legacy"], storage.Deprecated!.Reasons);
        Assert.Equal("Azure.Storage.Common", storage.InUse.Single().Deprecated!.AlternateId);
        var diagnostic = bag.ToSortedList().Single(d => d.Code == "OFR1003");
        Assert.Contains("Azure.Storage.Common", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspections_are_cached_and_the_search_is_lazy()
    {
        var scanned = await ScannedFixtures.GetAsync("versions");
        using var cacheDirectory = new ScratchDirectory("cache");
        var cache = new FileCache(cacheDirectory.Path);
        var feeds = new RecordedPackageFeeds(VersionsFeed.Load());
        DepsAuditRequest Request() => new()
        {
            Model = scanned.Outcome.Model!,
            Config = Config(scanned.Root),
            Feeds = feeds,
            Cache = cache,
            Diagnostics = new DiagnosticBag(),
            Package = "Newtonsoft.Json",
        };

        await DepsAuditor.RunAsync(Request(), TestContext.Current.CancellationToken);
        var first = feeds.Downloads;
        await DepsAuditor.RunAsync(Request(), TestContext.Current.CancellationToken);

        // Four in-use versions, the newest, and a binary search: fewer than the 11 candidates.
        Assert.InRange(first, 5, 9);
        Assert.Equal(first, feeds.Downloads);
        Assert.True(File.Exists(Path.Combine(cacheDirectory.Path, "packages", "newtonsoft.json", "13.0.3.json")));
    }

    [Fact]
    [ProducesDiagnostic("OFR1005")]
    public async Task A_package_no_feed_has_is_unknown()
    {
        var scanned = await ScannedFixtures.GetAsync("versions");
        var model = scanned.Outcome.Model! with
        {
            Packages = new SortedDictionary<string, PackageUsage>(StringComparer.Ordinal)
            {
                ["Contoso.Missing"] = new() { Versions = new(StringComparer.Ordinal) { ["1.0.0"] = ["src/Billing/Billing.csproj"] } },
            },
        };
        var bag = new DiagnosticBag();

        var result = await DepsAuditor.RunAsync(new DepsAuditRequest
        {
            Model = model, Config = Config(scanned.Root), Feeds = new RecordedPackageFeeds(VersionsFeed.Load()), Cache = NullCache.Instance, Diagnostics = bag,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PackageStatus.Unknown, result.Packages.Single().Status);
        Assert.Equal("OFR1005", bag.ToSortedList().Single().Code);
    }

    [Fact]
    [ProducesDiagnostic("OFR1006")]
    public async Task An_unreachable_feed_makes_the_result_partial()
    {
        var scanned = await ScannedFixtures.GetAsync("versions");
        using var feeds = NuGetPackageFeeds.ForSources(scanned.Root, ["http://127.0.0.1:9/v3/index.json"]);
        var bag = new DiagnosticBag();

        var result = await DepsAuditor.RunAsync(new DepsAuditRequest
        {
            Model = scanned.Outcome.Model!, Config = Config(scanned.Root), Feeds = feeds, Cache = NullCache.Instance, Diagnostics = bag, Package = "Newtonsoft.Json",
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Partial);
        Assert.Equal(PackageStatus.Unknown, result.Packages.Single().Status);
        Assert.Equal(["OFR1006"], bag.ToSortedList().Select(d => d.Code));
    }

    [Fact]
    public async Task Ignored_packages_and_project_filter()
    {
        var (result, _) = await AuditWithFolderFeedAsync(r => r with
        {
            Config = r.Config with { Deps = r.Config.Deps with { Ignore = ["Newtonsoft.Json"] } },
            Project = "src/Billing/Billing.csproj",
        });

        Assert.DoesNotContain(result.Packages, p => p.Id == "Newtonsoft.Json");
        Assert.All(result.Packages, p => Assert.All(p.InUse, v => Assert.Equal(["src/Billing/Billing.csproj"], v.Projects)));
        Assert.All(result.AssemblyReferences, r => Assert.Equal("src/Billing/Billing.csproj", r.Project));
        Assert.Contains(result.AssemblyReferences, r => r.Name == "System.Drawing" && r.Mapping!.Package == "System.Drawing.Common");
    }
}
