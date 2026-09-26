using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Reporting.Graph;

[JsonConverter(typeof(CamelCaseEnumConverter<GraphFormat>))]
public enum GraphFormat
{
    Json,
    Dot,
    Mermaid,
    Html,
}

public static class GraphRenderer
{
    public static string Render(GraphDocument graph, GraphFormat format, string title) => format switch
    {
        GraphFormat.Json => OfframpJson.Serialize(graph, ReportingJsonContext.Default.GraphDocument),
        GraphFormat.Dot => DotWriter.Write(graph),
        GraphFormat.Mermaid => MermaidWriter.Write(graph),
        GraphFormat.Html => HtmlGraphWriter.Write(graph, title),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The format implied by an output file's extension, or null.</summary>
    public static GraphFormat? FromExtension(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".JSON" => GraphFormat.Json,
        ".DOT" or ".GV" => GraphFormat.Dot,
        ".MMD" or ".MERMAID" => GraphFormat.Mermaid,
        ".HTML" or ".HTM" => GraphFormat.Html,
        _ => null,
    };
}
