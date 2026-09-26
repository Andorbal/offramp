using Offramp.Core.Model;
using Offramp.Reporting.Graph;
using Offramp.Workspace.Model;

namespace Offramp.Reporting.Tests;

public sealed class GraphViewTests
{
    // App (console, framework) → Web? no: App → Core (framework) → Contracts (standard)
    // Web (web, dual) → Core; Core.Tests (test, framework) → Core; Legacy (framework) ↔ Old (framework) cycle via assembly edge.
    private static readonly WorkspaceModel Model = ModelOf(
        Project("src/App/App.csproj", ProjectKind.Console, FrameworkClass.Framework, "src/Core/Core.csproj"),
        Project("src/Core/Core.csproj", ProjectKind.Library, FrameworkClass.Framework, "src/Contracts/Contracts.csproj"),
        Project("src/Contracts/Contracts.csproj", ProjectKind.Library, FrameworkClass.Standard),
        Project("src/Web/Web.csproj", ProjectKind.Web, FrameworkClass.Dual, "src/Core/Core.csproj"),
        Project("tests/Core.Tests/Core.Tests.csproj", ProjectKind.Test, FrameworkClass.Framework, "src/Core/Core.csproj"),
        Project("legacy/Legacy/Legacy.csproj", ProjectKind.Library, FrameworkClass.Framework, "legacy/Old/Old.csproj"),
        Project("legacy/Old/Old.csproj", ProjectKind.Library, FrameworkClass.Framework) with
        {
            AssemblyReferences = [new AssemblyReferenceInfo { Name = "Legacy", HintPath = "legacy/Legacy/bin/Legacy.dll", Kind = AssemblyReferenceKind.File }],
        });

    [Fact]
    public void Readiness_blockers_and_dependents_come_from_the_whole_model()
    {
        var graph = GraphView.Build(Model, new GraphViewOptions { ExcludeKinds = [ProjectKind.Test] });
        var byId = graph.Nodes.ToDictionary(n => n.Id);

        Assert.Equal(ProjectReadiness.Ready, byId["src/Core/Core.csproj"].Readiness);
        Assert.Equal(ProjectReadiness.Blocked, byId["src/App/App.csproj"].Readiness);
        Assert.Equal(["src/Core/Core.csproj"], byId["src/App/App.csproj"].Blockers);
        Assert.Equal(ProjectReadiness.Done, byId["src/Web/Web.csproj"].Readiness);
        Assert.Equal(ProjectReadiness.Done, byId["src/Contracts/Contracts.csproj"].Readiness);
        Assert.Equal(4, byId["src/Contracts/Contracts.csproj"].Dependents);
        Assert.False(byId.ContainsKey("tests/Core.Tests/Core.Tests.csproj"));
        Assert.Equal(3, byId["src/Core/Core.csproj"].Dependents);
    }

    [Fact]
    public void Cycle_members_are_blocked_by_each_other_and_marked()
    {
        var graph = GraphView.Build(Model, new GraphViewOptions());
        var legacy = graph.Nodes.Single(n => n.Name == "Legacy");

        Assert.Equal([["legacy/Legacy/Legacy.csproj", "legacy/Old/Old.csproj"]], graph.Cycles);
        Assert.True(legacy.InCycle);
        Assert.Equal(ProjectReadiness.Blocked, legacy.Readiness);
        Assert.Equal(["legacy/Old/Old.csproj"], legacy.Blockers);
        Assert.Equal(GraphHighlightMode.Cycles, graph.Highlight.Mode);
        Assert.Equal(["legacy/Legacy/Legacy.csproj", "legacy/Old/Old.csproj"], graph.Highlight.Nodes);
    }

    [Fact]
    public void Project_edges_only_hide_hint_path_edges_but_never_the_cycle()
    {
        var graph = GraphView.Build(Model, new GraphViewOptions { Edges = GraphEdgeFilter.Project });

        Assert.DoesNotContain(graph.Edges, e => e.Kind == GraphEdgeKind.Assembly);
        Assert.Single(graph.Cycles);
        Assert.True(graph.Nodes.Single(n => n.Name == "Legacy").InCycle);
    }

