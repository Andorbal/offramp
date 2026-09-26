using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>audit dead-code</c> on the <c>dead-code</c> fixture.</summary>
public sealed class AuditDeadCodeCommandTests
{
    [Fact]
    public async Task Json_envelope_validates_and_matches_the_snapshot()
    {
        var fixture = await ScannedFixtures.GetAsync("dead-code");
        fixture.Repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("audit", "dead-code", "--include-tests", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "dead-code");
        Assert.Equal(28, JsonNode.Parse(run.Out)!["result"]!["summary"]!["removableLoc"]!.GetValue<int>());
        await Verify(Scrub.Envelope(run.Out, fixture.Root), extension: "json");
    }

    [Fact]
    public async Task Terminal_and_markdown_views_match_their_snapshots()
    {
        var fixture = await ScannedFixtures.GetAsync("dead-code");
        fixture.Repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var table = await cli.RunAsync("audit", "dead-code");
        var markdown = await cli.RunAsync("audit", "dead-code", "--format", "markdown", "--min-confidence", "medium");

        Assert.Equal(0, table.ExitCode);
        Assert.StartsWith("# Dead code\n", markdown.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("| low |", markdown.Out, StringComparison.Ordinal);
        await Verify(Scrub.Text(table.Out, fixture.Root) + "\n---\n" + markdown.Out, extension: "txt");
    }
}
