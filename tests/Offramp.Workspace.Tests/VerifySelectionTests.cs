using Offramp.Core.Configuration;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Model;
using Offramp.Workspace.Verification;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

public sealed class VerifySelectionTests
{
    // Core ← Data ← Web; Core ← Tool; tests/Data.Tests → Data; Other alone.
    private static readonly WorkspaceModel Model = ModelOf(
        Project("src/Core/Core.csproj") with { Compile = ["src/Core/A.cs", "src/Shared/Linked.cs"] },
        Project("src/Data/Data.csproj", references: ["src/Core/Core.csproj"]) with { Compile = ["src/Data/Repo.cs"] },
        Project("src/Web/Web.csproj", references: ["src/Data/Data.csproj"], kind: ProjectKind.Web),
        Project("src/Tool/Tool.csproj", references: ["src/Core/Core.csproj"], kind: ProjectKind.Console),
        Project("tests/Data.Tests/Data.Tests.csproj", references: ["src/Data/Data.csproj"], kind: ProjectKind.Test),
        Project("other/Other.csproj"));

    [Theory]
    [InlineData("src/Data/Repo.cs", "src/Data/Data.csproj,src/Web/Web.csproj,tests/Data.Tests/Data.Tests.csproj")]
    [InlineData("src/Shared/Linked.cs", "src/Core/Core.csproj,src/Data/Data.csproj,src/Tool/Tool.csproj")]
    [InlineData("src/Web/Web.csproj", "src/Web/Web.csproj")]
    [InlineData("src/Web/Views/Home.cshtml", "src/Web/Web.csproj")]
    [InlineData("src/Directory.Build.props", "src/Core/Core.csproj,src/Data/Data.csproj,src/Tool/Tool.csproj,src/Web/Web.csproj,tests/Data.Tests/Data.Tests.csproj")]
    [InlineData("tests", "tests/Data.Tests/Data.Tests.csproj")]
    [InlineData("README.md", "other/Other.csproj,src/Core/Core.csproj,src/Data/Data.csproj,src/Tool/Tool.csproj,src/Web/Web.csproj,tests/Data.Tests/Data.Tests.csproj")]
    public void Affected_paths_map_to_owners_and_direct_dependents(string path, string expected)
    {
        var affected = VerifySelector.Affected(Model, [path]);

        Assert.Equal(expected.Split(','), affected);
    }

    [Fact]
    public void Named_projects_are_kept_and_excludes_apply_last()
    {
        var selection = VerifySelector.Select(Model, ["src/Web/Web.csproj", "src/Core/Core.csproj"], null,
            new VerifyProjectsConfig { Exclude = ["src/Web/**"] });

        Assert.Equal(["src/Core/Core.csproj"], selection.Projects);
        Assert.False(selection.Everything);
        Assert.Equal("2 projects named with --projects, less 1 project in verify.projects.exclude", selection.Scope);
    }

    [Fact]
    public void Without_options_everything_is_verified_unless_include_narrows_it()
    {
        var everything = VerifySelector.Select(Model, null, null, new VerifyProjectsConfig());
        var included = VerifySelector.Select(Model, null, null, new VerifyProjectsConfig { Include = ["src/**/*.csproj"] });

        Assert.True(everything.Everything);
        Assert.Equal(6, everything.Projects.Count);
        Assert.Equal(4, included.Projects.Count);
        Assert.False(included.Everything);
    }

    [Fact]
    public async Task Scratch_worktree_checks_out_head_with_working_tree_files_over_it()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        var git = new GitService(ProcessRunner.Instance);
        repository.Directory.Write("src/Legacy.Core/Legacy.Core.csproj", "<Project><!-- edited --></Project>\n");

        string path;
        await using (var scratch = await ScratchWorktree.CreateAsync(repository.Path, ["src/Legacy.Core/Legacy.Core.csproj"], git, TestContext.Current.CancellationToken))
        {
            path = scratch.Path;
            Assert.Equal("<Project><!-- edited --></Project>\n", File.ReadAllText(scratch.Resolve("src/Legacy.Core/Legacy.Core.csproj")));
            Assert.True(File.Exists(scratch.Resolve("src/Legacy.App/Legacy.App.csproj")));
            Assert.Equal(2, await WorktreesAsync(repository));
        }

