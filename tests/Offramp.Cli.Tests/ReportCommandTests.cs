using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

public sealed class ReportCommandTests : IDisposable
{
    private const string Now = "2026-09-25T20:11:04Z";

    private readonly CliHarness _cli = new();

    public ReportCommandTests()
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("dual-target"), _cli.Repo.Path);
        _cli.Repo.Write("offramp.yml", "version: 1\nreport:\n  title: Dual target\n");
        var model = FixtureModels.Load("dual-target") with { CreatedAt = Now };
        SaveFresh(model);

        // An earlier scan, when Shared was still framework-only.
        var earlier = model with
        {
            CreatedAt = "2026-08-01T10:00:00Z",
            Projects = [.. model.Projects.Select(p => p.Name == "Shared" ? p with { FrameworkClass = FrameworkClass.Framework } : p)],
        };
        Ledger.Write(Ledger.Snapshot(earlier), _cli.Repo.Combine(".offramp", "ledger"), _cli.Repo.Path);
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Json_envelope_carries_the_report_and_validates()
    {
        var run = await _cli.RunAsync("report", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "report");
        var report = JsonNode.Parse(run.Out)!["result"]!["report"]!;
        Assert.Equal("Dual target", report["title"]!.GetValue<string>());
        Assert.Equal(["2026-08-01T10:00:00Z", Now], report["series"]!.AsArray().Select(p => p!["createdAt"]!.GetValue<string>()));
        Assert.Equal(-25, report["headline"]!["frameworkLocChange"]!.GetValue<int>());
    }

    [Fact]
    public async Task Human_summary_matches_the_snapshot()
    {
        var run = await _cli.RunAsync("report");

        Assert.Equal(0, run.ExitCode);
        await Verify(Scrub.Text(run.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task A_format_without_out_writes_only_the_document_to_stdout()
    {
        var markdown = await _cli.RunAsync("report", "--format", "markdown", "--since", "2026-09-01");
        var json = await _cli.RunAsync("report", "--format", "json");

        Assert.Equal(0, markdown.ExitCode);
        Assert.StartsWith("# Dual target\n\nAs of 2026-09-25 20:11 UTC, since 2026-09-01.\n", markdown.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-01", markdown.Out, StringComparison.Ordinal);
        SchemaAssert.Valid("report-data", json.Out);
    }

    [Fact]
    public async Task Out_infers_the_format_and_can_embed_the_graph()
    {
        var run = await _cli.RunAsync("report", "--out", "docs/progress.html", "--with-graph", "--title", "Q3 <progress>");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Wrote docs/progress.html: 100% of 44 lines portable; 0 framework-only lines in 0 projects, down 25 since 2026-08-01.", run.Out, StringComparison.Ordinal);
        var html = _cli.Repo.Read("docs/progress.html");
        Assert.Contains("<title>Q3 &lt;progress&gt;</title>", html, StringComparison.Ordinal);
        Assert.Contains("<iframe class=\"graph\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Title_defaults_to_the_repository_folder()
    {
        _cli.Repo.Write("offramp.yml", "version: 1\n");

        var run = await _cli.RunAsync("report", "--format", "markdown");

        Assert.StartsWith("# " + Path.GetFileName(_cli.Repo.Path) + "\n", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--out", "report.txt")]
    [InlineData("--since", "last week")]
    [InlineData("--since", "2026-13-01")]
    [InlineData("--format", "pdf")]
    [InlineData("--with-graph", "--format=markdown")]
    [InlineData("--with-graph", "--out=report.md")]
    public async Task Invalid_options_are_usage_errors(string option, string value)
    {
        var run = await _cli.RunAsync("report", option, value);

        Assert.Equal(2, run.ExitCode);
    }

    [Fact]
    public async Task A_since_time_is_normalized_to_utc()
    {
        var run = await _cli.RunAsync("report", "--json", "--since", "2026-08-01T12:00:00+02:00");

        var report = JsonNode.Parse(run.Out)!["result"]!["report"]!;
        Assert.Equal("2026-08-01T10:00:00Z", report["since"]!.GetValue<string>());
        Assert.Equal(2, report["series"]!.AsArray().Count);
    }

    [Fact]
    [ProducesDiagnostic("OFR0202")]
    public async Task Files_in_the_ledger_that_are_not_snapshots_are_reported()
    {
        _cli.Repo.Write(".offramp/ledger/2026-09-01-conflict.json", "<<<<<<< HEAD\n{}\n");
        _cli.Repo.Write(".offramp/ledger/notes.json", "{ \"note\": \"not a snapshot\" }");

        var run = await _cli.RunAsync("report", "--json");

        Assert.Equal(0, run.ExitCode);
        var diagnostics = JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Where(d => d!["code"]!.GetValue<string>() == "OFR0202").ToList();
        Assert.Equal([".offramp/ledger/2026-09-01-conflict.json", ".offramp/ledger/notes.json"], diagnostics.Select(d => d!["file"]!.GetValue<string>()));
        Assert.Equal(2, JsonNode.Parse(run.Out)!["result"]!["report"]!["series"]!.AsArray().Count);
    }

    private void SaveFresh(WorkspaceModel model) =>
        WorkspaceStore.Save(_cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = _cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(_cli.Repo.Path, _cli.Repo.Combine(".offramp"), model.Solution),
        });
}
