using System.Text.Json.Nodes;
using Offramp.Cli.Infrastructure;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>audit</c> on the <c>behavior</c> fixture: the envelope, SARIF, Markdown, and the terminal view.</summary>
public sealed class AuditCommandTests
{
    /// <summary>The scanned fixture is shared by this class's tests, which run one at a time; each writes its own offramp.yml.</summary>
    private static async Task<(FixtureRepository Repository, CliHarness Cli)> HarnessAsync(string config)
    {
        var fixture = await ScannedFixtures.GetAsync("behavior");
        fixture.Repository.Directory.Write("offramp.yml", config);
        return (fixture.Repository, new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds());
    }

    [Fact]
    public async Task Api_envelope_validates_and_matches_the_snapshot()
    {
        var (repository, cli) = await HarnessAsync("version: 1\n");
        using var _ = cli;

        var run = await cli.RunAsync("audit", "api", "--json");

        Assert.Equal(1, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "audit");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal("api", result["audit"]!.GetValue<string>());
        Assert.Equal(2, result["ledger"]!.AsArray().Count);
        await Verify(Scrub.Envelope(run.Out, repository.Path), extension: "json");

        var never = await cli.RunAsync("audit", "api", "--json", "--fail-on", "never");
        Assert.Equal(0, never.ExitCode);
    }

    [Fact]
    public async Task Behavior_terminal_view_matches_the_snapshot_and_overrides_are_marked()
    {
        var (repository, cli) = await HarnessAsync("version: 1\nrules:\n  OFR3109: { severity: error, reason: we store these }\n  OFR3101: { severity: none }\n");
        using var _ = cli;

        var human = await cli.RunAsync("audit", "behavior");
        var json = await cli.RunAsync("audit", "behavior", "--json");

        Assert.Equal(1, human.ExitCode);
        await Verify(Scrub.Text(human.Out, repository.Path), extension: "txt");
        SchemaAssert.ValidEnvelope(json.Out, "audit");
        var node = JsonNode.Parse(json.Out)!;
        var floating = node["result"]!["findings"]!.AsArray().Single(f => f!["rule"]!.GetValue<string>() == "OFR3109")!;
        Assert.Equal(("error", true), (floating["severity"]!.GetValue<string>(), floating["overridden"]!.GetValue<bool>()));
        Assert.DoesNotContain(node["result"]!["rules"]!.AsArray(), r => r!.GetValue<string>() == "OFR3101");
        var diagnostic = node["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == "OFR3109")!;
        Assert.True(diagnostic["overridden"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sarif_goes_to_the_out_file_with_a_rule_per_finding()
    {
        var (repository, cli) = await HarnessAsync("version: 1\n");
        using var _ = cli;

        var run = await cli.RunAsync("audit", "serialization", "--out", "reports/serialization.sarif");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Wrote reports/serialization.sarif", run.Out, StringComparison.Ordinal);
        var sarif = repository.Directory.Read("reports/serialization.sarif");
        var document = JsonNode.Parse(sarif)!;
        Assert.Equal("2.1.0", document["version"]!.GetValue<string>());
        var sarifRun = document["runs"]![0]!;
        var rules = sarifRun["tool"]!["driver"]!["rules"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToList();
        var results = sarifRun["results"]!.AsArray();
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(r!["ruleId"]!.GetValue<string>(), rules[r["ruleIndex"]!.GetValue<int>()]));
        Assert.All(results, r => Assert.Equal("SRCROOT", r!["locations"]![0]!["physicalLocation"]!["artifactLocation"]!["uriBaseId"]!.GetValue<string>()));
        Assert.Contains(results, r => r!["ruleId"]!.GetValue<string>() == "OFR3203" && r["level"]!.GetValue<string>() == "error");
        await Verify(Scrub.Text(sarif, repository.Path).Replace(OfframpVersion.Current, "{Version}", StringComparison.Ordinal), extension: "json").UseMethodName("Sarif_document");
    }

    [Fact]
    public async Task Markdown_is_printed_alone_and_groups_by_file()
    {
        var (repository, cli) = await HarnessAsync("version: 1\n");
        using var _ = cli;

        var run = await cli.RunAsync("audit", "native", "--format", "markdown", "--group-by", "file", "--all-locations");

        Assert.StartsWith("# Audit native (net10.0)\n", run.Out, StringComparison.Ordinal);
        await Verify(run.Out, extension: "md");
    }

    [Fact]
    public async Task An_unknown_project_is_a_usage_error()
    {
        var (_, cli) = await HarnessAsync("version: 1\n");
        using var _ = cli;

        var run = await cli.RunAsync("audit", "behavior", "--project", "Nope", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR0021", run.Out, StringComparison.Ordinal);
    }
}
