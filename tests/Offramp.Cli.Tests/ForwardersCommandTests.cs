using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

/// <summary>ROADMAP M5 acceptance: <c>forwarders</c> output compiles; strings naming moved types are reported.</summary>
public sealed class ForwardersCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR2301")]
    public async Task Forwarders_for_moved_types_build_and_strings_naming_them_are_reported()
    {
        var fixture = await ScannedFixtures.ScanAsync("move-cases");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        await cli.RunAsync("move", "plan", "--from", "Legacy", "--to", "Core", "--files", "src/Legacy/Clean/Money.cs", "--out", "move-plan.json");
        Assert.Equal(0, (await cli.RunAsync("move", "apply", "--plan", "move-plan.json", "--verify", "none")).ExitCode);
        repository.Directory.Write("src/Reports/settings.json", "{\n  \"money\": \"Legacy.Clean.Money, Legacy\"\n}\n");
        repository.Directory.Write("src/Modern/Lookup.cs",
            "namespace Modern\n{\n    public static class Lookup\n    {\n        // Legacy.Clean.Money, Legacy (a comment, not a string)\n        public static System.Type? Money() => System.Type.GetType(\"Legacy.Clean.Money, Legacy\");\n    }\n}\n");

        var dryRun = await cli.RunAsync("forwarders", "--from", "Legacy", "--to", "Core", "--json");
        var since = await cli.RunAsync("forwarders", "--from", "Legacy", "--to", "Core", "--since", "HEAD", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "forwarders");
        var result = JsonNode.Parse(dryRun.Out)!["result"]!;
        Assert.Equal(["Legacy.Clean.Money src/Core/Clean/Money.cs"], result["forwarded"]!.AsArray().Select(f => $"{f!["type"]} {f["file"]}"));
        Assert.Equal(result["forwarded"]!.ToJsonString(), JsonNode.Parse(since.Out)!["result"]!["forwarded"]!.ToJsonString());
        Assert.Equal("src/Legacy/TypeForwarders.cs", result["file"]!.GetValue<string>());
        Assert.Empty(result["projectEdits"]!.AsArray());
        Assert.Equal(["src/Modern/Lookup.cs:6", "src/Reports/settings.json:2"], result["stringReferences"]!.AsArray().Select(r => $"{r!["file"]}:{r["line"]}"));
        Assert.Equal(2, JsonNode.Parse(dryRun.Out)!["diagnostics"]!.AsArray().Count(d => d!["code"]!.GetValue<string>() == "OFR2301"));
        Assert.False(repository.Directory.Exists("src/Legacy/TypeForwarders.cs"));

        var applied = await cli.RunAsync("forwarders", "--from", "Legacy", "--to", "Core", "--apply", "--json");

        Assert.Equal(0, applied.ExitCode);
        Assert.True(JsonNode.Parse(applied.Out)!["result"]!["applied"]!.GetValue<bool>());
        Assert.Contains("[assembly: global::System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::Legacy.Clean.Money))]",
            repository.Directory.Read("src/Legacy/TypeForwarders.cs"), StringComparison.Ordinal);
        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/Legacy/Legacy.csproj", "-nologo", "-v:q"]) { WorkingDirectory = repository.Path, Timeout = TimeSpan.FromMinutes(5) });
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
    }

    [Fact]
    [ProducesDiagnostic("OFR2302")]
    public async Task A_destination_that_depends_on_the_source_cannot_receive_forwards()
    {
        using var repository = await FixtureRepository.CreateAsync("move-cases");
        using var cli = WithStoredModel(repository);
        await repository.GitAsync("mv", "src/Core/Existing/Slug.cs", "src/Reports/Slug.cs");

        var cycle = await cli.RunAsync("forwarders", "--from", "Core", "--to", "Reports", "--since", "HEAD", "--json");
        var unknown = await cli.RunAsync("forwarders", "--from", "Core", "--to", "Reports", "--since", "no-such-branch", "--json");

        Assert.Equal(0, cycle.ExitCode);
        var node = JsonNode.Parse(cycle.Out)!;
        SchemaAssert.ValidEnvelope(cycle.Out, "forwarders");
        Assert.Equal(["Core.Existing.Slug"], node["result"]!["forwarded"]!.AsArray().Select(f => f!["type"]!.GetValue<string>()));
        Assert.Null(node["result"]!["file"]);
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2001");
        Assert.False(repository.Directory.Exists("src/Core/TypeForwarders.cs"));
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains(JsonNode.Parse(unknown.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2302");
    }

    /// <summary>A CLI over a committed fixture and its stored model, without building it: enough for <c>--since</c>.</summary>
    private static CliHarness WithStoredModel(FixtureRepository repository)
    {
        var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        repository.Directory.Write("offramp.yml", "version: 1\n");
        var model = FixtureModels.Load(repository.Name);
        WorkspaceStore.Save(repository.Directory.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = repository.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(repository.Path, repository.Directory.Combine(".offramp"), model.Solution),
        });
        return cli;
    }
}
