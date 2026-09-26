using System.Globalization;
using System.Net;
using System.Text;
using Offramp.Analysis.Seams;

namespace Offramp.Reporting.Seams;

/// <summary>
/// The type graph of <c>seams</c> as DOT or a self-contained HTML page: tainted types red,
/// clean types green, boundary types outlined, cut edges thick. The HTML lays types out in
/// layers by their longest reference path from an entry point, in name order within a layer,
/// so the same result always draws the same picture.
/// </summary>
public static class SeamsRenderer
{
    public static string Dot(SeamsResult result)
    {
        var tainted = result.Tainted.Select(t => t.Type).ToHashSet(StringComparer.Ordinal);
        var boundaries = result.Seams.Select(s => s.BoundaryType).ToHashSet(StringComparer.Ordinal);
        var b = new StringBuilder();
        b.Append("// offramp seams: ").Append(result.Project).Append('\n');
        b.Append("digraph seams {\n  rankdir=LR;\n  node [shape=box, style=filled, fontname=\"Helvetica\"];\n");
        foreach (var type in result.Types)
        {
            var fill = tainted.Contains(type) ? "#f4c7c3" : "#c8e6c9";
            var border = boundaries.Contains(type) ? ", penwidth=3" : "";
            b.Append("  \"").Append(Escape(type)).Append("\" [fillcolor=\"").Append(fill).Append('"').Append(border).Append("];\n");
        }

        foreach (var edge in result.Edges)
        {
            b.Append("  \"").Append(Escape(edge.From)).Append("\" -> \"").Append(Escape(edge.To)).Append("\" [label=\"")
                .Append(edge.Weight.ToString(CultureInfo.InvariantCulture)).Append('"')
                .Append(edge.Cut ? ", color=\"#c62828\", penwidth=3" : "").Append("];\n");
        }

        b.Append("}\n");
        return b.ToString();
    }

    public static string Html(SeamsResult result)
    {
        const int Width = 260;
        const int Height = 44;
        const int GapX = 90;
        const int GapY = 28;
        var layers = Layers(result);
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var (layer, types) in layers.GroupBy(l => l.Value).OrderBy(g => g.Key).Select(g => (g.Key, g.Select(x => x.Key).Order(StringComparer.Ordinal).ToList())))
        {
            for (var i = 0; i < types.Count; i++)
            {
                positions[types[i]] = (20 + (layer * (Width + GapX)), 20 + (i * (Height + GapY)));
            }
        }

