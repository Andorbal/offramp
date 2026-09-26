using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

public sealed class GraphCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public GraphCommandTests()
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("dual-target"), _cli.Repo.Path);
        _cli.Repo.Write("offramp.yml", "version: 1\n");
        SaveFresh(FixtureModels.Load("dual-target"));
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Json_envelope_carries_the_graph_and_validates()
    {
        var run = await _cli.RunAsync("graph", "--json", "--highlight", "frontier");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "graph");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Null(result["format"]);
        Assert.Equal(3, result["graph"]!["nodes"]!.AsArray().Count);
        Assert.Equal("frontier", result["graph"]!["highlight"]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Human_summary_matches_the_snapshot()
    {
        var run = await _cli.RunAsync("graph");

        Assert.Equal(0, run.ExitCode);
        await Verify(Scrub.Text(run.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task A_format_without_out_writes_only_the_document_to_stdout()
    {
        _cli.Repo.Write("offramp.yml", "version: 1\nunknownKey: 1\n");

        var run = await _cli.RunAsync("graph", "--format", "dot");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("// offramp graph: 3 projects, 3 references\n", run.Out, StringComparison.Ordinal);
        Assert.EndsWith("}\n", run.Out, StringComparison.Ordinal);
        Assert.Contains("OFR0050", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Out_infers_the_format_from_the_extension()
    {
        var run = await _cli.RunAsync("graph", "--out", "docs/graph.html", "--exclude-kind", "console");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Wrote docs/graph.html: 2 projects, 1 references, 0 cycles.", run.Out, StringComparison.Ordinal);
        Assert.Contains("id=\"offramp-graph\"", _cli.Repo.Read("docs/graph.html"), StringComparison.Ordinal);
        Assert.Contains("<title>DualTarget project graph</title>", _cli.Repo.Read("docs/graph.html"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--out", "graph.txt")]
    [InlineData("--exclude-kind", "gadget")]
    [InlineData("--depth", "2")]
    [InlineData("--format", "svg")]
    public async Task Invalid_options_are_usage_errors(string option, string value)
    {
        var run = await _cli.RunAsync("graph", option, value);

        Assert.Equal(2, run.ExitCode);
    }

    [Fact]
    public async Task Focus_resolves_names_and_rejects_unknown_projects()
    {
        var focused = await _cli.RunAsync("graph", "--focus", "Shared", "--depth", "1", "--direction", "dependencies", "--json");
        var unknown = await _cli.RunAsync("graph", "--focus", "Nope", "--json");

        Assert.Equal(["src/Contracts/Contracts.csproj", "src/Shared/Shared.csproj"],
            JsonNode.Parse(focused.Out)!["result"]!["graph"]!["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains(JsonNode.Parse(unknown.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0021");
    }

    [Fact]
    [ProducesDiagnostic("OFR0201")]
    public async Task Large_mermaid_graphs_are_flagged()
    {
        var projects = Enumerable.Range(0, 301)
            .Select(i => new ProjectInfo { Id = $"src/P{i:000}/P{i:000}.csproj", Name = $"P{i:000}", Kind = ProjectKind.Library, TargetFrameworks = ["net48"] })
            .ToList();
        SaveFresh(FixtureModels.Load("dual-target") with { Projects = projects, Graph = GraphBuilder.Build(projects) });

        var run = await _cli.RunAsync("graph", "--format", "mermaid", "--json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0201");
    }

    private void SaveFresh(WorkspaceModel model) =>
        WorkspaceStore.Save(_cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = _cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(_cli.Repo.Path, _cli.Repo.Combine(".offramp"), model.Solution),
        });
}
