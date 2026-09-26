using System.Text.Json.Nodes;
using Offramp.Core.Caching;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

/// <summary>ROADMAP M4 acceptance: pure renames, helpers used by production stay, a created Bar.Tests builds, rollback is exact.</summary>
public sealed class MoveCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR2103")]
    [ProducesDiagnostic("OFR2201")]
    [ProducesDiagnostic("OFR2206")]
    public async Task Dry_run_shows_the_plan_and_changes_nothing()
    {
        var fixture = await ScannedFixtures.GetAsync("tests-in-prod");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();
        var before = Tree(fixture.Root);

        var json = await cli.RunAsync("move", "tests", "--project", "Foo", "--json");
        var human = await cli.RunAsync("move", "tests", "--project", "Foo");

        Assert.Equal(0, json.ExitCode);
        SchemaAssert.ValidEnvelope(json.Out, "move-tests");
        var result = JsonNode.Parse(json.Out)!["result"]!;
        Assert.False(result["applied"]!.GetValue<bool>());
        Assert.Contains("rename from src/Foo/TestData/Builders.cs", result["preview"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["OFR2103", "OFR2201", "OFR2206"],
            JsonNode.Parse(json.Out)!["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).Where(c => c.StartsWith("OFR2", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(before, Tree(fixture.Root));
        await Verify(Scrub.Text(human.Out, fixture.Root), extension: "txt");
    }

    [Fact]
    public async Task Applying_stages_pure_renames_and_keeps_shared_code()
    {
        var fixture = await ScannedFixtures.ScanAsync("tests-in-prod");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var hashes = new[] { "src/Foo/Service/Tests/OrderServiceTests.cs", "src/Foo/TestData/Builders.cs" }
            .ToDictionary(f => f, f => ContentHash.Sha256File(repository.Directory.Combine(f.Split('/'))));

        var run = await cli.RunAsync("move", "tests", "--project", "Foo", "--apply", "--json");

        Assert.Equal(0, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.True(result["applied"]!.GetValue<bool>());
        Assert.Equal("passed", result["verify"]!["status"]!.GetValue<string>());
        Assert.Equal(
            "R100\tsrc/Foo/Service/Tests/OrderServiceTests.cs\tsrc/Foo.Tests/Service/OrderServiceTests.cs\nR100\tsrc/Foo/TestData/Builders.cs\tsrc/Foo.Tests/TestData/Builders.cs",
            (await repository.GitAsync("diff", "--cached", "-M100%", "--name-status")).StandardOutput.Trim());
        Assert.Equal(hashes["src/Foo/TestData/Builders.cs"], ContentHash.Sha256File(repository.Directory.Combine("src", "Foo.Tests", "TestData", "Builders.cs")));
        Assert.Equal(hashes["src/Foo/Service/Tests/OrderServiceTests.cs"], ContentHash.Sha256File(repository.Directory.Combine("src", "Foo.Tests", "Service", "OrderServiceTests.cs")));
        Assert.True(repository.Directory.Exists("src/Foo/Shared/Clock.cs"));
        Assert.True(repository.Directory.Exists("src/Foo/Health/StartupChecks.cs"));
        Assert.Equal(" M src/Foo/Foo.csproj", (await repository.GitAsync("status", "--porcelain", "--untracked-files=no", "--", "src")).StandardOutput
            .Split('\n').Single(l => l.StartsWith(' ')));
        Assert.Contains("<InternalsVisibleTo Include=\"Foo.Tests\" />", repository.Directory.Read("src/Foo/Foo.csproj"), StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR2210")]
    public async Task Bar_gets_a_created_test_project_that_builds_and_rollback_restores_the_tree()
    {
        var fixture = await ScannedFixtures.ScanAsync("tests-in-prod");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var before = Tree(fixture.Root);
        var statusBefore = (await repository.GitAsync("status", "--porcelain", "--untracked-files=no")).StandardOutput;

        var run = await cli.RunAsync("move", "tests", "--project", "Bar", "--create", "--prune-packages", "--apply", "--json");

        Assert.Equal(0, run.ExitCode);
        var node = JsonNode.Parse(run.Out)!;
        SchemaAssert.ValidEnvelope(run.Out, "move-tests");
        var result = node["result"]!;
        Assert.True(result["created"]!.GetValue<bool>());
        Assert.Equal("passed", result["verify"]!["status"]!.GetValue<string>());
        Assert.Contains("src/Bar.Tests/Bar.Tests.csproj", result["verify"]!["projects"]!.AsArray().Select(p => p!["project"]!.GetValue<string>()));
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2210");
        Assert.DoesNotContain("NUnit", repository.Directory.Read("src/Bar/Bar.csproj"), StringComparison.Ordinal);
        await Verify(repository.Directory.Read("src/Bar.Tests/Bar.Tests.csproj"), extension: "xml").UseMethodName("Created_test_project");

        var rollback = await cli.RunAsync("move", "rollback", "--journal", result["journal"]!.GetValue<string>(), "--json");

        Assert.Equal(0, rollback.ExitCode);
        SchemaAssert.ValidEnvelope(rollback.Out, "move-rollback");
        Assert.Equal(before, Tree(fixture.Root));
        Assert.Equal(statusBefore, (await repository.GitAsync("status", "--porcelain", "--untracked-files=no")).StandardOutput);
    }

    [Fact]
    [ProducesDiagnostic("OFR2050")]
    public async Task A_failed_verification_rolls_the_move_back()
    {
        var fixture = await ScannedFixtures.ScanAsync("tests-in-prod");
        using var repository = fixture.Repository;
        var command = OperatingSystem.IsWindows() ? "exit /b 1" : "exit 1";
        repository.Directory.Write("offramp.yml", $"version: 1\nverify:\n  mode: command\n  command: \"{command}\"\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        cli.Machine.Setup.Add(r => r.On(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", [], spec => Offramp.Core.Processes.ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult()));
        var before = Tree(fixture.Root);

        var run = await cli.RunAsync("move", "tests", "--project", "Foo", "--apply", "--json");

        Assert.Equal(1, run.ExitCode);
        var node = JsonNode.Parse(run.Out)!;
        Assert.True(node["result"]!["rolledBack"]!.GetValue<bool>());
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2050");
        Assert.Equal(before, Tree(fixture.Root));
        Assert.Empty((await repository.GitAsync("diff", "--cached", "--name-status")).StandardOutput.Trim());
    }

    [Fact]
    [ProducesDiagnostic("OFR2151")]
    public async Task Rollback_stops_when_files_changed_since_the_move()
    {
        var fixture = await ScannedFixtures.ScanAsync("tests-in-prod");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var applied = JsonNode.Parse((await cli.RunAsync("move", "tests", "--project", "Foo", "--apply", "--verify", "none", "--json")).Out)!;
        File.AppendAllText(repository.Directory.Combine("src", "Foo", "Foo.csproj"), "<!-- later -->\n");

        var run = await cli.RunAsync("move", "rollback", "--journal", applied["result"]!["journal"]!.GetValue<string>(), "--json");

        Assert.Equal(1, run.ExitCode);
        var node = JsonNode.Parse(run.Out)!;
        Assert.Equal(["src/Foo/Foo.csproj"], node["result"]!["changed"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2151");
        Assert.True(repository.Directory.Exists("src/Foo.Tests/TestData/Builders.cs"));
    }

    [Fact]
    [ProducesDiagnostic("OFR2002")]
    public async Task The_source_cannot_be_its_own_destination()
    {
        using var cli = ModelOnly(FixtureModels.Load("tests-in-prod"));

        var run = await cli.RunAsync("move", "tests", "--project", "Foo", "--to", "src/Foo/Foo.csproj", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2002");
    }

    [Fact]
    [ProducesDiagnostic("OFR2203")]
    public async Task Without_a_test_project_or_create_nothing_moves()
    {
        using var cli = ModelOnly(FixtureModels.Load("tests-in-prod"));

        var run = await cli.RunAsync("move", "tests", "--project", "Bar", "--json");

        Assert.Equal(1, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "move-tests");
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2203");
    }

    [Fact]
    [ProducesDiagnostic("OFR2202")]
    public async Task Two_candidate_test_projects_are_ambiguous()
    {
        var model = FixtureModels.Load("tests-in-prod");
        var twin = model.Projects.Single(p => p.Name == "Foo.Tests") with { Id = "tests/Foo.Tests/Foo.Tests.csproj" };
        using var cli = ModelOnly(model with { Projects = [.. model.Projects, twin] });

        var run = await cli.RunAsync("move", "tests", "--project", "Foo", "--json");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2202");
    }

    [Fact]
    [ProducesDiagnostic("OFR2205")]
    public async Task Only_csharp_projects_are_analyzed()
    {
        var model = FixtureModels.Load("tests-in-prod");
        using var cli = ModelOnly(model with { Projects = [.. model.Projects.Select(p => p.Name == "Bar" ? p with { Language = "vb" } : p)] });

        var run = await cli.RunAsync("move", "tests", "--project", "Bar", "--create", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR2205");
    }

    /// <summary>A CLI over the fixture's sources and a stored model, without a compiler log: enough for checks made before analysis.</summary>
    private static CliHarness ModelOnly(WorkspaceModel model)
    {
        var cli = new CliHarness();
        FixtureRepository.CopyDirectory(FixtureRepository.SourcePath("tests-in-prod"), cli.Repo.Path);
        cli.Repo.Write("offramp.yml", "version: 1\n");
        WorkspaceStore.Save(cli.Repo.Combine(".offramp", "workspace.json"), model with
        {
            RepositoryRoot = cli.Repo.Path,
            Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
            Inputs = WorkspaceInputs.Collect(cli.Repo.Path, cli.Repo.Combine(".offramp"), model.Solution),
        });
        return cli;
    }

    /// <summary>Every source file (not build output, not Offramp's state) with its hash.</summary>
    internal static List<string> Tree(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal) && !f.StartsWith(".offramp/", StringComparison.Ordinal)
                && !f.Split('/').Any(s => s is "bin" or "obj"))
            .Select(f => f + " " + ContentHash.Sha256File(Path.Combine(root, f)))
            .Order(StringComparer.Ordinal)];
}
