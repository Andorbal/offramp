using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary>
/// scan, slice, and doctor --fix through the CLI, on the windows-only-build-steps
/// fixture scanned from its committed binary log (no build needed).
/// </summary>
public sealed class WorkspaceCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public WorkspaceCommandTests()
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("windows-only-build-steps"), _cli.Repo.Path);
    }

    public void Dispose() => _cli.Dispose();

    private Task<CliRun> ScanAsync(params string[] extra) => _cli.RunAsync(["scan", "--binlog", "msbuild.binlog", .. extra]);

    [Fact]
    public async Task Scan_json_validates_and_matches_the_snapshot()
    {
        var run = await ScanAsync("--json");

        // The captured build failed (OFR0130, an error), so scan exits 1 with the model written.
        Assert.Equal(1, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "scan");
        SchemaAssert.Valid("workspace", _cli.Repo.Read(".offramp/workspace.json"));
        await Verify(Scrub.Envelope(run.Out, _cli.Repo.Path), extension: "json");
    }

    [Fact]
    public async Task Scan_human_output_matches_the_snapshot()
    {
        var run = await ScanAsync();

        Assert.Equal(1, run.ExitCode);
        await Verify(Scrub.Text(run.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task No_build_cannot_be_combined_with_logs()
    {
        var run = await ScanAsync("--no-build");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--no-build cannot be combined", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_of_a_missing_log_exits_3()
    {
        var run = await _cli.RunAsync("scan", "--binlog", "missing.binlog", "--json");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("missing.binlog", Diagnostic(run, "OFR0004")["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task If_stale_skips_a_fresh_model()
    {
        await ScanAsync();

        var run = await ScanAsync("--if-stale", "--json");

        Assert.Equal(0, run.ExitCode);
        Assert.True(JsonNode.Parse(run.Out)!["result"]!["upToDate"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Slice_writes_a_solution_filter()
    {
        await ScanAsync();

        var run = await _cli.RunAsync("slice", "--for", "Soap", "--out", "slices/soap.slnf");

        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Wrote slices/soap.slnf: 1 projects", run.Out, StringComparison.Ordinal);
        var filter = JsonNode.Parse(_cli.Repo.Read("slices/soap.slnf"))!["solution"]!;
        Assert.Equal(@"..\WindowsOnly.sln", filter["path"]!.GetValue<string>());
        Assert.Equal([@"src\Soap\Soap.csproj"], filter["projects"]!.AsArray().Select(p => p!.GetValue<string>()));

        // The filter the slice wrote does not make the model stale.
        var again = await _cli.RunAsync("slice", "--for", "src/Office/Office.csproj", "--json");
        Assert.Equal(0, again.ExitCode);
        SchemaAssert.ValidEnvelope(again.Out, "slice");
        Assert.DoesNotContain(JsonNode.Parse(again.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0002");
    }

    [Fact]
    [ProducesDiagnostic("OFR0021")]
    public async Task Slice_of_an_unknown_project_is_a_usage_error()
    {
        await ScanAsync();

        var run = await _cli.RunAsync("slice", "--for", "Soap,Nope", "--json");

        Assert.Equal(2, run.ExitCode);
        var diagnostic = JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == "OFR0021")!;
        Assert.Equal("Nope", diagnostic["data"]!["project"]!.GetValue<string>());
    }

    [Fact]
    public async Task Slice_without_a_model_exits_3()
    {
        var run = await _cli.RunAsync("slice", "--for", "Soap", "--json");

        Assert.Equal(3, run.ExitCode);
        Assert.Equal(".offramp/workspace.json", Diagnostic(run, "OFR0001")["data"]!["path"]!.GetValue<string>());
    }

    [Fact]
    [ProducesDiagnostic("OFR0022")]
    public async Task Slice_of_a_model_without_a_solution_is_reported()
    {
        await ScanAsync();
        var path = _cli.Repo.Combine(".offramp", "workspace.json");
        var model = JsonNode.Parse(File.ReadAllText(path))!;
        model["solution"] = null;
        File.WriteAllText(path, model.ToJsonString());

        var run = await _cli.RunAsync("slice", "--for", "Soap", "--json");

        Assert.Equal(3, run.ExitCode);
        Assert.NotNull(Diagnostic(run, "OFR0022"));
    }

    [Fact]
    public async Task A_stale_model_warns_and_fails_with_fail_on_stale()
    {
        await ScanAsync();
        _cli.Repo.Write("src/Soap/Soap.csproj", _cli.Repo.Read("src/Soap/Soap.csproj") + "\n");

        var warned = await _cli.RunAsync("slice", "--for", "Soap", "--json");
        var failed = await _cli.RunAsync("slice", "--for", "Soap", "--json", "--fail-on-stale");

        Assert.Equal(0, warned.ExitCode);
        Assert.Equal("warning", Diagnostic(warned, "OFR0002")["severity"]!.GetValue<string>());
        Assert.Equal(1, failed.ExitCode);
        Assert.Equal("error", Diagnostic(failed, "OFR0002")["severity"]!.GetValue<string>());
    }

    [Fact]
    public async Task Doctor_fix_previews_and_applies_only_with_apply()
    {
        await ScanAsync();
        var before = _cli.Repo.Read("Directory.Build.props");

        var preview = await _cli.RunAsync("doctor", "--fix", "--json");

        Assert.Equal(before, _cli.Repo.Read("Directory.Build.props"));
        var fix = JsonNode.Parse(preview.Out)!["result"]!["fix"]!;
        Assert.False(fix["applied"]!.GetValue<bool>());
        Assert.Contains("+  <PropertyGroup Condition=", fix["diff"]!.GetValue<string>(), StringComparison.Ordinal);
        SchemaAssert.ValidEnvelope(preview.Out, "doctor");

        var applied = await _cli.RunAsync("doctor", "--fix", "--apply", "--json");

        Assert.True(JsonNode.Parse(applied.Out)!["result"]!["fix"]!["applied"]!.GetValue<bool>());
        Assert.Contains("<OfframpCompileOnly>true</OfframpCompileOnly>", _cli.Repo.Read("Directory.Build.props"), StringComparison.Ordinal);

        var again = await _cli.RunAsync("doctor", "--fix", "--apply", "--json");
        Assert.True(JsonNode.Parse(again.Out)!["result"]!["fix"]!["alreadyPresent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Doctor_reports_windows_only_steps_from_the_model()
    {
        await ScanAsync();

        var run = await _cli.RunAsync("doctor");

        Assert.Contains("Windows-only build steps", run.Out, StringComparison.Ordinal);
        Assert.Contains("3 project(s) need Windows to build", run.Out, StringComparison.Ordinal);
        Assert.Contains("offramp doctor --fix --apply", run.Out, StringComparison.Ordinal);
    }

    private static JsonNode Diagnostic(CliRun run, string code) =>
        JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == code)!;
}
