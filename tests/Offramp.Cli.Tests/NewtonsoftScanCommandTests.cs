using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>newtonsoft scan</c> on the <c>newtonsoft-sdk</c> and <c>newtonsoft-legacy</c> fixtures.</summary>
public sealed class NewtonsoftScanCommandTests
{
    [Theory]
    [InlineData("newtonsoft-sdk")]
    [InlineData("newtonsoft-legacy")]
    public async Task Json_envelope_validates_and_matches_the_snapshot(string fixture)
    {
        var scanned = await ScannedFixtures.GetAsync(fixture);
        using var cli = new CliHarness(scanned.Repository.Directory);

        var run = await cli.RunAsync("newtonsoft", "scan", "--version", "13.0.4", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "newtonsoft-scan");
        Assert.Equal("13.0.4", JsonNode.Parse(run.Out)!["result"]!["target"]!.GetValue<string>());
        await Verify(Scrub.Envelope(run.Out, scanned.Root), extension: "json").UseParameters(fixture);
    }

    [Fact]
    public async Task Terminal_and_markdown_views_match_their_snapshots()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-legacy");
        using var cli = new CliHarness(scanned.Repository.Directory);

        var table = await cli.RunAsync("newtonsoft", "scan", "--version", "13.0.4");
        var markdown = await cli.RunAsync("newtonsoft", "scan", "--version", "13.0.4", "--format", "markdown");

        Assert.Equal(0, table.ExitCode);
        Assert.StartsWith("# Newtonsoft.Json: 3 projects, 3 sites, target 13.0.4\n", markdown.Out, StringComparison.Ordinal);
        await Verify(Scrub.Text(table.Out, scanned.Root) + "\n---\n" + markdown.Out, extension: "txt");
    }

    [Fact]
    public async Task One_project_is_scanned_alone()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory);

        var run = await cli.RunAsync("newtonsoft", "scan", "--version", "13.0.4", "--project", "Inventory", "--json");

        var projects = JsonNode.Parse(run.Out)!["result"]!["projects"]!.AsArray();
        Assert.Equal("src/Inventory/Inventory.csproj", Assert.Single(projects)!["project"]!.GetValue<string>());
    }

    [Fact]
    [ProducesDiagnostic("OFR3760")]
    public async Task A_version_that_is_not_a_version_is_a_usage_error()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory);

        var run = await cli.RunAsync("newtonsoft", "scan", "--version", "thirteen", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR3760", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_project_is_a_usage_error()
    {
        var scanned = await ScannedFixtures.GetAsync("newtonsoft-sdk");
        using var cli = new CliHarness(scanned.Repository.Directory);

        var run = await cli.RunAsync("newtonsoft", "scan", "--version", "13.0.4", "--project", "Nope", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR0021", run.Out, StringComparison.Ordinal);
    }
}
