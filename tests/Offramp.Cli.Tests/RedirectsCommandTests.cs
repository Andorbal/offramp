using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>redirects sync</c> through the CLI on the scanned <c>versions</c> fixture.</summary>
public sealed class RedirectsCommandTests
{
    [Fact]
    public async Task Sync_with_prune_is_a_dry_run_that_removes_the_stale_redirect()
    {
        var fixture = await ScannedFixtures.GetAsync("versions");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();
        var before = MoveCommandTests.Tree(fixture.Root);

        var json = await cli.RunAsync("redirects", "sync", "--prune", "--json");
        var human = await cli.RunAsync("redirects", "sync", "--prune");

        Assert.Equal(0, json.ExitCode);
        SchemaAssert.ValidEnvelope(json.Out, "redirects-sync");
        var result = JsonNode.Parse(json.Out)!["result"]!;
        Assert.Equal(1, result["summary"]!["pruned"]!.GetValue<int>());
        Assert.Contains("-      <dependentAssembly>", result["preview"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(before, MoveCommandTests.Tree(fixture.Root));
        await Verify(Scrub.Text(human.Out, fixture.Root), extension: "txt");
    }
}
