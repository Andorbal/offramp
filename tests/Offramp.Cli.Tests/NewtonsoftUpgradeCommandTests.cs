using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>newtonsoft upgrade</c> on both fixtures, with real restores, builds, and test runs.</summary>
public sealed class NewtonsoftUpgradeCommandTests
{
    private static readonly string[] Upgraded =
        ["src/Catalog/Catalog.csproj", "src/Inventory/Inventory.csproj", "tests/Catalog.Tests/Catalog.Tests.csproj"];

    [Theory]
    [InlineData("newtonsoft-sdk")]
    [InlineData("newtonsoft-legacy")]
    public async Task Dry_run_json_validates_and_matches_the_snapshot(string fixture)
    {
        var scanned = await ScannedFixtures.GetAsync(fixture);
        using var cli = new CliHarness(scanned.Repository.Directory);

        var run = await cli.RunAsync("newtonsoft", "upgrade", "--version", "13.0.4", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "newtonsoft-upgrade");
        Assert.False(JsonNode.Parse(run.Out)!["result"]!["applied"]!.GetValue<bool>());
        await Verify(Scrub.Envelope(run.Out, scanned.Root), extension: "json").UseParameters(fixture);
    }

    [Fact]
    [ProducesDiagnostic("OFR3762")]
    public async Task Probes_recorded_on_7_report_what_13_changes_and_rollback_undoes_the_upgrade()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();
        Assert.Equal(0, (await cli.RunAsync("newtonsoft", "tests", "--record", "--apply", "--json")).ExitCode);
        var before = Upgraded.ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(scanned.Root, p)));

        var rolledBack = await cli.RunAsync("newtonsoft", "upgrade", "--version", "13.0.4", "--on-diff", "rollback", "--apply", "--json");
        var unchanged = Upgraded.All(p => before[p].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(scanned.Root, p))));
        var kept = await cli.RunAsync("newtonsoft", "upgrade", "--version", "13.0.4", "--apply", "--json");

        Assert.Equal(1, rolledBack.ExitCode);
        Assert.True(JsonNode.Parse(rolledBack.Out)!["result"]!["rolledBack"]!.GetValue<bool>());
        Assert.True(unchanged);
        Assert.Equal(1, kept.ExitCode);
        SchemaAssert.ValidEnvelope(kept.Out, "newtonsoft-upgrade");
        var result = JsonNode.Parse(kept.Out)!["result"]!;
        Assert.False(result["rolledBack"]!.GetValue<bool>());
        Assert.Equal("passed", result["verify"]!["status"]!.GetValue<string>());
        var statuses = result["probes"]!.AsArray().ToDictionary(p => p!["probe"]!.GetValue<string>(), p => p!["status"]!.GetValue<string>());
        // What scan predicted: the camel-cased "LEGACY CODE" and the [EnumMember] keys (OFR3703, OFR3705), Money's ISerializable shape (OFR3704).
        Assert.Equal("differs", statuses["Contoso.Catalog.CatalogCodec.Write#1.entry.typical"]);
        Assert.Equal("differs", statuses["Contoso.Catalog.MoneyCodec.Write#1.entry.typical"]);
        // Inventory, where scan found no change, observes none.
        Assert.All(statuses.Where(s => s.Key.StartsWith("Contoso.Inventory.", StringComparison.Ordinal)), s => Assert.Equal("same", s.Value));
        Assert.Contains("Version=\"13.0.4\"", File.ReadAllText(Path.Combine(scanned.Root, "src", "Catalog", "Catalog.csproj")), StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3761")]
    public async Task A_removed_api_fails_the_build_and_the_upgrade_is_rolled_back()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, "src", "Catalog", "Async.cs"), """
                namespace Contoso.Catalog
                {
                    public static class Async
                    {
                        public static System.Threading.Tasks.Task<string> Write(Product product) => Newtonsoft.Json.JsonConvert.SerializeObjectAsync(product);
                    }
                }
                """);
            return request;
        });
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();
        var before = Upgraded.ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(scanned.Root, p)));

        var run = await cli.RunAsync("newtonsoft", "upgrade", "--version", "13.0.4", "--apply", "--json");

        Assert.Equal(1, run.ExitCode);
        Assert.True(JsonNode.Parse(run.Out)!["result"]!["rolledBack"]!.GetValue<bool>());
        Assert.Contains("OFR3761", run.Out, StringComparison.Ordinal);
        Assert.Contains("OFR3701 at src/Catalog/Async.cs", run.Out, StringComparison.Ordinal);
        Assert.All(Upgraded, p => Assert.Equal(before[p], File.ReadAllBytes(Path.Combine(scanned.Root, p))));
    }

    [Fact]
    [ProducesDiagnostic("OFR3766")]
    public async Task A_failed_restore_rolls_the_upgrade_back()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();
        cli.Machine.Setup.Add(r => r.On("dotnet", ["restore"], 1, "error NU1101: Unable to find package Newtonsoft.Json."));
        var before = Upgraded.ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(scanned.Root, p)));

        var run = await cli.RunAsync("newtonsoft", "upgrade", "--version", "13.0.4", "--apply", "--json");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("OFR3766", run.Out, StringComparison.Ordinal);
        Assert.All(Upgraded, p => Assert.Equal(before[p], File.ReadAllBytes(Path.Combine(scanned.Root, p))));
    }

    [Fact]
    [ProducesDiagnostic("OFR3764")]
    [ProducesDiagnostic("OFR3767")]
    public async Task A_legacy_solution_moves_packages_config_hint_paths_and_redirects_and_builds()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy", (root, request) =>
        {
            // The application refuses anything but 7.x; it stays, and everything else moves.
            var config = Path.Combine(root, "src", "Orders.App", "packages.config");
            File.WriteAllText(config, File.ReadAllText(config).Replace("targetFramework=\"net48\"", "targetFramework=\"net48\" allowedVersions=\"[7,8)\"", StringComparison.Ordinal));
            return request;
        });
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealTests();

        var run = await cli.RunAsync("newtonsoft", "upgrade", "--version", "13.0.4", "--apply", "--json");

        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal("passed", result["verify"]!["status"]!.GetValue<string>());
        Assert.Contains("OFR3764", run.Out, StringComparison.Ordinal);
        Assert.Contains("OFR3767", run.Out, StringComparison.Ordinal);
        var orders = File.ReadAllText(Path.Combine(scanned.Root, "src", "Orders", "Orders.csproj"));
        Assert.Contains("Newtonsoft.Json, Version=13.0.0.0", orders, StringComparison.Ordinal);
        Assert.Contains(@"packages\Newtonsoft.Json.13.0.4\lib\net45\Newtonsoft.Json.dll", orders, StringComparison.Ordinal);
        Assert.Contains("version=\"13.0.4\"", File.ReadAllText(Path.Combine(scanned.Root, "src", "Orders", "packages.config")), StringComparison.Ordinal);
        Assert.Contains("version=\"7.0.1\"", File.ReadAllText(Path.Combine(scanned.Root, "src", "Orders.App", "packages.config")), StringComparison.Ordinal);
    }
}