        var svgWidth = positions.Count == 0 ? 200 : positions.Values.Max(p => p.X) + Width + 20;
        var svgHeight = positions.Count == 0 ? 100 : positions.Values.Max(p => p.Y) + Height + 20;
        var tainted = result.Tainted.Select(t => t.Type).ToHashSet(StringComparer.Ordinal);
        var boundaries = result.Seams.Select(s => s.BoundaryType).ToHashSet(StringComparer.Ordinal);
        var svg = new StringBuilder();
        svg.Append(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{svgWidth:0}\" height=\"{svgHeight:0}\" role=\"img\" aria-label=\"Type graph\">"));
        svg.Append("<defs><marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"10\" refY=\"5\" markerWidth=\"7\" markerHeight=\"7\" orient=\"auto-start-reverse\"><path d=\"M0,0L10,5L0,10z\" fill=\"#555\"/></marker></defs>");
        foreach (var edge in result.Edges)
        {
            var (fx, fy) = positions[edge.From];
            var (tx, ty) = positions[edge.To];
            var (x1, y1, x2, y2) = fx <= tx ? (fx + Width, fy + (Height / 2), tx, ty + (Height / 2)) : (fx, fy + (Height / 2), tx + Width, ty + (Height / 2));
            svg.Append(Invariant($"<line x1=\"{x1:0.#}\" y1=\"{y1:0.#}\" x2=\"{x2:0.#}\" y2=\"{y2:0.#}\" stroke=\"{(edge.Cut ? "#c62828" : "#888")}\" stroke-width=\"{(edge.Cut ? 4 : 1.5)}\" marker-end=\"url(#arrow)\"><title>{Encode(edge.From)} → {Encode(edge.To)} ({edge.Weight})</title></line>"));
        }

        foreach (var (type, (x, y)) in positions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var fill = tainted.Contains(type) ? "#f4c7c3" : "#c8e6c9";
            var stroke = boundaries.Contains(type) ? "stroke=\"#c62828\" stroke-width=\"3\"" : "stroke=\"#555\" stroke-width=\"1\"";
            var label = type.Length > 36 ? "…" + type[^35..] : type;
            svg.Append(Invariant($"<g><rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{Width}\" height=\"{Height}\" rx=\"6\" fill=\"{fill}\" {stroke}/><text x=\"{x + 10:0.#}\" y=\"{y + 27:0.#}\" font-family=\"Helvetica, Arial, sans-serif\" font-size=\"13\">{Encode(label)}</text><title>{Encode(type)}</title></g>"));
        }

        svg.Append("</svg>");

        var seams = new StringBuilder();
        foreach (var seam in result.Seams)
        {
            seams.Append(Invariant($"<section><h2>{Encode(seam.Id)}: {Encode(seam.ProposedInterface)} on {Encode(seam.BoundaryType)}</h2>"));
            seams.Append(Invariant($"<p>Callers: {Encode(string.Join(", ", seam.Callers))}. Score {seam.Score:0.00}{(seam.ArticulationPoint ? "; articulation point: extracting it disconnects the taint" : "")}.</p><ul>"));
            foreach (var member in seam.Members)
            {
                seams.Append(Invariant($"<li><code>{Encode(member.Signature)}</code>: {member.CallSites} call site{(member.CallSites == 1 ? "" : "s")}{(member.WireFriendly ? "" : "; not wire-friendly: " + Encode(string.Join("; ", member.Problems)))}{(member.Static ? "; static" : "")}</li>"));
            }

            seams.Append("</ul></section>");
        }

        var loc = result.Extraction?.EstimatedLoc ?? 0;
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Seams: {{Encode(result.Project)}}</title>
            <style>
            body { font-family: Helvetica, Arial, sans-serif; margin: 24px; color: #222; }
            .graph { overflow: auto; border: 1px solid #ddd; padding: 8px; }
            code { background: #f4f4f4; padding: 1px 4px; }
            .legend span { display: inline-block; padding: 2px 8px; margin-right: 8px; border: 1px solid #555; }
            </style>
            </head>
            <body>
            <h1>Seams in {{Encode(result.Project)}}</h1>
            <p>{{result.Tainted.Count.ToString(CultureInfo.InvariantCulture)}} types cannot port ({{loc.ToString(CultureInfo.InvariantCulture)}} lines); {{result.Seams.Count.ToString(CultureInfo.InvariantCulture)}} seam{{(result.Seams.Count == 1 ? "" : "s")}}.</p>
            <p class="legend"><span style="background:#c8e6c9">clean</span><span style="background:#f4c7c3">cannot port</span><span style="border:3px solid #c62828">boundary type</span><span style="border-bottom:4px solid #c62828">cut reference</span></p>
            <div class="graph">{{svg}}</div>
            {{seams}}
            </body>
            </html>

            """;
    }

    /// <summary>Longest path from a root (a type nothing references), ignoring edges that close cycles.</summary>
    private static Dictionary<string, int> Layers(SeamsResult result)
    {
        var targets = result.Edges.GroupBy(e => e.From, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(e => e.To).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var layer = result.Types.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        var incoming = result.Edges.Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        foreach (var root in result.Types.Where(t => !incoming.Contains(t)).Concat(result.Types))
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            void Walk(string type, int depth)
            {
                if (!path.Add(type))
                {
                    return;
                }

                layer[type] = Math.Max(layer[type], depth);
                foreach (var next in targets.GetValueOrDefault(type) ?? [])
                {
                    if (depth + 1 > layer[next] && depth < result.Types.Count)
                    {
                        Walk(next, depth + 1);
                    }
                }

                path.Remove(type);
            }

            Walk(root, layer[root]);
        }

        return layer;
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
