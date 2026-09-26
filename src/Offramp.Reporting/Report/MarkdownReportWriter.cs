using System.Globalization;
using System.Text;

namespace Offramp.Reporting.Report;

/// <summary>The report's tables as GitHub-flavored Markdown, for pasting into an issue or a wiki.</summary>
public static class MarkdownReportWriter
{
    /// <param name="report">The report.</param>
    /// <param name="summary">The summary paragraph; null writes <see cref="ReportText.Summary"/>.</param>
    public static string Write(ReportData report, ReportSummary? summary = null)
    {
        summary ??= new ReportSummary { Text = ReportText.Summary(report) };
        var h = report.Headline;
        var md = new StringBuilder();
        md.Append("# ").Append(Cell(report.Title)).Append("\n\n");
        md.Append("As of ").Append(ReportText.Moment(report.AsOf)).Append(" UTC");
        if (report.Since is not null)
        {
            md.Append(", since ").Append(report.Since);
        }

        md.Append(".\n\n");
        md.Append(summary.Text.Trim()).Append("\n\n");
        if (summary.Source is not null)
        {
            md.Append("_Summary written by a language model from the numbers below (`--llm`)._\n\n");
        }

        md.Append("- **Portable:** ").Append(h.PortablePercent.ToString("0.#", CultureInfo.InvariantCulture)).Append("% of ")
            .Append(ReportText.Count(h.Loc, "line")).Append(" are in standard, modern, or dual projects.\n");
        md.Append("- **Framework-only:** ").Append(ReportText.Count(h.FrameworkLoc, "line")).Append(" in ")
            .Append(ReportText.Count(h.FrameworkProjects, "project")).Append(ReportText.Change(report)).Append(".\n");
        md.Append("- **Applications done:** ").Append(ReportText.Fraction(h.ApplicationsDone, h.Applications)).Append(".\n");
        md.Append("- **Ready to port today:** ").Append(ReportText.Count(h.Ready, "project")).Append(".\n");

        md.Append("\n## Burn-down\n\n");
        Table(md, ["Scan", .. ReportBuilder.Classes, "total"], [false, true, true, true, true, true],
            report.Series.Select(p => (IReadOnlyList<string>)[ReportText.Moment(p.CreatedAt), .. ReportBuilder.Classes.Select(c => N(p.ByFrameworkClass[c].Loc)), N(p.Loc)]));

        md.Append("\n## By area\n\nLines of code by framework class.\n\n");
        Table(md, ["Area", "projects", .. ReportBuilder.Classes, "total"], [false, true, true, true, true, true, true],
            report.Areas.Select(a => (IReadOnlyList<string>)[Code(a.Area), N(a.Projects), .. ReportBuilder.Classes.Select(c => N(a.ByFrameworkClass[c].Loc)), N(a.Loc)]));

        md.Append("\n## Applications\n\n");
        if (report.Applications.Count == 0)
        {
            md.Append("No console, service, web, or desktop applications in the workspace.\n");
        }
        else
        {
            Table(md, ["Application", "Kind", "Status", "Projects left", "Lines left", "Port next"], [false, false, false, true, true, false],
                report.Applications.Select(a => (IReadOnlyList<string>)
                [
                    Code(a.Project), ReportText.Wire(a.Kind), ReportText.Wire(a.Status),
                    ReportText.Fraction(a.Remaining, a.Closure), N(a.RemainingLoc), string.Join(", ", a.Next.Select(Code)),
                ]));
        }

        md.Append("\n## Ready to port today\n\n");
        if (report.Frontier.Count == 0)
        {
            md.Append(h.FrameworkProjects == 0 ? "Nothing is framework-only any more.\n" : "Every framework-only project waits on another.\n");
        }
        else
        {
            Table(md, ["Project", "Lines", "Dependents"], [false, true, true],
                report.Frontier.Select(f => (IReadOnlyList<string>)[Code(f.Project), N(f.Loc), N(f.Dependents)]));
        }

        return md.ToString();
    }

    private static void Table(StringBuilder md, IReadOnlyList<string> headers, IReadOnlyList<bool> numeric, IEnumerable<IReadOnlyList<string>> rows)
    {
        md.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n|");
        foreach (var right in numeric)
        {
            md.Append(right ? "---:|" : "---|");
        }

        md.Append('\n');
        foreach (var row in rows)
        {
            md.Append("| ").Append(string.Join(" | ", row)).Append(" |\n");
        }
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Code(string value) => "`" + value.Replace("`", "'", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal) + "`";

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
