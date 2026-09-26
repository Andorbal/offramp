using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>seams</c> on the <c>seams</c> fixture.</summary>
public sealed class SeamsCommandTests
{
    [Fact]
    public async Task Json_envelope_validates_and_matches_the_snapshot()
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        fixture.Repository.Directory.Write("offramp.yml", "version: 1\nseams:\n  unportableSources: [ list ]\n  unportableSymbols: [ System.DirectoryServices ]\n");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("seams", "--project", "Accounts", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "seams");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal("IDirectoryLookup", result["seams"]![0]!["proposedInterface"]!.GetValue<string>());
        Assert.True(result["seams"]![0]!["articulationPoint"]!.GetValue<bool>());
        await Verify(Scrub.Envelope(run.Out, fixture.Root), extension: "json");
    }

    [Fact]
    public async Task Audit_findings_taint_the_same_types_as_the_symbol_list()
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        fixture.Repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("seams", "--project", "Accounts", "--unportable-from", "audit", "--json");

        Assert.Equal(0, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal("audit", result["unportableFrom"]!.GetValue<string>());
        Assert.Equal(
            ["Accounts.Directory.CachedDirectoryLookup", "Accounts.Directory.DirectoryCache", "Accounts.Directory.DirectoryLookup"],
            result["tainted"]!.AsArray().Select(t => t!["type"]!.GetValue<string>()));
        Assert.Equal("Accounts.Directory.DirectoryLookup", result["seams"]![0]!["boundaryType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dot_and_html_views_render_the_cut()
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        fixture.Repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var dot = await cli.RunAsync("seams", "--project", "Accounts", "--unportable-from", "list", "--symbols", "System.DirectoryServices", "--format", "dot");
        var html = await cli.RunAsync("seams", "--project", "Accounts", "--unportable-from", "list", "--symbols", "System.DirectoryServices", "--out", "seams.html");
        var table = await cli.RunAsync("seams", "--project", "Accounts", "--unportable-from", "list", "--symbols", "System.DirectoryServices");

        Assert.Contains("\"Accounts.Users.UserService\" -> \"Accounts.Directory.DirectoryLookup\" [label=\"5\", color=\"#c62828\", penwidth=3];", dot.Out, StringComparison.Ordinal);
        Assert.Contains("Wrote seams.html: 1 seam.", html.Out, StringComparison.Ordinal);
        var page = fixture.Repository.Directory.Read("seams.html");
        Assert.Contains("<svg", page, StringComparison.Ordinal);
        Assert.Contains("seam-1: IDirectoryLookup on Accounts.Directory.DirectoryLookup", page, StringComparison.Ordinal);
        await Verify(Scrub.Text(table.Out, fixture.Root), extension: "txt");
    }
}
