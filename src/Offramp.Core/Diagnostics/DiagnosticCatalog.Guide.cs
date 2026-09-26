namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string GuideArea = "guide";

    public static readonly DiagnosticDescriptor OFR0040 = new(
        "OFR0040", Severity.Error,
        "guide progress file unreadable",
        "`offramp guide` keeps its progress in `<state>/guide.json`, and that file is not valid, so the guide stopped without changing it.",
        "The file was edited by hand, cut off by a full disk, or written by a newer Offramp.",
        "Fix the JSON, or run `offramp guide --reset all` to start the guide's record over (the workspace model and everything else stay).",
        GuideArea);

    public static readonly DiagnosticDescriptor OFR0041 = new(
        "OFR0041", Severity.Error,
        "guide step needs a project",
        "The step is done per project, several projects are still open for it, and no `--project` said which one to run.",
        "`offramp guide --run STEP` without a terminal to ask on, for a step such as `move-tests` or `port`.",
        "Pass `--project` with one of the projects the message lists, or run `offramp guide` on a terminal to pick one.",
        GuideArea);

    public static readonly DiagnosticDescriptor OFR0042 = new(
        "OFR0042", Severity.Warning,
        "guide step did not complete",
        "A step the guide ran exited with a failure, so the step stays open. The step's own output says what went wrong.",
        "`doctor` found a failing check, a command could not run (no workspace model, bad options), or an applied change failed verification.",
        "Fix what the step reported and run it again, or mark it done or skipped if it does not matter for this repository.",
        GuideArea);

    public static readonly DiagnosticDescriptor OFR0043 = new(
        "OFR0043", Severity.Error,
        "guide step cannot be skipped or marked done",
        "The guide decides this step from the repository itself: `scan` is done while the workspace model exists and is fresh, and every later step reads the model.",
        "`offramp guide --skip scan` or `--done scan`.",
        "Run `offramp scan` (or `offramp guide --run scan`).",
        GuideArea);
}
