using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>ifdef report|wrap|strip</c> through the CLI, with the real toolchain proving the wrapped code builds.</summary>
public sealed class IfdefCommandTests
{
    [Fact]
    public async Task Report_json_validates_and_matches_the_snapshot()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("ifdef", "report", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "ifdef-report");
        await Verify(JsonNode.Parse(run.Out)!["result"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), extension: "json");
    }

    [Fact]
    public async Task Wrapping_audit_findings_lets_the_project_build_for_the_target()
    {
        var fixture = await ScannedFixtures.ScanAsync("netfx-only");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        // Without the wrap, Legacy.Core does not build for net10.0: the check can fail.
        Retarget(repository);
        Assert.False((await BuildForTarget(repository)).Succeeded);
        await repository.GitAsync("checkout", "--", "src/Legacy.Core/Legacy.Core.csproj");

        var audit = await cli.RunAsync("audit", "api", "--format", "json", "--out", "audit.json");
        var dryRun = await cli.RunAsync("ifdef", "wrap", "--findings", "audit.json", "--json");
        var applied = await cli.RunAsync("ifdef", "wrap", "--findings", "audit.json", "--apply", "--json");

        Assert.Equal(1, audit.ExitCode);
        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "ifdef-wrap");
        var result = JsonNode.Parse(applied.Out)!["result"]!;
        Assert.True(result["applied"]!.GetValue<bool>());
        Assert.Equal(["src/Legacy.Core/Thumbnails.cs", "src/Legacy.Core/UrlHelper.cs"], result["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Contains("#if NETFRAMEWORK\n        public static string CurrentPath()", repository.Directory.Read("src/Legacy.Core/UrlHelper.cs"), StringComparison.Ordinal);

        Retarget(repository);
        var build = await BuildForTarget(repository);
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
    }

    [Fact]
    [ProducesDiagnostic("OFR3604")]
    public async Task An_unreadable_findings_file_is_a_usage_error()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        fixture.Repository.Directory.Write("not-audit.json", "{ \"graph\": [] }");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("ifdef", "wrap", "--findings", "not-audit.json", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR3604", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strip_is_a_dry_run_until_applied_and_rolls_back()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var before = repository.Directory.Read("src/Shared/Clock.cs");

        var dryRun = await cli.RunAsync("ifdef", "strip", "--symbol", "NETFRAMEWORK");
        var applied = await cli.RunAsync("ifdef", "strip", "--symbol", "NETFRAMEWORK", "--apply", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        await Verify(Scrub.Text(dryRun.Out, repository.Path), extension: "txt");
        SchemaAssert.ValidEnvelope(applied.Out, "ifdef-strip");
        Assert.DoesNotContain("#if", repository.Directory.Read("src/Shared/Clock.cs"), StringComparison.Ordinal);

        var journal = JsonNode.Parse(applied.Out)!["result"]!["journal"]!.GetValue<string>();
        var rollback = await cli.RunAsync("move", "rollback", "--journal", journal, "--json");
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, repository.Directory.Read("src/Shared/Clock.cs"));
    }

    private static void Retarget(FixtureRepository repository)
    {
        var project = repository.Directory.Read("src/Legacy.Core/Legacy.Core.csproj");
        repository.Directory.Write("src/Legacy.Core/Legacy.Core.csproj", project.Replace("<TargetFramework>net48</TargetFramework>", "<TargetFrameworks>net48;net10.0</TargetFrameworks>", StringComparison.Ordinal));
    }

    private static Task<ProcessResult> BuildForTarget(FixtureRepository repository) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/Legacy.Core/Legacy.Core.csproj", "-f", "net10.0", "-nologo", "-v:q"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(5),
        });
}
