using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

public sealed class VerifyCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public VerifyCommandTests()
    {
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("dual-target"), _cli.Repo.Path);
        _cli.Repo.Write("offramp.yml", "version: 1\n");
        var model = FixtureModels.Load("dual-target");
        WorkspaceStore.Save(_cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = _cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(_cli.Repo.Path, _cli.Repo.Combine(".offramp"), model.Solution),
        });
    }

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Mode_none_skips_and_validates()
    {
        var run = await _cli.RunAsync("verify", "--mode", "none", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "verify");
        var node = JsonNode.Parse(run.Out)!;
        Assert.Equal("skipped", node["result"]!["status"]!.GetValue<string>());
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR5090");
    }

    [Fact]
    public async Task Command_mode_merges_the_scripts_envelope()
    {
        _cli.Repo.Write("lint.json", """{ "diagnostics": [ { "code": "LINT042", "severity": "error", "message": "Banned API", "file": "src/Shared/Clock.cs", "line": 7 } ] }""");
        var command = OperatingSystem.IsWindows() ? "type lint.json & exit /b 2" : "cat lint.json; exit 2";
        _cli.Repo.Write("offramp.yml", $"version: 1\nverify:\n  mode: command\n  command: \"{command}\"\n");
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        _cli.Machine.Setup.Add(r => r.On(shell, [], spec => ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult()));

        var json = await _cli.RunAsync("verify", "--projects", "Shared", "--json");
        var human = await _cli.RunAsync("verify", "--projects", "Shared");

        Assert.Equal(1, json.ExitCode);
        SchemaAssert.ValidEnvelope(json.Out, "verify");
        var node = JsonNode.Parse(json.Out)!;
        Assert.Equal("LINT042", node["result"]!["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR5020" && d["data"]!["code"]!.GetValue<string>() == "LINT042");
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR5001");
        await Verify(Scrub.Text(human.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task Command_mode_without_a_command_is_a_configuration_error()
    {
        var run = await _cli.RunAsync("verify", "--mode", "command", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0053");
    }

    [Fact]
    public async Task Unknown_projects_are_usage_errors()
    {
        var run = await _cli.RunAsync("verify", "--projects", "Shared,Nope", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0021");
    }

    [Theory]
    [InlineData("--projects", "Shared", "--all")]
    [InlineData("--affected-by", "src", "--all")]
    [InlineData("--mode", "compile", "--all")]
    public async Task Invalid_options_are_usage_errors(string option, string value, string other)
    {
        var run = await _cli.RunAsync("verify", option, value, other);

        Assert.Equal(2, run.ExitCode);
    }
}
