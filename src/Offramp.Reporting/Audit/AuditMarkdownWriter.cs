using System.Globalization;
using System.Text;
using Offramp.Analysis.Audits;
using Offramp.Core.Diagnostics;

namespace Offramp.Reporting.Audit;

/// <summary>How findings are grouped in the human and Markdown views.</summary>
public enum AuditGrouping
{
    Rule,
    Project,
    Namespace,
    File,
}

/// <summary>An audit as Markdown: the summary per rule, the porting ledger (audit api), and findings grouped.</summary>
public static class AuditMarkdownWriter
{
    public static string Write(AuditResult result, AuditGrouping grouping, int? locationsPerGroup)
    {
        var b = new StringBuilder();
        var errors = result.Findings.Count(f => f.Severity == Severity.Error);
        var warnings = result.Findings.Count(f => f.Severity == Severity.Warning);
        var info = result.Findings.Count(f => f.Severity == Severity.Info);
        b.Append("# Audit ").Append(AuditRunner.Wire(result.Audit)).Append(" (").Append(result.Target).Append(")\n\n");
        b.Append(Plural(result.Findings.Count, "finding")).Append(" in ").Append(Plural(result.Projects.Count, "project")).Append(": ")
            .Append(Plural(errors, "error")).Append(", ").Append(Plural(warnings, "warning")).Append(", ").Append(info.ToString(CultureInfo.InvariantCulture)).Append(" info.\n\n");

        if (result.Summary.Count > 0)
        {
            b.Append("| Rule | Severity | Title | Findings | Projects |\n|---|---|---|---:|---:|\n");
            foreach (var rule in result.Summary)
            {
                b.Append(Invariant($"| [{rule.Rule}](https://offramp.dev/diagnostics/{rule.Rule}) | {rule.Severity.ToWire()} | {Escape(rule.Title)} | {rule.Findings} | {rule.Projects} |\n"));
            }

            b.Append('\n');
        }

        if (result.Ledger.Count > 0)
        {
            b.Append("## Porting ledger\n\n| Project | Files | Portable | Portability |\n|---|---:|---:|---:|\n");
            foreach (var project in result.Ledger)
            {
                b.Append(Invariant($"| {Escape(project.Project)} | {project.Files} | {project.PortableFiles} | {project.Portability:P0} |\n"));
            }

            b.Append('\n');
        }

        if (result.TopNamespaces.Count > 0)
        {
            b.Append("## Top namespaces\n\n| Namespace | Findings |\n|---|---:|\n");
            foreach (var ns in result.TopNamespaces)
            {
                b.Append(Invariant($"| {Escape(ns.Namespace)} | {ns.Findings} |\n"));
            }

            b.Append('\n');
        }

        foreach (var group in Group(result.Findings, grouping))
        {
            b.Append("## ").Append(Escape(group.Key)).Append('\n');
            if (grouping == AuditGrouping.Rule && AuditRules.Every.FirstOrDefault(r => r.Id == group.Key) is { } rule)
            {
                b.Append('\n').Append(Escape(rule.Title)).Append(". ").Append(Escape(rule.Recommendation)).Append('\n');
            }

            b.Append('\n');
            var shown = locationsPerGroup is { } limit ? group.Take(limit).ToList() : group.ToList();
            foreach (var finding in shown)
            {
                b.Append(Invariant($"- `{finding.File}:{finding.Line}` {finding.Rule} {finding.Severity.ToWire()}: {Escape(finding.Message)}\n"));
            }

            if (shown.Count < group.Count())
            {
                b.Append(Invariant($"- … {group.Count() - shown.Count} more\n"));
            }

            b.Append('\n');
        }

        if (result.Skipped.Count > 0)
        {
            b.Append("## Not audited\n\n");
            foreach (var skipped in result.Skipped)
            {
                b.Append("- ").Append(Escape(skipped)).Append('\n');
            }

            b.Append('\n');
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Findings grouped by the chosen key, groups in ordinal order, findings in result order.</summary>
    public static IEnumerable<IGrouping<string, AuditFinding>> Group(IReadOnlyList<AuditFinding> findings, AuditGrouping grouping) =>
        findings
            .GroupBy(f => grouping switch
            {
                AuditGrouping.Project => f.Project,
                AuditGrouping.Namespace => f.Namespace ?? "(no namespace)",
                AuditGrouping.File => f.File,
                _ => f.Rule,
            }, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

    private static string Plural(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