    [Fact]
    public void Include_kinds_is_a_whitelist_and_exclude_wins()
    {
        var graph = GraphView.Build(Model, new GraphViewOptions
        {
            IncludeKinds = [ProjectKind.Library, ProjectKind.Test],
            ExcludeKinds = [ProjectKind.Test],
        });

        Assert.All(graph.Nodes, n => Assert.Equal(ProjectKind.Library, n.Kind));
        Assert.All(graph.Edges, e => Assert.Contains(graph.Nodes, n => n.Id == e.From));
    }

    [Theory]
    [InlineData(GraphDirection.Dependencies, 1, new[] { "src/Contracts/Contracts.csproj", "src/Core/Core.csproj" })]
    [InlineData(GraphDirection.Dependents, 1, new[] { "src/App/App.csproj", "src/Core/Core.csproj", "src/Web/Web.csproj", "tests/Core.Tests/Core.Tests.csproj" })]
    [InlineData(GraphDirection.Both, 1, new[] { "src/App/App.csproj", "src/Contracts/Contracts.csproj", "src/Core/Core.csproj", "src/Web/Web.csproj", "tests/Core.Tests/Core.Tests.csproj" })]
    public void Focus_walks_the_requested_direction_to_the_depth(GraphDirection direction, int depth, string[] expected)
    {
        var graph = GraphView.Build(Model, new GraphViewOptions { Focus = "src/Core/Core.csproj", Depth = depth, Direction = direction });

        Assert.Equal(expected, graph.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Focus_depth_limits_the_walk()
    {
        var shallow = GraphView.Build(Model, new GraphViewOptions { Focus = "src/App/App.csproj", Depth = 1, Direction = GraphDirection.Dependencies });
        var deep = GraphView.Build(Model, new GraphViewOptions { Focus = "src/App/App.csproj", Direction = GraphDirection.Dependencies });

        Assert.Equal(["src/App/App.csproj", "src/Core/Core.csproj"], shallow.Nodes.Select(n => n.Id));
        Assert.Equal(["src/App/App.csproj", "src/Contracts/Contracts.csproj", "src/Core/Core.csproj"], deep.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Frontier_and_blockers_highlights()
    {
        var frontier = GraphView.Build(Model, new GraphViewOptions { Highlight = GraphHighlightMode.Frontier });
        var blockers = GraphView.Build(Model, new GraphViewOptions { Highlight = GraphHighlightMode.Blockers });

        Assert.Equal(["src/Core/Core.csproj"], frontier.Highlight.Nodes);
        // Core blocks App and Core.Tests; Legacy and Old block each other.
        Assert.Equal(["src/Core/Core.csproj", "legacy/Legacy/Legacy.csproj", "legacy/Old/Old.csproj"], blockers.Highlight.Nodes);
    }

    [Theory]
    [InlineData("src/Foo/Foo.csproj", "src")]
    [InlineData("src/Area/Foo/Foo.csproj", "src/Area")]
    [InlineData("Foo/Foo.csproj", ".")]
    [InlineData("Foo.csproj", ".")]
    public void Directory_is_the_parent_of_the_project_folder(string id, string expected) =>
        Assert.Equal(expected, GraphView.Directory(id));

    [Fact]
    public void Clusters_by_directory_or_kind()
    {
        var byDirectory = GraphView.Build(Model, new GraphViewOptions { Cluster = GraphClusterMode.Directory });
        var byKind = GraphView.Build(Model, new GraphViewOptions { Cluster = GraphClusterMode.Kind });
        var none = GraphView.Build(Model, new GraphViewOptions());

        Assert.Equal(["legacy", "src", "tests"], byDirectory.Nodes.Select(n => n.Cluster).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("web", byKind.Nodes.Single(n => n.Name == "Web").Cluster);
        Assert.All(none.Nodes, n => Assert.Null(n.Cluster));
    }

    internal static ProjectInfo Project(string id, ProjectKind kind, FrameworkClass frameworkClass, params string[] references) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(id),
        AssemblyName = Path.GetFileNameWithoutExtension(id),
        Kind = kind,
        FrameworkClass = frameworkClass,
        TargetFrameworks = frameworkClass switch
        {
            FrameworkClass.Framework => ["net48"],
            FrameworkClass.Standard => ["netstandard2.0"],
            FrameworkClass.Modern => ["net10.0"],
            _ => ["net48", "net10.0"],
        },
        ProjectReferences = references,
        Loc = 10,
    };

    internal static WorkspaceModel ModelOf(params ProjectInfo[] projects) => new()
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
