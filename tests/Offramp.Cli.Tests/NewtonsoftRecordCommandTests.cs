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
        Assert.Contains("CatalogSerializeProbes.Probe_Contoso_Catalog_MoneyCodec_Write_1_entry_typical", record.Out, StringComparison.Ordinal);
        Assert.Contains("baseline path is ", record.Out, StringComparison.Ordinal);
        Assert.Contains("configured command's filter", record.Out, StringComparison.Ordinal);
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
            Assert.Single(Codes(record.Out), c => c == "OFR3739");
            Assert.False(Directory.Exists(baselines));
        }

        // Its output folder has MSTest's adapter.
        Assert.DoesNotContain("OFR3747", Codes(record.Out));
    }

    [Fact]
    [ProducesDiagnostic("OFR3747")]
    public async Task A_legacy_test_project_without_an_adapter_is_not_run_and_says_which_package_to_add()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy", (root, request) =>
        {
            var project = Path.Combine(root, "tests", "Orders.Tests", "Orders.Tests.csproj");
            File.WriteAllLines(project, File.ReadAllLines(project).Where(l => !l.Contains("MSTest.TestAdapter.", StringComparison.Ordinal)));
            return request;
        });
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");

        Assert.Single(Codes(record.Out), c => c == "OFR3747");
        Assert.Contains("Add the MSTest.TestAdapter package", record.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization", "Baseline")));
    }

    [Fact]
    [ProducesDiagnostic("OFR3747")]
    public async Task A_legacy_nunit_2_project_gets_probes_that_compile_and_is_told_it_needs_the_nunit_2_adapter()
    {
        // MassTransit field test P1 #1: NUnit 2.6.4 through packages.config, NUnit.Runners, no VSTest adapter.
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy", (root, request) =>
        {
            ToNUnit2(Path.Combine(root, "tests", "Orders.Tests"));
            PackagesConfigRestore.RestoreAsync(root).GetAwaiter().GetResult();
            return request;
        });
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var record = await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json");

        var result = JsonNode.Parse(record.Out)!["result"]!;
        Assert.Equal("nunit", result["targets"]!.AsArray().Single()!["framework"]!.GetValue<string>());
        Assert.NotEmpty(result["probes"]!.AsArray());
        // The probes compiled into the NUnit 2 project and it built.
        Assert.DoesNotContain("OFR3735", Codes(record.Out));
        Assert.DoesNotContain("OFR3740", Codes(record.Out));
        Assert.Contains("[global::NUnit.Framework.TestFixture]", File.ReadAllText(Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization", "OrdersSerializeProbes.cs")), StringComparison.Ordinal);
        Assert.Single(Codes(record.Out), c => c == "OFR3747");
        Assert.Contains("Add the NUnitTestAdapter package", record.Out, StringComparison.Ordinal);
    }

    private static List<string> Codes(string envelope) =>
        [.. JsonNode.Parse(envelope)!["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>())];

    /// <summary>Turns the legacy MSTest project into a legacy NUnit 2.6.4 one, as MassTransit's are: no adapter.</summary>
    private static void ToNUnit2(string folder)
    {
        var packages = Path.Combine(folder, "packages.config");
        File.WriteAllLines(packages, File.ReadAllLines(packages)
            .Where(l => !l.Contains("\"MSTest.TestFramework\"", StringComparison.Ordinal))
            .Select(l => l.Contains("\"MSTest.TestAdapter\"", StringComparison.Ordinal) ? "  <package id=\"NUnit\" version=\"2.6.4\" targetFramework=\"net48\" />" : l));
        var project = Path.Combine(folder, "Orders.Tests.csproj");
        var text = File.ReadAllText(project).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n').Where(l => !l.Contains("MSTest.TestAdapter.", StringComparison.Ordinal) && !l.Contains("<ProjectTypeGuids>", StringComparison.Ordinal)).ToList();
        var first = lines.FindIndex(l => l.Contains("<Reference Include=\"Microsoft.VisualStudio.TestPlatform.TestFramework,", StringComparison.Ordinal));
        lines.RemoveRange(first, 6);
        lines.InsertRange(first,
        [
            "    <Reference Include=\"nunit.framework, Version=2.6.4.14350, Culture=neutral, PublicKeyToken=96d09a1eb7f44a77, processorArchitecture=MSIL\">",
            "      <HintPath>..\\..\\packages\\NUnit.2.6.4\\lib\\nunit.framework.dll</HintPath>",
            "      <Private>True</Private>",
            "    </Reference>",
        ]);
        File.WriteAllText(project, string.Join('\n', lines));
        var tests = Path.Combine(folder, "OrderCodecTests.cs");
        File.WriteAllText(tests, File.ReadAllText(tests)
            .Replace("using Microsoft.VisualStudio.TestTools.UnitTesting;", "using NUnit.Framework;", StringComparison.Ordinal)
            .Replace("[TestClass]", "[TestFixture]", StringComparison.Ordinal)
            .Replace("[TestMethod]", "[Test]", StringComparison.Ordinal));
    }
}
