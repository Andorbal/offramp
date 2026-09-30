using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.NuGet.Audit;
using Offramp.NuGet.Feeds;

namespace Offramp.NuGet.Tests;

/// <summary>Open Live Writer and NHibernate field tests (P2): how `deps audit` reaches packages, and what it points to when there are none.</summary>
public sealed class FeedAccessTests
{
    /// <summary>A feed that accepts connections and never answers took 1,668 s on Open Live Writer; each request now gives up.</summary>
    [Fact]
    public async Task A_feed_that_never_answers_is_unreachable_after_the_timeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = new List<Socket>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await listener.AcceptSocketAsync());
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Stopped.
            }
        });
        try
        {
            using var root = new ScratchDirectory();
            var source = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v3/index.json";
            using var feeds = NuGetPackageFeeds.ForSources(root.Path, [source], timeout: TimeSpan.FromSeconds(2));
            var clock = Stopwatch.StartNew();

            var versions = await feeds.GetVersionsAsync("Newtonsoft.Json", TestContext.Current.CancellationToken);

            Assert.Equal([source], versions.UnreachableSources);
            Assert.False(versions.Found);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), clock.Elapsed.ToString());
        }
        finally
        {
            listener.Stop();
            await accepting;
            accepted.ForEach(s => s.Dispose());
        }
    }

    /// <summary>`scan` restores packages.config packages with their .nupkg; the audit reads them there instead of downloading them again.</summary>
    [Fact]
    public async Task A_package_in_a_packages_config_folder_is_read_from_there()
    {
        using var root = new ScratchDirectory();
        var nupkg = FeedMaterializer.Nupkg(VersionsFeed.Load().Packages.Single(p => p.Id == "Newtonsoft.Json" && p.Version == "13.0.3"));
        File.WriteAllBytes(root.Write("packages/Newtonsoft.Json.13.0.3/Newtonsoft.Json.13.0.3.nupkg", ""), nupkg);
        Directory.CreateDirectory(root.Combine("empty-feed"));
        using var feeds = NuGetPackageFeeds.ForSources(root.Path, ["empty-feed"], packagesFolders: [root.Combine("packages")]);

        var bytes = await feeds.DownloadAsync("Newtonsoft.Json", NuGetVersion.Parse("13.0.3"), TestContext.Current.CancellationToken);

        Assert.Equal(nupkg, bytes);
        Assert.Null(await feeds.DownloadAsync("Newtonsoft.Json", NuGetVersion.Parse("13.0.1"), TestContext.Current.CancellationToken));
    }

    /// <summary>NHibernate 4.1 references 15 checked-in DLLs and no package; the audit said "0 packages" and nothing else.</summary>
    [Fact]
    [ProducesDiagnostic("OFR1008")]
    public async Task Dlls_referenced_by_hint_path_point_to_resolve_dlls()
    {
        using var root = new ScratchDirectory();
        var config = ConfigLoader.Load(new ConfigSources { RepositoryRoot = root.Path }).Config;
        async Task<DiagnosticBag> AuditAsync(string fixture)
        {
            var bag = new DiagnosticBag();
            await DepsAuditor.RunAsync(new DepsAuditRequest
            {
                Model = FixtureModels.Load(fixture), Config = config, Feeds = new RecordedPackageFeeds(new FeedRecording { Source = "none", RecordedAt = "", Packages = [] }),
                Cache = NullCache.Instance, Diagnostics = bag,
            }, TestContext.Current.CancellationToken);
            return bag;
        }

        var loose = await AuditAsync("loose-dlls");
        var packagesConfig = await AuditAsync("legacy-csproj");

        var pointer = Assert.Single(loose.ToSortedList(), d => d.Code == "OFR1008");
        Assert.Equal("4 references in 1 project point at DLLs by HintPath, which deps audit does not see; `offramp deps resolve-dlls` matches them to packages and projects.", pointer.Message);
        Assert.DoesNotContain(packagesConfig.ToSortedList(), d => d.Code == "OFR1008");
    }
}
