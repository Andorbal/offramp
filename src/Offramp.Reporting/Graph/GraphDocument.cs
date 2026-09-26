using System.Text.Json.Serialization;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Reporting.Graph;

/// <summary>The graph data (<c>graph --format json</c>, <c>docs/spec/commands/graph.md</c>).</summary>
public sealed record GraphDocument
{
    public required IReadOnlyList<GraphNode> Nodes { get; init; }

    public required IReadOnlyList<GraphEdge> Edges { get; init; }

    /// <summary>Cycles among the shown projects, each sorted, ordered by first member.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; }

    public GraphLegend Legend { get; init; } = GraphLegend.Instance;

    public required GraphHighlight Highlight { get; init; }

    /// <summary>The options that produced this view.</summary>
    public required GraphViewOptions View { get; init; }
}

public sealed record GraphNode
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ProjectKind Kind { get; init; }

    public required FrameworkClass FrameworkClass { get; init; }

    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    public int Loc { get; init; }

    public int PackageCount { get; init; }

    public required ProjectReadiness Readiness { get; init; }

    /// <summary>Framework-only projects this one depends on, directly or transitively (in the whole model).</summary>
    public required IReadOnlyList<string> Blockers { get; init; }

    /// <summary>Projects that depend on this one, directly or transitively (in the whole model).</summary>
    public int Dependents { get; init; }

    /// <summary>The directory holding the project's directory ("src" for src/Foo/Foo.csproj; "." at the root).</summary>
    public required string Directory { get; init; }

    /// <summary>The cluster this node is drawn in, or null with <c>--cluster none</c>.</summary>
    public string? Cluster { get; init; }

    public bool InCycle { get; init; }
}

public sealed record GraphLegend
{
    public static readonly GraphLegend Instance = new();

    public SortedDictionary<string, string> FrameworkClass { get; init; } = new(StringComparer.Ordinal)
    {
        ["dual"] = FrameworkPalette.Dual,
        ["framework"] = FrameworkPalette.Framework,
        ["modern"] = FrameworkPalette.Modern,
        ["standard"] = FrameworkPalette.Standard,
    };

    /// <summary>Kind → shape, as drawn by the visual formats.</summary>
    public SortedDictionary<string, string> Kind { get; init; } = new(StringComparer.Ordinal)
    {
        ["console"] = "box",
        ["library"] = "rounded box",
        ["service"] = "hexagon",
        ["test"] = "dashed rounded box",
        ["unknown"] = "ellipse",
        ["web"] = "tab",
        ["winforms"] = "double box",
        ["wpf"] = "double box",
    };

    public SortedDictionary<string, string> Edge { get; init; } = new(StringComparer.Ordinal)
    {
        ["assembly"] = "dashed: HintPath reference to another project's output",
        ["project"] = "solid: ProjectReference",
    };

    public string Cycle { get; init; } = FrameworkPalette.Cycle;
}

[JsonConverter(typeof(CamelCaseEnumConverter<GraphHighlightMode>))]
public enum GraphHighlightMode
{
    None,
    Cycles,
    Frontier,
    Blockers,
}

/// <summary>The highlighted nodes: cycle members, the frontier (ready today), or the top blockers by how much they unlock.</summary>
public sealed record GraphHighlight(GraphHighlightMode Mode, IReadOnlyList<string> Nodes);

[JsonConverter(typeof(CamelCaseEnumConverter<GraphDirection>))]
public enum GraphDirection
{
    Both,
    Dependencies,
    Dependents,
}

[JsonConverter(typeof(CamelCaseEnumConverter<GraphClusterMode>))]
public enum GraphClusterMode
{
    None,
    Directory,
    Kind,
}

[JsonConverter(typeof(CamelCaseEnumConverter<GraphEdgeFilter>))]
public enum GraphEdgeFilter
{
    All,
    Project,
}

public sealed record GraphViewOptions
{
    public IReadOnlyList<ProjectKind> IncludeKinds { get; init; } = [];

    public IReadOnlyList<ProjectKind> ExcludeKinds { get; init; } = [];

    public string? Focus { get; init; }

    /// <summary>Levels around <see cref="Focus"/>; null for unlimited.</summary>
    public int? Depth { get; init; }

    public GraphDirection Direction { get; init; } = GraphDirection.Both;

    public GraphClusterMode Cluster { get; init; } = GraphClusterMode.None;

    public GraphHighlightMode Highlight { get; init; } = GraphHighlightMode.Cycles;

    public GraphEdgeFilter Edges { get; init; } = GraphEdgeFilter.All;
}
