using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

public sealed class GraphBuilderTests
{
    [Fact]
    public void Order_is_leaf_first_with_ordinal_tie_breaks()
    {
        var graph = GraphBuilder.Build(
        [
            Project("src/App/App.csproj", references: ["src/B/B.csproj", "src/A/A.csproj"]),
            Project("src/B/B.csproj", references: ["src/Core/Core.csproj"]),
            Project("src/A/A.csproj", references: ["src/Core/Core.csproj"]),
            Project("src/Core/Core.csproj"),
        ]);

        Assert.Empty(graph.Cycles);
        Assert.Equal(["src/Core/Core.csproj", "src/A/A.csproj", "src/B/B.csproj", "src/App/App.csproj"], graph.TopologicalOrder);
    }

    [Fact]
    public void Input_order_does_not_change_the_graph()
    {
        ProjectInfo[] projects =
        [
            Project("c.csproj", references: ["a.csproj"]),
            Project("a.csproj"),
            Project("b.csproj", references: ["a.csproj", "c.csproj"]),
            Project("d.csproj", references: ["e.csproj"]),
            Project("e.csproj", references: ["d.csproj"]),
        ];

        var expected = GraphBuilder.Build(projects);
        var reversed = GraphBuilder.Build([.. projects.Reverse()]);

        Assert.Equal(expected.Edges, reversed.Edges);
        Assert.Equal(expected.TopologicalOrder, reversed.TopologicalOrder);
        Assert.Equal(expected.Cycles.Select(c => string.Join(",", c)), reversed.Cycles.Select(c => string.Join(",", c)));
    }

    [Fact]
    public void An_acyclic_graph_reports_no_cycles()
    {
        var graph = GraphBuilder.Build([Project("a.csproj", references: ["b.csproj"]), Project("b.csproj")]);

        Assert.Empty(graph.Cycles);
    }

    [Fact]
    public void Project_reference_cycles_are_components_with_every_member()
    {
        var graph = GraphBuilder.Build(
        [
            Project("a.csproj", references: ["b.csproj"]),
            Project("b.csproj", references: ["c.csproj"]),
            Project("c.csproj", references: ["a.csproj"]),
            Project("d.csproj", references: ["a.csproj"]),
        ]);

        var cycle = Assert.Single(graph.Cycles);
        Assert.Equal(["a.csproj", "b.csproj", "c.csproj"], cycle);
        Assert.Equal(["a.csproj", "b.csproj", "c.csproj", "a.csproj"], GraphBuilder.CyclePath(cycle, graph.Edges));

        // The cycle is ordered as one unit before its dependents.
        Assert.Equal("d.csproj", graph.TopologicalOrder[^1]);
        Assert.Equal(4, graph.TopologicalOrder.Count);
    }

    [Fact]
    public void A_project_that_references_itself_is_a_cycle()
    {
        var graph = GraphBuilder.Build([Project("a.csproj", references: ["a.csproj"])]);

        Assert.Equal(["a.csproj"], Assert.Single(graph.Cycles));
    }

    [Fact]
    public void A_hint_path_to_another_projects_output_is_an_edge()
    {
        var graph = GraphBuilder.Build(
        [
            Project("src/Alpha/Alpha.csproj", assemblyName: "Alpha", references: ["src/Beta/Beta.csproj"]),
            Project("src/Beta/Beta.csproj", assemblyName: "Beta", files: [("Alpha", "src/Alpha/bin/Debug/net48/Alpha.dll")]),
        ]);

        Assert.Contains(new GraphEdge("src/Beta/Beta.csproj", "src/Alpha/Alpha.csproj", GraphEdgeKind.Assembly), graph.Edges);
        Assert.Single(graph.Cycles);
    }

    [Fact]
    public void Ambiguous_assembly_names_and_self_output_are_not_edges()
    {
        var graph = GraphBuilder.Build(
        [
            Project("one/Shared.csproj", assemblyName: "Shared"),
            Project("two/Shared.csproj", assemblyName: "Shared"),
            Project("app/App.csproj", assemblyName: "App", files: [("Shared", "lib/Shared.dll"), ("App", "bin/App.dll")]),
        ]);

        Assert.Empty(graph.Edges);
        Assert.Empty(graph.Cycles);
    }

    [Fact]
    public void References_to_projects_outside_the_model_are_ignored()
    {
        var graph = GraphBuilder.Build([Project("a.csproj", references: ["elsewhere/b.csproj"])]);

        Assert.Empty(graph.Edges);
        Assert.Equal(["a.csproj"], graph.TopologicalOrder);
    }

    internal static ProjectInfo Project(
        string id, string? assemblyName = null, string[]? references = null, (string Name, string HintPath)[]? files = null,
        ProjectKind kind = ProjectKind.Library) => new()
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(id),
            AssemblyName = assemblyName ?? Path.GetFileNameWithoutExtension(id),
            Kind = kind,
            ProjectReferences = references ?? [],
            AssemblyReferences = [.. (files ?? []).Select(f => new AssemblyReferenceInfo
            {
                Name = f.Name,
                HintPath = f.HintPath,
                Kind = AssemblyReferenceKind.File,
            })],
        };
}
