using System.Text;

namespace Offramp.Core.Diagnostics;

/// <summary>Renders <c>docs/diagnostics.md</c> from <see cref="DiagnosticCatalog"/> and <see cref="ReservedDiagnostics"/>.</summary>
public static class DiagnosticsDocument
{
    public static readonly IReadOnlyList<(string Range, string Area)> Ranges =
    [
        ("OFR0001–0099", "workspace/model, configuration, and environment"),
        ("OFR0100–0199", "project loading"),
        ("OFR0200–0299", "graph/report"),
        ("OFR1000–1999", "dependencies"),
        ("OFR2000–2999", "moves"),
        ("OFR3000–3999", "audits"),
        ("OFR4000–4999", "scaffolding, seams, codemods"),
        ("OFR5000–5999", "verification"),
        ("OFR9000–9999", "MCP and LLM"),
    ];

    public static string Render()
    {
        var b = new StringBuilder();
        b.Append("# Diagnostics\n\n");
        b.Append("Every Offramp diagnostic has a stable code, a meaning, a typical cause, and a fix.\n");
        b.Append("This file is generated from `src/Offramp.Core/Diagnostics/DiagnosticCatalog*.cs`\n");
        b.Append("by `eng/gen-diagnostics.sh`; do not edit it by hand. A test fails when the file\n");
        b.Append("and the catalog disagree, and another fails when a code has no test that produces it.\n\n");
        b.Append("Severity may be overridden per code in `offramp.yml` (`rules:`); overridden\n");
        b.Append("findings carry `\"overridden\": true`. The severity listed here is the default;\n");
        b.Append("where a command reports a code at another severity, the entry says so.\n\n");

        b.Append("## Ranges\n\n| Range | Area |\n|---|---|\n");
        foreach (var (range, area) in Ranges)
        {
            b.Append("| ").Append(range).Append(" | ").Append(area).Append(" |\n");
        }

        b.Append("\n## Codes\n\n| Code | Severity | Area | Title |\n|---|---|---|---|\n");
        foreach (var d in DiagnosticCatalog.All)
        {
            b.Append("| [").Append(d.Code).Append("](#").Append(d.Code.ToLowerInvariant()).Append(") | ")
             .Append(d.DefaultSeverity.ToWire()).Append(" | ").Append(d.Area).Append(" | ").Append(d.Title).Append(" |\n");
        }

        foreach (var d in DiagnosticCatalog.All)
        {
            b.Append("\n### ").Append(d.Code).Append("\n\n");
            b.Append("**").Append(d.Title).Append("** · ").Append(d.DefaultSeverity.ToWire()).Append(" · ").Append(d.Area).Append("\n\n");
            b.Append(d.Meaning).Append("\n\n");
            b.Append("- **Typical cause:** ").Append(d.Cause).Append('\n');
            b.Append("- **Fix:** ").Append(d.Fix).Append('\n');
        }

        b.Append("\n## Reserved codes\n\n");
        b.Append("Codes the specification assigns to commands that have not shipped yet. Implementations\n");
        b.Append("use these numbers; each moves to the table above in the pull request that first emits it.\n\n");
        b.Append("| Code | Severity | Meaning |\n|---|---|---|\n");
        foreach (var r in ReservedDiagnostics.All)
        {
            b.Append("| ").Append(r.Code).Append(" | ").Append(r.Severity).Append(" | ").Append(r.Meaning).Append(" |\n");
        }

        return b.ToString();
    }
}
