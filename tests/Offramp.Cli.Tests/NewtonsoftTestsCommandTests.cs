using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>newtonsoft tests</c> on the <c>newtonsoft-legacy</c> fixture: a dry run, then an applied and undone run.</summary>
public sealed class NewtonsoftTestsCommandTests
{
    [Fact]
    public async Task Dry_run_json_validates_and_matches_the_snapshot()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-legacy");
        using var cli = new CliHarness(scanned.Repository.Directory);

        var run = await cli.RunAsync("newtonsoft", "tests", "--samples", Path.Combine(scanned.Root, "samples"), "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "newtonsoft-tests");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.False(result["applied"]!.GetValue<bool>());
        Assert.False(Directory.Exists(Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization")));
        await Verify(Scrub.Envelope(run.Out, scanned.Root), extension: "json");
    }

    [Fact]
    public async Task Applying_writes_the_probes_and_rollback_removes_them()
    {
        var scanned = await ScannedFixtures.ScanAsync("newtonsoft-legacy");
        using var cli = new CliHarness(scanned.Repository.Directory).WithRealGitAndBuilds();
        var project = Path.Combine(scanned.Root, "tests", "Orders.Tests", "Orders.Tests.csproj");
        var before = File.ReadAllBytes(project);

        var apply = await cli.RunAsync("newtonsoft", "tests", "--samples", Path.Combine(scanned.Root, "samples"), "--apply", "--json");

        Assert.Equal(0, apply.ExitCode);
        var journal = JsonNode.Parse(apply.Out)!["result"]!["journal"]!.GetValue<string>();
        var folder = Path.Combine(scanned.Root, "tests", "Orders.Tests", "NewtonsoftCharacterization");
        Assert.True(File.Exists(Path.Combine(folder, "OrdersSerializeProbes.cs")));
        Assert.True(File.Exists(Path.Combine(folder, "Inputs", "Contoso.Orders.Order.json")));
        Assert.Contains("NewtonsoftCharacterization\\OfframpProbe.cs", File.ReadAllText(project), StringComparison.Ordinal);

        var rollback = await cli.RunAsync("move", "rollback", "--journal", journal, "--json");

        Assert.Equal(0, rollback.ExitCode);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(before, File.ReadAllBytes(project));
    }
}
