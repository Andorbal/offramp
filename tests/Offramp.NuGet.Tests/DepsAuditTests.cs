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

    /// <summary>
    /// SmartStoreNET field test (P0 #4): EntityFramework.SqlServerCompact 6.4.4 was "upgraded" to 4.3.1,
    /// a release with only content transforms and an install script; LibSassHost's native Windows
    /// package was <c>ok</c>. A package whose later releases dropped their portable build is not an upgrade either.
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR1007")]
    public async Task Versions_without_assemblies_or_older_than_the_one_in_use_are_never_an_upgrade()
    {
        static RecordedFile Dll(string path, string name) => new() { Path = path, Assembly = new RecordedAssembly { Name = name, Version = "1.0.0.0" } };
        var recording = new FeedRecording
        {
            Source = "synthetic",
            RecordedAt = "2026-09-29",
            Packages =
            [
                new RecordedPackage
                {
                    Id = "EntityFramework.SqlServerCompact", Version = "4.3.1", Synthetic = true,
                    DependencyGroups = [new RecordedDependencyGroup("", [new RecordedDependency("EntityFramework", "[4.3.1, )")])],
                    Files = [new RecordedFile { Path = "Content/App.config.transform", Content = "<configuration />" }, new RecordedFile { Path = "tools/install.ps1", Content = "param()" }],
                },
                new RecordedPackage
                {
                    Id = "EntityFramework.SqlServerCompact", Version = "6.4.4", Synthetic = true,
                    Files = [Dll("lib/net45/EntityFramework.SqlServerCompact.dll", "EntityFramework.SqlServerCompact")],
                },
                new RecordedPackage { Id = "Contoso.Dropped", Version = "1.0.0", Synthetic = true, Files = [Dll("lib/netstandard2.0/Contoso.Dropped.dll", "Contoso.Dropped")] },
                new RecordedPackage { Id = "Contoso.Dropped", Version = "2.0.0", Synthetic = true, Files = [Dll("lib/net45/Contoso.Dropped.dll", "Contoso.Dropped")] },
                new RecordedPackage
                {
                    Id = "LibSassHost.Native.win-x64", Version = "1.3.3", Synthetic = true,
                    Files = [new RecordedFile { Path = "runtimes/win-x64/native/libsass.dll" }, new RecordedFile { Path = "build/LibSassHost.Native.win-x64.props", Content = "<Project />" }],
                },
                new RecordedPackage { Id = "LibSassHost.Native.linux-x64", Version = "1.3.3", Synthetic = true, Files = [new RecordedFile { Path = "runtimes/linux-x64/native/libsass.so" }] },
            ],
        };
        using var root = new ScratchDirectory();
        static PackageUsage InUse(string version) => new() { Versions = new(StringComparer.Ordinal) { [version] = ["src/Web/Web.csproj"] } };
        var model = FixtureModels.Load("versions") with
        {
            Packages = new SortedDictionary<string, PackageUsage>(StringComparer.Ordinal)
            {
                ["Contoso.Dropped"] = InUse("2.0.0"),
                ["EntityFramework.SqlServerCompact"] = InUse("6.4.4"),
                ["LibSassHost.Native.win-x64"] = InUse("1.3.3"),
            },
        };
        var bag = new DiagnosticBag();

        var result = await DepsAuditor.RunAsync(new DepsAuditRequest
        {
            Model = model, Config = Config(root.Path), Feeds = new RecordedPackageFeeds(recording), Cache = NullCache.Instance, Diagnostics = bag,
        }, TestContext.Current.CancellationToken);
        var byId = result.Packages.ToDictionary(p => p.Id, StringComparer.Ordinal);

        var compact = byId["EntityFramework.SqlServerCompact"];
        Assert.NotEqual(PackageStatus.Upgrade, compact.Status);
        Assert.Null(compact.NewestSupporting);
        Assert.True(compact.NoVersionSupports);
        var dropped = byId["Contoso.Dropped"];
        Assert.Equal((PackageStatus.Blocked, "1.0.0"), (dropped.Status, dropped.NewestSupporting));
        var downgrade = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR1007");
        Assert.Contains("Contoso.Dropped 2.0.0 does not support net10.0", downgrade.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(bag.ToSortedList(), d => d.Code == "OFR1002");
        var sass = byId["LibSassHost.Native.win-x64"];
        Assert.Equal((PackageStatus.Ok, true), (sass.Status, sass.WindowsOnly));
        Assert.Equal("1.3.3 runtimes/win-x64/native/libsass.dll: native code for Windows only", sass.WindowsOnlyEvidence);
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR1004" && d.Message.EndsWith("the feed has LibSassHost.Native.linux-x64 for Linux.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Open Live Writer field test (P2): Microsoft.Bcl.Build, whose only files are build targets, was
    /// `ok` although the package map says it is built in on modern .NET (its targets fail under the
    /// SDK's MSBuild). A build-only package the map does not know stays `ok`.
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR1009")]
    public async Task A_package_with_nothing_for_any_framework_follows_the_package_map()
    {
        var recording = new FeedRecording
        {
            Source = "synthetic",
            RecordedAt = "2026-09-29",
            Packages =
            [
                new RecordedPackage { Id = "Microsoft.Bcl.Build", Version = "1.0.21", Synthetic = true, Files = [new RecordedFile { Path = "build/Microsoft.Bcl.Build.targets", Content = "<Project />" }] },
                new RecordedPackage { Id = "NUnitTestAdapter", Version = "2.2.0", Synthetic = true, Files = [new RecordedFile { Path = "build/NUnitTestAdapter.props", Content = "<Project />" }] },
            ],
        };
        using var root = new ScratchDirectory();
        static PackageUsage InUse(string version) => new() { Versions = new(StringComparer.Ordinal) { [version] = ["src/Web/Web.csproj"] } };
        var model = FixtureModels.Load("versions") with
        {
            Packages = new SortedDictionary<string, PackageUsage>(StringComparer.Ordinal) { ["Microsoft.Bcl.Build"] = InUse("1.0.21"), ["NUnitTestAdapter"] = InUse("2.2.0") },
        };
        var bag = new DiagnosticBag();

        var result = await DepsAuditor.RunAsync(new DepsAuditRequest
        {
            Model = model, Config = Config(root.Path), Feeds = new RecordedPackageFeeds(recording), Cache = NullCache.Instance, Diagnostics = bag,
        }, TestContext.Current.CancellationToken);

        var bcl = result.Packages.Single(p => p.Id == "Microsoft.Bcl.Build");
        Assert.Equal((PackageStatus.Replace, "built in on modern .NET"), (bcl.Status, bcl.Replacement!.Replacement));
        Assert.Equal(PackageStatus.Ok, result.Packages.Single(p => p.Id == "NUnitTestAdapter").Status);
        var empty = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR1009");
        Assert.Equal("Microsoft.Bcl.Build 1.0.21 has nothing for any framework (only build or tool files), so it does nothing for net10.0; the package map says: built in on modern .NET.", empty.Message);
        Assert.DoesNotContain(bag.ToSortedList(), d => d.Code is "OFR1001" or "OFR1007");
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
