using System.Text.Json.Nodes;
using Offramp.Core.Model;
using Offramp.Core.Processes;
using Offramp.Workspace.Model;
using Offramp.Workspace.Slicing;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

public sealed class SliceBuilderTests
{
    private static readonly WorkspaceModel Model = ModelOf(
        Project("src/Core/Core.csproj"),
        Project("src/Data/Data.csproj", references: ["src/Core/Core.csproj"]),
        Project("src/Web/Web.csproj", references: ["src/Data/Data.csproj"]),
        Project("src/Other/Other.csproj"),
        Project("tests/Data.Tests/Data.Tests.csproj", references: ["src/Data/Data.csproj", "tests/Helpers/Helpers.csproj"], kind: ProjectKind.Test),
        Project("tests/Helpers/Helpers.csproj"),
        Project("tests/Web.Tests/Web.Tests.csproj", references: ["src/Web/Web.csproj"], kind: ProjectKind.Test));

    [Fact]
    public void Closure_holds_the_requested_projects_and_their_dependencies()
    {
        var slice = Build(["src/Data/Data.csproj"]);

        Assert.Equal(["src/Core/Core.csproj", "src/Data/Data.csproj"], slice.Projects);
        Assert.Equal(new SliceCounts(1, 1, 0, 0, 2), slice.Counts);
    }

    [Fact]
    public void Dependents_bring_their_own_dependencies()
    {
        var slice = Build(["src/Data/Data.csproj"], includeDependents: true);

        Assert.Equal(
            ["src/Core/Core.csproj", "src/Data/Data.csproj", "src/Web/Web.csproj", "tests/Data.Tests/Data.Tests.csproj",
             "tests/Helpers/Helpers.csproj", "tests/Web.Tests/Web.Tests.csproj"],
            slice.Projects);
        Assert.Equal(3, slice.Counts.Dependents);
    }

    [Fact]
    public void Tests_that_reference_the_slice_are_added_with_their_dependencies()
    {
        var slice = Build(["src/Core/Core.csproj"], includeTests: true);

        Assert.Equal(["src/Core/Core.csproj"], slice.Projects);

        var data = Build(["src/Data/Data.csproj"], includeTests: true);
        Assert.Equal(
            ["src/Core/Core.csproj", "src/Data/Data.csproj", "tests/Data.Tests/Data.Tests.csproj", "tests/Helpers/Helpers.csproj"],
            data.Projects);
        Assert.Equal(1, data.Counts.Tests);
    }

    [Fact]
    public void Solution_filter_paths_are_relative_to_the_solution_and_the_filter()
    {
        var content = SliceBuilder.SolutionFilter("src/App.sln", ["src/Core/Core.csproj", "tests/X/X.csproj"], "slices/core.slnf");

        var json = JsonNode.Parse(content)!;
        Assert.Equal(@"..\src\App.sln", json["solution"]!["path"]!.GetValue<string>());
        Assert.Equal([@"Core\Core.csproj", @"..\tests\X\X.csproj"], json["solution"]!["projects"]!.AsArray().Select(p => p!.GetValue<string>()));
    }

    [Fact]
    public void Slngen_format_is_a_command_line()
    {
        var slice = SliceBuilder.Build(Model, ["src/Core/Core.csproj"], false, false, "slngen", "App.sln", "slices/core.slnf");

        Assert.Equal("slngen --launch false --solutionfile slices/core.sln src/Core/Core.csproj\n", slice.Content.Replace('\\', '/'));
    }

    [Fact]
    public async Task The_filter_builds_with_dotnet()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var model = Store.WorkspaceStore.Read(fixture.WorkspacePath);
        var slice = SliceBuilder.Build(model, ["src/Shared/Shared.csproj"], false, false, "slnf", model.Solution!, "shared.slnf");
        File.WriteAllText(Path.Combine(fixture.Root, "shared.slnf"), slice.Content);

        Assert.Equal(["src/Contracts/Contracts.csproj", "src/Shared/Shared.csproj"], slice.Projects);
        var build = await ProcessRunner.Instance.RunAsync(
            new ProcessSpec("dotnet", ["build", "shared.slnf", "-nologo", "-v:minimal", "-nodeReuse:false"]) { WorkingDirectory = fixture.Root },
            TestContext.Current.CancellationToken);
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
        Assert.DoesNotContain("Tool", build.StandardOutput, StringComparison.Ordinal);
    }

    private static SliceResult Build(string[] requested, bool includeDependents = false, bool includeTests = false) =>
        SliceBuilder.Build(Model, requested, includeDependents, includeTests, "slnf", "App.sln", null);

    private static WorkspaceModel ModelOf(params ProjectInfo[] projects) => new()
    {
        CreatedAt = "2026-09-25T20:11:04Z",
        RepositoryRoot = "/repo",
        Solution = "App.sln",
        Source = new WorkspaceSource(WorkspaceSourceKind.Build, ".offramp/msbuild.binlog", new string('0', 64)),
        Sdk = new SdkInfo("10.0.100", "linux-x64"),
        Projects = projects,
        Graph = GraphBuilder.Build(projects),
    };
}
