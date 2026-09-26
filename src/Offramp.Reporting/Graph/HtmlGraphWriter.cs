using System.Net;
using Offramp.Core.Json;

namespace Offramp.Reporting.Graph;

/// <summary>
/// A single self-contained HTML file: layout, rendering, and interaction are
/// inline; the graph data is embedded as JSON in
/// <c>&lt;script type="application/json" id="offramp-graph"&gt;</c> so other tools
/// can read it back.
/// </summary>
public static class HtmlGraphWriter
{
    public const string DataElementId = "offramp-graph";

    public static string Write(GraphDocument graph, string title) =>
        Resources.Read("graph.html")
            .Replace("{{TITLE}}", WebUtility.HtmlEncode(title), StringComparison.Ordinal)
            .Replace("{{GRAPH_JSON}}", EmbeddableJson(OfframpJson.Serialize(graph, ReportingJsonContext.Default.GraphDocument)), StringComparison.Ordinal);

    /// <summary>JSON that cannot end its script element: "&lt;/" becomes the equivalent "&lt;\/".</summary>
    internal static string EmbeddableJson(string json) =>
        json.TrimEnd('\n').Replace("</", "<\\/", StringComparison.Ordinal);
}
