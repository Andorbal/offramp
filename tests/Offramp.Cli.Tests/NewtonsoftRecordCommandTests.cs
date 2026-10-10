using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary>
/// <c>newtonsoft tests --record</c> and <c>--verify</c> with real builds and test runs on the
/// <c>newtonsoft-sdk</c> fixture, whose test projects run Newtonsoft.Json 7.0.1 on .NET 10 on every OS.
/// </summary>
public sealed class NewtonsoftRecordCommandTests
{
    private const string Folder = "tests/Catalog.Tests/NewtonsoftCharacterization";

    [Fact]
    [ProducesDiagnostic("OFR3744")]
    [ProducesDiagnostic("OFR3746")]
    [ProducesDiagnostic("OFR3762")]
    public async Task Recorded_baselines_verify_and_a_changed_baseline_is_caught()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var generated = await cli.RunAsync("newtonsoft", "tests", "--apply", "--json");
        Assert.Equal(0, generated.ExitCode);
        var unrecorded = await cli.RunAsync("newtonsoft", "tests", "--verify", "--json");
        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");
        var verify = await cli.RunAsync("newtonsoft", "tests", "--verify", "--json");
        var baseline = Path.Combine(scanned.Root, Folder, "Baseline", "core", "Contoso.Catalog.MoneyCodec.Write#1.entry.typical.txt");
        File.WriteAllText(baseline, File.ReadAllText(baseline).Replace("\"amount\"", "\"Amount\"", StringComparison.Ordinal));
        var changed = await cli.RunAsync("newtonsoft", "tests", "--verify", "--json");
        File.AppendAllText(Path.Combine(scanned.Root, Folder, "CatalogSerializeProbes.cs"), "// edited\n");
        var stale = await cli.RunAsync("newtonsoft", "tests", "--record", "--json");

        Assert.Contains("OFR3744", unrecorded.Out, StringComparison.Ordinal);
        Assert.Equal(0, record.ExitCode);
        SchemaAssert.ValidEnvelope(record.Out, "newtonsoft-tests");
        var outcomes = JsonNode.Parse(record.Out)!["result"]!["outcomes"]!.AsArray();
        Assert.All(outcomes, o => Assert.Equal("recorded", o!["status"]!.GetValue<string>()));
        Assert.All(outcomes, o => Assert.Equal("7.0.0.0", o!["newtonsoft"]!.GetValue<string>()));
        Assert.True(File.Exists(baseline));
        Assert.Equal(0, verify.ExitCode);
        Assert.All(JsonNode.Parse(verify.Out)!["result"]!["outcomes"]!.AsArray(), o => Assert.Equal("same", o!["status"]!.GetValue<string>()));
        Assert.Equal(1, changed.ExitCode);
        var statuses = JsonNode.Parse(changed.Out)!["result"]!["outcomes"]!.AsArray().ToDictionary(o => o!["probe"]!.GetValue<string>(), o => o!["status"]!.GetValue<string>());
        Assert.Equal("differs", statuses["Contoso.Catalog.MoneyCodec.Write#1.entry.typical"]);
        // Verifying reads inputs from the baselines: the reader now gets the changed JSON.
        Assert.Equal("nowThrows", statuses["Contoso.Catalog.MoneyCodec.Read#1.entry.roundtrip-typical"]);
        Assert.Contains("OFR3762", changed.Out, StringComparison.Ordinal);
        Assert.Equal(2, stale.ExitCode);
        Assert.Contains("OFR3746", stale.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3738")]
    public async Task A_probe_that_observes_something_else_each_run_is_quarantined()
    {
        // String hash codes are randomized per process on .NET: the probe sees another number each run.
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, "src", "Catalog", "Labels.cs"), """
                using Newtonsoft.Json;

                namespace Contoso.Catalog
                {
                    public static class Labels
                    {
                        public static string All() => JsonConvert.SerializeObject(new[] { "label".GetHashCode() });
                    }
                }
                """);
            return request;
        });
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");

        var outcome = JsonNode.Parse(record.Out)!["result"]!["outcomes"]!.AsArray().Single(o => o!["probe"]!.GetValue<string>() == "Contoso.Catalog.Labels.All#1.entry.typical")!;
        Assert.Equal("quarantined", outcome["status"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(scanned.Root, Folder, "Baseline", "core", "Contoso.Catalog.Labels.All#1.entry.typical.txt")));
        Assert.Contains("OFR3738", record.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3745")]
    public async Task A_test_project_that_runs_another_Newtonsoft_is_refused()
    {
        // Test SDK 17.x's test host brings Newtonsoft.Json 13 to .NET targets (ADR 0067).
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk", (root, request) =>
        {
            var project = Path.Combine(root, "tests", "Inventory.Tests", "Inventory.Tests.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\"", "\"Microsoft.NET.Test.Sdk\" Version=\"17.11.1\"", StringComparison.Ordinal));
            return request;
        });
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");

        Assert.Contains("OFR3745", record.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(scanned.Root, "tests", "Inventory.Tests", "NewtonsoftCharacterization", "Baseline")));
        Assert.True(Directory.Exists(Path.Combine(scanned.Root, Folder, "Baseline")));
    }

    [Fact]
    [ProducesDiagnostic("OFR3740")]
    public async Task A_test_project_that_does_not_build_stops_the_recording()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();
        File.WriteAllText(Path.Combine(scanned.Root, "tests", "Catalog.Tests", "Broken.cs"), "namespace Contoso.Catalog.Tests { public class Broken { int x = \"no\"; } }\n");

        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");

        Assert.Equal(1, record.ExitCode);
        Assert.Contains("OFR3740", record.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(scanned.Root, Folder, "Baseline")));
    }

    [Fact]
    [ProducesDiagnostic("OFR3741")]
    public async Task A_configured_command_that_runs_fewer_probes_leaves_the_rest_missing()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk");
        scanned.Repository.Directory.Write("offramp.yml", """
            version: 1
            newtonsoft:
              tests:
                command: 'dotnet test {project} --no-build -f {framework} --filter "FullyQualifiedName~CatalogCodec&FullyQualifiedName~Write"'
            """);
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");

        var statuses = JsonNode.Parse(record.Out)!["result"]!["outcomes"]!.AsArray().Select(o => (Probe: o!["probe"]!.GetValue<string>(), Status: o["status"]!.GetValue<string>())).ToList();
        Assert.Contains(statuses, s => s.Probe == "Contoso.Catalog.CatalogCodec.Write#1.entry.typical" && s.Status == "recorded");
        Assert.Contains(statuses, s => s.Probe == "Contoso.Catalog.MoneyCodec.Write#1.entry.typical" && s.Status == "missing");
        Assert.Contains("OFR3741", record.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3739")]
    public async Task Legacy_framework_tests_run_on_windows_only()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy");
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var record = await cli.RunAsync("newtonsoft", "tests", "--samples", Path.Combine(scanned.Root, "samples"), "--record", "--apply", "--json");

        var baselines = Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization", "Baseline", "netfx");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Directory.Exists(baselines), record.Out);
            Assert.Contains(Directory.EnumerateFiles(baselines), f => File.ReadAllText(f).Contains("newtonsoft: 7.0.0.0", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("OFR3739", record.Out, StringComparison.Ordinal);
            Assert.False(Directory.Exists(baselines));
        }
    }
}
