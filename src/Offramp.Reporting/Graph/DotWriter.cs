using System.Globalization;
using System.Text;
using Offramp.Core.Model;

namespace Offramp.Reporting.Graph;

/// <summary>Graphviz DOT with the visual encoding of <c>docs/spec/commands/graph.md</c>; nodes and edges in ordinal order.</summary>
public static class DotWriter
{
    private const string Highlight = "#D69E2E";

    public static string Write(GraphDocument graph)
    {
        var builder = new StringBuilder();
        builder.Append("// offramp graph: ").Append(Count(graph.Nodes.Count, "project")).Append(", ").Append(Count(graph.Edges.Count, "reference")).Append('\n');
        builder.Append("// fill = framework class (framework ").Append(FrameworkPalette.Framework).Append(", standard ").Append(FrameworkPalette.Standard)
            .Append(", modern ").Append(FrameworkPalette.Modern).Append(", dual ").Append(FrameworkPalette.Dual).Append(");\n");
        builder.Append("// shape = kind (library rounded box, console box, service hexagon, web tab, test dashed rounded box, winforms/wpf double box);\n");
        builder.Append("// red border = in a cycle; dashed edge = HintPath reference to another project's output");
        if (graph.Highlight.Mode is GraphHighlightMode.Frontier or GraphHighlightMode.Blockers)
        {
            builder.Append("; gold border = ").Append(graph.Highlight.Mode == GraphHighlightMode.Frontier ? "ready to port" : "top blocker");
        }

        builder.Append('\n');
        builder.Append("digraph offramp {\n");
        builder.Append("  rankdir=LR;\n");
        builder.Append("  node [fontname=\"Helvetica\", fontsize=11, fontcolor=\"white\", color=\"#1A202C\"];\n");
        builder.Append("  edge [color=\"#718096\", arrowsize=0.7];\n");

        var highlighted = graph.Highlight.Mode is GraphHighlightMode.Frontier or GraphHighlightMode.Blockers
            ? graph.Highlight.Nodes.ToHashSet(StringComparer.Ordinal)
            : [];
        var clusters = graph.Nodes.Where(n => n.Cluster is not null).GroupBy(n => n.Cluster!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        for (var i = 0; i < clusters.Count; i++)
        {
            builder.Append("  subgraph cluster_").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" {\n");
            builder.Append("    label=").Append(Quote(clusters[i].Key)).Append(";\n");
            builder.Append("    style=\"rounded\";\n    color=\"#A0AEC0\";\n    fontname=\"Helvetica\";\n");
            foreach (var node in clusters[i])
            {
                builder.Append("    ").Append(Node(node, highlighted)).Append('\n');
            }

            builder.Append("  }\n");
        }

        foreach (var node in graph.Nodes.Where(n => n.Cluster is null))
        {
            builder.Append("  ").Append(Node(node, highlighted)).Append('\n');
        }

        var cycleOf = CycleMembership(graph);
        foreach (var edge in graph.Edges)
        {
            builder.Append("  ").Append(Quote(edge.From)).Append(" -> ").Append(Quote(edge.To));
            var attributes = new List<string>();
            if (edge.Kind == GraphEdgeKind.Assembly)
            {
                attributes.Add("style=dashed");
            }

            if (cycleOf.TryGetValue(edge.From, out var a) && cycleOf.TryGetValue(edge.To, out var b) && a == b)
            {
                attributes.Add("color=" + Quote(FrameworkPalette.Cycle));
            }

            if (attributes.Count > 0)
            {
                builder.Append(" [").Append(string.Join(", ", attributes)).Append(']');
            }

            builder.Append(";\n");
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    internal static Dictionary<string, int> CycleMembership(GraphDocument graph)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < graph.Cycles.Count; i++)
        {
            foreach (var member in graph.Cycles[i])
            {
                result[member] = i;
            }
        }

        return result;
    }

    private static string Node(GraphNode node, HashSet<string> highlighted)
    {
        var (shape, style, peripheries) = node.Kind switch
        {
            ProjectKind.Library => ("box", "rounded,filled", 1),
            ProjectKind.Console => ("box", "filled", 1),
            ProjectKind.Service => ("hexagon", "filled", 1),
            ProjectKind.Web => ("tab", "filled", 1),
            ProjectKind.Test => ("box", "rounded,dashed,filled", 1),
            ProjectKind.Winforms or ProjectKind.Wpf => ("box", "filled", 2),
            _ => ("ellipse", "filled", 1),
        };
        var attributes = new List<string>
        {
            "label=" + Quote(node.Name),
            "shape=" + shape,
            "style=" + Quote(style),
            "fillcolor=" + Quote(FrameworkPalette.Of(node.FrameworkClass)),
        };
        if (peripheries > 1)
        {
            attributes.Add("peripheries=" + peripheries.ToString(CultureInfo.InvariantCulture));
        }

        if (node.InCycle)
        {
            attributes.Add("color=" + Quote(FrameworkPalette.Cycle));
            attributes.Add("penwidth=2.5");
        }
        else if (highlighted.Contains(node.Id))
        {
            attributes.Add("color=" + Quote(Highlight));
            attributes.Add("penwidth=3");
        }

        attributes.Add("tooltip=" + Quote(Tooltip(node)));
        return Quote(node.Id) + " [" + string.Join(", ", attributes) + "];";
    }

    internal static string Tooltip(GraphNode node) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{node.Id}\n{Wire(node.Kind)}, {Wire(node.FrameworkClass)} ({string.Join(", ", node.TargetFrameworks)})\n{node.Loc} lines, {node.PackageCount} packages\n{Wire(node.Readiness)}{(node.Blockers.Count > 0 ? ", blocked by " + string.Join(", ", node.Blockers) : "")}");

    internal static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
