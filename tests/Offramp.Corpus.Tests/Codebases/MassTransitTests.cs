using System.Text.Json.Nodes;
using Offramp.Corpus.Tests.Harness;

namespace Offramp.Corpus.Tests.Codebases;

/// <summary>
/// MassTransit 3.3.5 (<c>docs/field-tests/2026-10-masstransit-3.3.5.md</c>): a message bus library, 36 legacy
/// .NET Framework 4.5.2 projects on <c>packages.config</c>, with NUnit 2 tests and Newtonsoft.Json 7.0.1 used about
/// as many ways as it can be. The assertions pin what that field test found about the <c>newtonsoft</c> commands;
/// the sweep checks the rest.
/// </summary>
[Trait("Category", "Corpus")]
[Trait("Codebase", "masstransit")]
public sealed class MassTransitTests
{
    private const string Core = "src/MassTransit/MassTransit.csproj";
    private const string Tests = "src/MassTransit.Tests/MassTransit.Tests.csproj";

    // At most the codebase's timeoutMinutes less 15 (ManifestTests checks).
    [Fact(Timeout = 30 * 60 * 1000)]
    public async Task The_field_test_findings_stay_fixed()
    {
        await using var corpus = await CorpusRun.OpenAsync("masstransit");

        var sweep = await corpus.SweepAsync();

        // Out of the box: doctor --fix is all it takes for every project to build outside Windows.
        Assert.Equal(36, sweep.Scan.Result["projects"]!.GetValue<int>());
        Assert.Empty(sweep.Scan.Result["notLoaded"]!.AsArray());
        Assert.Empty(sweep.Scan.Result["partial"]!.AsArray());

        var scan = await corpus.RunAsync("newtonsoft-scan", "newtonsoft", "scan");
        var projects = scan.Result["projects"]!.AsArray();
        Assert.True(projects.Count >= 9, $"{projects.Count} projects use Newtonsoft.Json.");
        var sites = projects.SelectMany(p => p!["sites"]!.AsArray()).ToDictionary(s => s!["id"]!.GetValue<string>(), s => s!);
        Assert.True(sites.Count >= 50, $"{sites.Count} sites.");
        // What worked: BSON is obsolete in 13.x at exactly the places the build warns about; a typeof(JToken) is not a site.
        Assert.Contains(scan.Result["findings"]!.AsArray(), f => Rule(f) == "OFR3702" && FileOf(f) == "src/MassTransit/Serialization/BsonMessageSerializer.cs");
        Assert.Empty(projects.Single(p => p!["project"]!.GetValue<string>() == "src/MassTransit.RabbitMqTransport/MassTransit.RabbitMqTransport.csproj")!["sites"]!.AsArray());
        // P1 #2: the serializers cached in Lazy<JsonSerializer>, and the one copied into a readonly field, have known settings.
        Assert.Equal(("member", "F:MassTransit.Serialization.JsonMessageSerializer.SerializerSettings"), Settings(sites["MassTransit.Serialization.JsonMessageSerializer.Serialize#1"]));
        Assert.Equal(("member", "F:MassTransit.Serialization.BsonMessageSerializer.SerializerSettings"), Settings(sites["MassTransit.Serialization.BsonMessageSerializer.Serialize#1"]));
        Assert.Equal(("member", "P:MassTransit.Serialization.BsonMessageSerializer.Serializer"),
            Settings(sites["MassTransit.Serialization.EncryptedMessageSerializer.MassTransit.IMessageSerializer.Serialize#1"]));

        var planned = await corpus.RunAsync("newtonsoft-tests", "newtonsoft", "tests");
        // P1 #1: the NUnit 2 test projects are found and get NUnit probes.
        Assert.All(planned.Result["targets"]!.AsArray(), t => Assert.Equal("nunit", t!["framework"]!.GetValue<string>()));
        Assert.Contains(planned.Result["targets"]!.AsArray(), t => t!["testProject"]!.GetValue<string>() == Tests);
        Assert.NotEmpty(planned.Result["probes"]!.AsArray());
        // P1 #5: no probe is dropped for a constructor call that does not compile (XElement's copy constructor).
        Assert.DoesNotContain("OFR3735", planned.Codes);
        // P2 #6: a site without a payload type says so.
        Assert.DoesNotContain(planned.Result["uncovered"]!.AsArray(), u => u!["detail"]!.GetValue<string>().Contains("for  and", StringComparison.Ordinal));

        var generated = await corpus.RunAsync("newtonsoft-tests", "newtonsoft", "tests", "--apply");
        // P1 #4: only the test project that gets probes gets files; the three without probes are not touched.
        var files = generated.Result["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.StartsWith("src/MassTransit.Tests/", f, StringComparison.Ordinal));
        foreach (var untouched in new[] { "MassTransit.Futures.Tests", "MassTransit.QuartzIntegration.Tests", "MassTransit.AzureServiceBusTransport.Tests" })
        {
            Assert.False(Directory.Exists(Path.Combine(corpus.Repository.Path, "src", untouched, "NewtonsoftCharacterization")), untouched);
        }

        var record = await corpus.RunAsync("newtonsoft-tests", "newtonsoft", "tests", "--record", "--apply");
        // P1 #1: no VSTest adapter (only NUnit.Runners), so it is not run, on any OS, and the package to add is named.
        var adapter = Assert.Single(record.Diagnostics("OFR3747"));
        Assert.Equal(Tests, adapter["project"]!.GetValue<string>());
        Assert.Equal("NUnitTestAdapter", adapter["data"]!["package"]!.GetValue<string>());
        // P2 #7: what cannot run here is said once, not once per recording pass.
        Assert.Equal(OperatingSystem.IsWindows() ? 0 : 1, record.Diagnostics("OFR3739").Count());

        var upgrade = await corpus.RunAsync("newtonsoft-upgrade", "newtonsoft", "upgrade");
        // P1 #3: only Newtonsoft.Json redirects change; the stale log4net ones are redirects sync's.
        var changed = upgrade.Result["preview"]!.GetValue<string>().Split('\n').Where(l => (l.StartsWith('+') || l.StartsWith('-')) && !l.StartsWith("+++", StringComparison.Ordinal) && !l.StartsWith("---", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(changed, l => l.Contains("log4net", StringComparison.Ordinal));
        Assert.All(upgrade.Result["redirects"]!["apps"]!.AsArray().SelectMany(a => a!["redirects"]!.AsArray()), r => Assert.Equal("Newtonsoft.Json", r!["assembly"]!.GetValue<string>()));
        // What worked: every packages.config entry moves, and the result builds against 13.x.
        Assert.True(upgrade.Result["projects"]!.AsArray().Count >= 9);
        var applied = await corpus.RunAsync("newtonsoft-upgrade", "newtonsoft", "upgrade", "--apply", "--yes");
        Assert.Equal("passed", applied.Result["verify"]!["status"]!.GetValue<string>());
        Assert.Contains("Newtonsoft.Json.13.", corpus.Repository.Read(Core), StringComparison.Ordinal);
    }

    private static (string, string?) Settings(JsonNode site) =>
        (site["settings"]!["kind"]!.GetValue<string>(), site["settings"]!["symbol"]?.GetValue<string>());

    private static string Rule(JsonNode? finding) => finding!["rule"]!.GetValue<string>();

    private static string FileOf(JsonNode? finding) => finding!["file"]?.GetValue<string>() ?? "";
}
