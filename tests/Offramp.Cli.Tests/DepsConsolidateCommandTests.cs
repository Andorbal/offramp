using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Tests;

/// <summary><c>deps consolidate</c> through the CLI on <c>versions</c>, planning against the recorded feed and verifying with a real restore.</summary>
public sealed class DepsConsolidateCommandTests
{
    [Fact]
    public async Task Dry_run_plans_every_package_and_changes_nothing()
    {
        using var repository = await FixtureRepository.CreateAsync("versions");
        using var cli = Harness(repository);
        var before = MoveCommandTests.Tree(repository.Path);

        var json = await cli.RunAsync("deps", "consolidate", "--all", "--verify", "none", "--json");
        var human = await cli.RunAsync("deps", "consolidate", "--all", "--verify", "none");

        Assert.Equal(0, json.ExitCode);
        SchemaAssert.ValidEnvelope(json.Out, "deps-consolidate");
        var packages = JsonNode.Parse(json.Out)!["result"]!["packages"]!.AsArray();
        Assert.Equal("13.0.3", packages.Single(p => p!["id"]!.GetValue<string>() == "Newtonsoft.Json")!["selected"]!.GetValue<string>());
        Assert.Equal(before, MoveCommandTests.Tree(repository.Path));
        await Verify(Scrub.Text(human.Out, repository.Path), extension: "txt");
    }

    [Fact]
    public async Task Applying_after_a_passing_restore_edits_the_projects_and_rollback_undoes_it()
    {
        using var repository = await FixtureRepository.CreateAsync("versions");
        using var cli = Harness(repository);
        var before = MoveCommandTests.Tree(repository.Path);

        var run = await cli.RunAsync("deps", "consolidate", "--package", "Newtonsoft.Json", "--apply", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "deps-consolidate");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.True(result["verification"]!["passed"]!.GetValue<bool>(), result["verification"]!.ToJsonString());
        Assert.True(result["applied"]!.GetValue<bool>());
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", repository.Directory.Read("src/Billing/Billing.csproj"), StringComparison.Ordinal);
        Assert.Contains("Version=\"9.0.1\"", repository.Directory.Read("src/Customer.Api/Customer.Api.csproj"), StringComparison.Ordinal);

        var rollback = await cli.RunAsync("move", "rollback", "--journal", result["journal"]!.GetValue<string>());

        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, MoveCommandTests.Tree(repository.Path));
    }

    /// <summary>The committed fixture, its stored model, and the recorded feed for Offramp's own queries (restore still uses the fixture's nuget.config).</summary>
    private static CliHarness Harness(FixtureRepository repository)
    {
        var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        VersionsFeed.WriteFolderFeed(repository.Directory.Combine(".offramp", "recorded-feed"));
        repository.Directory.Write("offramp.yml", repository.Directory.Read("offramp.yml") + "  feeds: [ .offramp/recorded-feed ]\n");
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
