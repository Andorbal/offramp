namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string SeamsArea = "seams";

    public static readonly DiagnosticDescriptor OFR4001 = new(
        "OFR4001", Severity.Warning,
        "no seam found",
        "`seams` found no boundary to put an interface on: nothing uses the unportable symbols, the taint reaches the project's entry points directly, or the smallest boundary crosses more references than --max-cut allows.",
        "Unportable types in the project's public API, or an unportable base type every class derives from.",
        "Check the unportable symbols (--symbols, seams.unportableSymbols); split the project first, or raise --max-cut.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4002 = new(
        "OFR4002", Severity.Warning,
        "seam member not wire-friendly",
        "A member the clean side calls on the boundary type takes or returns something that cannot cross a network boundary as data (a delegate, event, stream, pointer, `ref`/`out` parameter, `object`, interface, or a type without public settable properties).",
        "Callbacks, streams, and domain objects with behavior in the boundary's signatures.",
        "Change the member to exchange data (DTOs), or keep it local with `remote --skip-member`.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4003 = new(
        "OFR4003", Severity.Warning,
        "static member on the boundary",
        "The clean side calls a static member of the boundary type; an interface cannot declare it, so it needs an instance wrapper.",
        "Static helpers and availability checks on the unportable class.",
        "Add an instance member that calls the static one and use it through the interface.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4010 = new(
        "OFR4010", Severity.Warning,
        "caller instantiates concrete type directly",
        "`extract interface` left a caller depending on the concrete type because it creates the instance itself with `new`; the interface cannot be swapped for that caller until the instance is injected.",
        "Service locator style code and classes that build their own dependencies.",
        "Take the interface as a constructor parameter (or resolve it from the container) and remove the `new`.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4011 = new(
        "OFR4011", Severity.Error,
        "extract type not found",
        "`extract interface --type` names no class or struct declared in the project's recorded compilation.",
        "A typo, a nested type written with `.` instead of `+`, or a type from another project.",
        "Pass the fully qualified name of a class declared in --project (as `seams` prints it).",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4012 = new(
        "OFR4012", Severity.Error,
        "extraction would not compile",
        "The edited project was compiled in memory before anything was written, and the extraction introduced errors (or a source file changed since the last scan), so nothing was written.",
        "Members with signatures an interface cannot express, callers that pass the retyped dependency on as the concrete type, stale scans.",
        "Narrow the members with --members, run `offramp scan` again, or extract by hand; the message lists the first errors.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4013 = new(
        "OFR4013", Severity.Error,
        "seam not found",
        "`extract interface --from-seams FILE#ID` could not read the seams document, or it has no seam with that id.",
        "A path to something other than `seams --out seams.json` output, or an id from an older run.",
        "Run `offramp seams --project P --out seams.json` and pass one of its ids (seam-1, seam-2, ...).",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4020 = new(
        "OFR4020", Severity.Warning,
        "sync member over remote boundary",
        "A synchronous interface member now makes an HTTP call: the generated client blocks on it, which ties up a thread for the network round trip and can deadlock under a synchronization context.",
        "Interfaces designed for in-process calls.",
        "Generate the asynchronous variant with `remote --async-variant` and move callers to it.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4021 = new(
        "OFR4021", Severity.Error,
        "generated project directory exists",
        "`remote` writes new projects only: a directory it would generate (contracts, client, or host) already exists and is not empty, so nothing was generated.",
        "Running `remote` twice, or a name that collides with an existing project.",
        "Choose other places with --contracts-dir, --client-dir, and --host-dir, or delete the earlier output.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4022 = new(
        "OFR4022", Severity.Info,
        "host falls back to net48",
        "The implementation and the files it uses did not compile for net10.0-windows with Microsoft.Windows.Compatibility (or the check could not run), so the host is the legacy net48 fallback: OWIN self-host with ASP.NET Web API 2.",
        "Implementations that use APIs missing from modern .NET even on Windows (WCF server, Remoting, System.Web), or types from other projects.",
        "Port what the message lists, then generate again with --host-framework net10-windows.",
        SeamsArea);

    public static readonly DiagnosticDescriptor OFR4023 = new(
        "OFR4023", Severity.Error,
        "remote interface not found",
        "`remote --interface` names no interface declared in the project, the implementation is missing or ambiguous, or `--skip-member` names no member of the interface.",
        "A type from another project, several classes implementing the interface, a typo.",
        "Pass --project, and --implementation when more than one class implements the interface.",
        SeamsArea);
}
