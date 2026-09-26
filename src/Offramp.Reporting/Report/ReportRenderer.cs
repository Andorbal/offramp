using Offramp.Core.Json;

namespace Offramp.Reporting.Report;

public static class ReportRenderer
{
    public static string Render(ReportData report, ReportFormat format, string? graphHtml = null) => format switch
    {
        ReportFormat.Html => HtmlReportWriter.Write(report, graphHtml),
        ReportFormat.Json => OfframpJson.Serialize(report, ReportingJsonContext.Default.ReportData),
        ReportFormat.Markdown => MarkdownReportWriter.Write(report),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The format implied by an output file's extension, or null.</summary>
    public static ReportFormat? FromExtension(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".HTML" or ".HTM" => ReportFormat.Html,
        ".JSON" => ReportFormat.Json,
        ".MD" or ".MARKDOWN" => ReportFormat.Markdown,
        _ => null,
    };
}
