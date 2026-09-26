namespace Offramp.Core.Diagnostics;

/// <summary>A code the specification assigns but no shipped command emits yet.</summary>
public sealed record ReservedDiagnostic(string Code, string Severity, string Meaning);

/// <summary>
/// Codes referenced by <c>docs/spec/</c> that later milestones implement. Listed
/// so implementations use the agreed numbers; a code moves from here to
/// <see cref="DiagnosticCatalog"/> in the pull request that first emits it.
/// </summary>
public static class ReservedDiagnostics
{
    public static IReadOnlyList<ReservedDiagnostic> All { get; } =
    [
        new("OFR0201", "info", "Mermaid output too large to render well"),
        new("OFR1001", "error", "no package version supports the target"),
        new("OFR1002", "warning", "in-use version does not support the target"),
        new("OFR1003", "warning", "package deprecated"),
        new("OFR1004", "warning", "package assets are Windows-only"),
        new("OFR1005", "warning", "package not found on any feed"),
        new("OFR1203", "warning", "pin kept a package below the otherwise-selected version"),
        new("OFR1210", "error", "pin conflicts with a transitive lower bound (chain attached)"),
        new("OFR1211", "error", "restore verification reported NU1605/NU1107/NU1608/NU1010"),
        new("OFR1220", "warning", "family member lacks the family version"),
        new("OFR1401", "info", "loose DLL is another project's output"),
        new("OFR1402", "info", "loose DLL matched to a package"),
        new("OFR1403", "warning", "loose DLL unmatched"),
        new("OFR1404", "error", "loose Framework-only DLL with no replacement"),
        new("OFR1501–1504", "info/warning", "binding redirect added/changed/pruned/stale"),
        new("OFR2001", "error", "move would create a project reference cycle"),
        new("OFR2002", "error", "destination equals source"),
        new("OFR2010", "error", "move crosses a solution slice boundary"),
        new("OFR2050", "error", "verification failed; changes rolled back"),
        new("OFR2101", "warning", "file needs co-move"),
        new("OFR2102", "error", "required package unavailable for destination"),
        new("OFR2103", "error", "file does not compile in destination"),
        new("OFR2104", "error", "source still depends on moved code"),
        new("OFR2105", "warning", "Windows-only API in moved file"),
        new("OFR2110", "info", "partial type co-moved"),
        new("OFR2111", "warning", "destination excludes the file path"),
        new("OFR2120", "warning", "namespace differs from destination root namespace"),
        new("OFR2150", "warning", "file changed since plan"),
        new("OFR2201", "warning", "candidate referenced by production code; not moved"),
        new("OFR2202", "error", "multiple candidate test projects"),
        new("OFR2203", "error", "no test project found; use `--to` or `--create`"),
        new("OFR2204", "warning", "destination path collision"),
        new("OFR2210", "info", "test-framework packages removable from source"),
        new("OFR2301", "warning", "string reference to a moved type"),
        new("OFR3001", "error", "API missing on target"),
        new("OFR3002", "warning", "Windows-only API"),
        new("OFR3003", "error", "API throws on modern .NET"),
        new("OFR3004–3009", "error", "removed technology (WebForms, ASMX, WCF server, Remoting, WF, CAS)"),
        new("OFR3101–3120", "varies", "behavior rules (see `spec/commands/audit.md`)"),
        new("OFR3201–3211", "varies", "serialization rules"),
        new("OFR3301–3320", "varies", "native interop rules"),
        new("OFR3401–3402", "info", "dead code candidates; test-only usage"),
        new("OFR3501–3502", "warning", "public API differs between targets / from baseline"),
        new("OFR3601", "warning", "member cannot be wrapped in `#if`"),
        new("OFR4001–4003", "varies", "seams"),
        new("OFR4010", "warning", "caller instantiates concrete type directly"),
        new("OFR4020", "warning", "sync member over remote boundary"),
        new("OFR4030", "error", "gRPC unavailable for net48 host"),
        new("OFR4101–4105", "varies", "service conversion notes"),
        new("OFR4201–4202", "varies", "web scaffold notes"),
        new("OFR4301–4303", "varies", "csproj modernize notes"),
        new("OFR4401–4404", "varies", "config convert notes"),
        new("OFR4501, OFR4510", "varies", "codemod skipped site; SqlClient encrypt default"),
        new("OFR5001", "error", "verification build failed"),
        new("OFR5002", "error", "verification timed out"),
        new("OFR5010", "warning", "new error code relative to baseline"),
        new("OFR5090", "info", "verification skipped by configuration"),
        new("OFR9101", "error", "MCP request outside allowed root"),
    ];
}
