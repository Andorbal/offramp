using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

/// <summary>deps audit and deps gac through the CLI, on the scanned versions model and its recorded feed.</summary>
public sealed class DepsCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public DepsCommandTests()
    {
        VersionsFeed.WriteFolderFeed(_cli.Repo.Combine("recorded-feed"));
        _cli.Repo.Write("offramp.yml", "version: 1\ndeps:\n  feeds: [ recorded-feed ]\n  pins:\n    - package: Newtonsoft.Json\n      project: src/Customer.Api/Customer.Api.csproj\n      version: 9.0.1\n      reason: \"Customer integrations\"\n");
        var model = FixtureModels.Load("versions");
        WorkspaceStore.Save(_cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = _cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(_cli.Repo.Path, _cli.Repo.Combine(".offramp"), model.Solution),
        });
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Audit_json_validates_and_blocked_packages_fail_the_run()
    {
        var run = await _cli.RunAsync("deps", "audit", "--json");

        Assert.Equal(1, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "deps-audit");
        var codes = JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ToList();
        Assert.Contains("OFR1001", codes);
        Assert.True(_cli.Repo.Exists(".offramp/cache/packages/newtonsoft.json/13.0.3.json"));
    }

    [Fact]
    public async Task Audit_human_output_matches_the_snapshot()
    {
        var run = await _cli.RunAsync("deps", "audit");

        await Verify(Scrub.Text(run.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task Markdown_and_json_formats_print_the_document_alone()
    {
        var markdown = await _cli.RunAsync("deps", "audit", "--format", "markdown");
        var json = await _cli.RunAsync("deps", "audit", "--format", "json", "--package", "Newtonsoft.Json");

        Assert.StartsWith("# Package audit for net10.0\n", markdown.Out, StringComparison.Ordinal);
        Assert.Contains("| Contoso.Legacy.Reports | 1.1.0 | 1.1.0: no | none | none | blocked |", markdown.Out, StringComparison.Ordinal);
        Assert.Contains("OFR1001", markdown.Error, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        var result = JsonNode.Parse(json.Out)!;
        Assert.Equal("Newtonsoft.Json", result["packages"]![0]!["id"]!.GetValue<string>());
        Assert.True(result["packages"]![0]!["inUse"]![0]!["pinned"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Target_changes_the_answer()
    {
        var run = await _cli.RunAsync("deps", "audit", "--package", "EntityFramework", "--target", "8", "--json");

        var package = JsonNode.Parse(run.Out)!["result"]!["packages"]![0]!;
        Assert.Equal("net8.0", package["target"]!.GetValue<string>());
        Assert.Equal("upgrade", package["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_project_is_a_usage_error()
    {
        var run = await _cli.RunAsync("deps", "audit", "--project", "Nope");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR0021", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gac_lists_framework_references_with_their_equivalents()
    {
        var run = await _cli.RunAsync("deps", "gac", "--json");
        var human = await _cli.RunAsync("deps", "gac");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "deps-gac");
        await Verify(Scrub.Text(human.Out, _cli.Repo.Path), extension: "txt");
    }
}