        Assert.False(Directory.Exists(path));
        Assert.Equal(1, await WorktreesAsync(repository));
        Assert.Equal(" M src/Legacy.Core/Legacy.Core.csproj", (await repository.GitAsync("status", "--porcelain")).StandardOutput.TrimEnd());
    }

    [Fact]
    public async Task Scratch_copy_outside_git_holds_only_the_given_files()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only", git: false);

        await using var scratch = await ScratchWorktree.CreateAsync(repository.Path, ["src/Legacy.Core/Legacy.Core.csproj"], new GitService(ProcessRunner.Instance), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(scratch.Resolve("src/Legacy.Core/Legacy.Core.csproj")));
        Assert.False(File.Exists(scratch.Resolve("src/Legacy.App/Legacy.App.csproj")));
    }

    /// <summary>
    /// ADR 0062: the scratch copy takes what the working tree has and HEAD does not, copies only what differs,
    /// and names what HEAD lacks, so <c>csproj modernize</c> can say that verification relied on it (OFR4309).
    /// </summary>
    [Fact]
    public async Task Scratch_worktree_names_the_files_it_took_that_head_does_not_have()
    {
        using var repository = await FixtureRepository.CreateAsync("netfx-only");
        var git = new GitService(ProcessRunner.Instance);
        repository.Directory.Write("src/Legacy.Core/Legacy.Core.csproj", "<Project><!-- edited --></Project>\n");
        repository.Directory.Write("data/markets.xml", "<markets />\n");
        repository.Directory.Write("src/Legacy.Core/Generated/Master.xml", "<master />\n");

        await using var scratch = await ScratchWorktree.CreateAsync(repository.Path,
            ["data/markets.xml", "src/Legacy.App/Legacy.App.csproj", "src/Legacy.Core/Generated/Master.xml", "src/Legacy.Core/Legacy.Core.csproj"], git, TestContext.Current.CancellationToken);

        Assert.Equal(["data/markets.xml", "src/Legacy.Core/Generated/Master.xml"], scratch.Uncommitted);
        Assert.Equal("<master />\n", File.ReadAllText(scratch.Resolve("src/Legacy.Core/Generated/Master.xml")));
        Assert.Equal("<Project><!-- edited --></Project>\n", File.ReadAllText(scratch.Resolve("src/Legacy.Core/Legacy.Core.csproj")));
        Assert.Equal(File.ReadAllText(repository.Directory.Combine("src", "Legacy.App", "Legacy.App.csproj")), File.ReadAllText(scratch.Resolve("src/Legacy.App/Legacy.App.csproj")));
    }

    /// <summary>ADR 0062: the files a scan's build read, from its binary log: in the repository, never the build's output.</summary>
    [Fact]
    public async Task Build_reads_are_the_repositorys_files_the_build_read_outside_bin_and_obj()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var model = fixture.Outcome.Model!;

        var reads = BuildReads.Read(RepoPaths.ToAbsolute(fixture.Root, model.Source.Path), fixture.Root, Path.Combine(fixture.Root, ".offramp"));

        Assert.Contains("Directory.Build.props", reads);
        Assert.Contains("Directory.Build.targets", reads);
        Assert.Contains("src/Shared/Clock.cs", reads);
        Assert.DoesNotContain(reads, r => r.Split('/').Any(s => s is "bin" or "obj" or ".offramp" or ".git") || r.StartsWith("../", StringComparison.Ordinal));
        Assert.Equal(reads.Order(StringComparer.Ordinal), reads);
        Assert.Empty(BuildReads.Read(Path.Combine(fixture.Root, "missing.binlog"), fixture.Root, Path.Combine(fixture.Root, ".offramp")));
    }

    private static async Task<int> WorktreesAsync(FixtureRepository repository) =>
        (await repository.GitAsync("worktree", "list", "--porcelain")).StandardOutput.Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal));

    private static WorkspaceModel ModelOf(params ProjectInfo[] projects) => new()
    {
        CreatedAt = "2026-09-25T20:11:04Z",
        RepositoryRoot = "/repo",
        Solution = "App.sln",
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Projects = [.. projects.OrderBy(p => p.Id, StringComparer.Ordinal)],
        Graph = GraphBuilder.Build(projects),
    };
}
