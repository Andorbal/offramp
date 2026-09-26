namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string ReportingArea = "graph/report";

    public static readonly DiagnosticDescriptor OFR0201 = new(
        "OFR0201", Severity.Info,
        "graph too large for Mermaid",
        "The Mermaid graph has more than 300 projects; Mermaid renderers become slow and unreadable at that size.",
        "`graph --format mermaid` on a large solution without a focus or kind filter.",
        "Narrow the view with `--focus PROJECT --depth N` or `--exclude-kind test`, or use `--format html`.",
        ReportingArea);
}
