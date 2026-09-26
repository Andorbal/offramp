namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string VerificationArea = "verify";

    public static readonly DiagnosticDescriptor OFR5001 = new(
        "OFR5001", Severity.Error,
        "verification failed",
        "The verification build (or `verify.command`) failed. With a baseline, only errors the baseline does not list count.",
        "A compile error in the selected projects, a broken change, or a verification command that exited non-zero.",
        "Read the grouped errors in the result (the first occurrence of each code is shown) and the binary log under `.offramp/verify/`; fix them or record the current state with `verify --baseline`.",
        VerificationArea);

    public static readonly DiagnosticDescriptor OFR5002 = new(
        "OFR5002", Severity.Error,
        "verification timed out",
        "The verification build or command ran longer than `verify.timeoutSeconds` and was stopped.",
        "A large build, a hung process, or a timeout set too low for this repository.",
        "Raise `verify.timeoutSeconds`, narrow the build with `--projects` or `verify.projects`, or verify a slice.",
        VerificationArea);

    public static readonly DiagnosticDescriptor OFR5010 = new(
        "OFR5010", Severity.Warning,
        "new error code relative to baseline",
        "The build reports an error code that the recorded baseline does not contain.",
        "A change introduced a new kind of failure in a repository that was already failing to build in known ways.",
        "Fix the new errors, or record a new baseline with `verify --baseline` if they are expected.",
        VerificationArea);

    public static readonly DiagnosticDescriptor OFR5020 = new(
        "OFR5020", Severity.Warning,
        "finding from the verification command",
        "`verify.command` printed a JSON envelope with a finding whose code is not an Offramp code; it is reported under this code at its own severity, with the original code in `data.code`.",
        "A verification script that runs linters, tests, or other tools and reports their findings as an envelope.",
        "See the tool that reported `data.code`. Offramp codes in the envelope are merged unchanged.",
        VerificationArea);

    public static readonly DiagnosticDescriptor OFR5090 = new(
        "OFR5090", Severity.Info,
        "verification skipped by configuration",
        "`verify.mode` (or `--mode`) is `none`, so nothing was built or run.",
        "Verification turned off in `offramp.yml`, for example while iterating on a plan.",
        "Set `verify.mode` to `build` or `command` to verify changes.",
        VerificationArea);
}
