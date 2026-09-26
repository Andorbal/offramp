using System.Globalization;
using System.Text;
using Offramp.Core.Model;

namespace Offramp.Reporting.Graph;

/// <summary>
/// A Mermaid <c>flowchart LR</c>. Mermaid lacks some shapes, so kinds map to the
/// closest available ones; the legend comment lists the mapping.
/// </summary>
public static class MermaidWriter
{
    /// <summary>Above this many nodes Mermaid renders poorly (<c>OFR0201</c>).</summary>
    public const int MaxNodes = 300;

    public static string Write(GraphDocument graph)
    {
        var ids = graph.Nodes.Select((n, i) => (n.Id, Short: "n" + i.ToString(CultureInfo.InvariantCulture)))
            .ToDictionary(p => p.Id, p => p.Short, StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.Append("%% offramp graph: ").Append(graph.Nodes.Count.ToString(CultureInfo.InvariantCulture)).Append(" projects, ")
            .Append(graph.Edges.Count.ToString(CultureInfo.InvariantCulture)).Append(" references\n");
        builder.Append("%% shapes: library (rounded), console [box], service {{hexagon}}, web >flag], test (dashed rounded), winforms/wpf [[double]], unknown ((circle))\n");
        builder.Append("%% red border = in a cycle; dotted edge = HintPath reference to another project's output\n");
        builder.Append("flowchart LR\n");
        builder.Append("  classDef framework fill:").Append(FrameworkPalette.Framework).Append(",stroke:#1A202C,color:#fff\n");
        builder.Append("  classDef standard fill:").Append(FrameworkPalette.Standard).Append(",stroke:#1A202C,color:#fff\n");
        builder.Append("  classDef modern fill:").Append(FrameworkPalette.Modern).Append(",stroke:#1A202C,color:#fff\n");
        builder.Append("  classDef dual fill:").Append(FrameworkPalette.Dual).Append(",stroke:#1A202C,color:#fff\n");
        builder.Append("  classDef test stroke-dasharray:5 3\n");
        builder.Append("  classDef cycle stroke:").Append(FrameworkPalette.Cycle).Append(",stroke-width:3px\n");
        builder.Append("  classDef highlight stroke:#D69E2E,stroke-width:3px\n");

        var clusters = graph.Nodes.Where(n => n.Cluster is not null).GroupBy(n => n.Cluster!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        for (var i = 0; i < clusters.Count; i++)
        {
            builder.Append("  subgraph c").Append(i.ToString(CultureInfo.InvariantCulture)).Append("[\"").Append(Escape(clusters[i].Key)).Append("\"]\n");
            foreach (var node in clusters[i])
            {
                builder.Append("    ").Append(Node(ids[node.Id], node)).Append('\n');
            }

            builder.Append("  end\n");
        }

        foreach (var node in graph.Nodes.Where(n => n.Cluster is null))
        {
            builder.Append("  ").Append(Node(ids[node.Id], node)).Append('\n');
        }

        foreach (var edge in graph.Edges)
        {
            builder.Append("  ").Append(ids[edge.From]).Append(edge.Kind == GraphEdgeKind.Assembly ? " -.-> " : " --> ").Append(ids[edge.To]).Append('\n');
        }

        foreach (var group in graph.Nodes.GroupBy(n => n.FrameworkClass).OrderBy(g => g.Key))
        {
            AppendClass(builder, group.Select(n => ids[n.Id]), DotWriter.Wire(group.Key));
        }

        AppendClass(builder, graph.Nodes.Where(n => n.Kind == ProjectKind.Test).Select(n => ids[n.Id]), "test");
        AppendClass(builder, graph.Nodes.Where(n => n.InCycle).Select(n => ids[n.Id]), "cycle");
        if (graph.Highlight.Mode is GraphHighlightMode.Frontier or GraphHighlightMode.Blockers)
        {
            var cycle = graph.Nodes.Where(n => n.InCycle).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            AppendClass(builder, graph.Highlight.Nodes.Where(n => !cycle.Contains(n) && ids.ContainsKey(n)).Select(n => ids[n]), "highlight");
        }

        return builder.ToString();
    }

    private static void AppendClass(StringBuilder builder, IEnumerable<string> ids, string className)
    {
        var list = ids.ToList();
        if (list.Count > 0)
        {
            builder.Append("  class ").Append(string.Join(',', list)).Append(' ').Append(className).Append('\n');
        }
    }

    private static string Node(string id, GraphNode node)
    {
        var label = "\"" + Escape(node.Name) + "\"";
        return node.Kind switch
        {
            ProjectKind.Library or ProjectKind.Test => $"{id}({label})",
            ProjectKind.Console => $"{id}[{label}]",
            ProjectKind.Service => $"{id}{{{{{label}}}}}",
            ProjectKind.Web => $"{id}>{label}]",
            ProjectKind.Winforms or ProjectKind.Wpf => $"{id}[[{label}]]",
            _ => $"{id}(({label}))",
        };
    }

    private static string Escape(string value) => value.Replace("\"", "#quot;", StringComparison.Ordinal);
}
