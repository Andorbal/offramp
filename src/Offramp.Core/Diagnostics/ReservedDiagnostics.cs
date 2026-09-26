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
        new("OFR2010", "error", "move crosses a solution slice boundary"),
        new("OFR4001–4003", "varies", "seams"),
        new("OFR4010", "warning", "caller instantiates concrete type directly"),
        new("OFR4020", "warning", "sync member over remote boundary"),
        new("OFR4030", "error", "gRPC unavailable for net48 host"),
        new("OFR4101–4105", "varies", "service conversion notes"),
        new("OFR4201–4202", "varies", "web scaffold notes"),
        new("OFR4301–4303", "varies", "csproj modernize notes"),
        new("OFR4401–4404", "varies", "config convert notes"),
        new("OFR4501, OFR4510", "varies", "codemod skipped site; SqlClient encrypt default"),
        new("OFR9101", "error", "MCP request outside allowed root"),
    ];
}
