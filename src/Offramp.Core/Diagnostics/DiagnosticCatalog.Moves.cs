namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string MovesArea = "move";

    public static readonly DiagnosticDescriptor OFR2002 = new(
        "OFR2002", Severity.Error,
        "destination equals source",
        "The move's destination project is the source project itself.",
        "`--to` naming the source project, or a naming rule that resolves to it.",
        "Name a different destination with `--to`.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2050 = new(
        "OFR2050", Severity.Error,
        "verification failed; changes rolled back",
        "The build (or verification command) failed after the move, and `verify.onFailure: rollback` undid it from the journal: renames reversed, project files restored byte for byte, new files deleted.",
        "Moved code that compiles in isolation but breaks the solution build, a test project that does not restore, or an unrelated broken build.",
        "Read the verification errors in the result; fix them or narrow the move, then run it again. `verify.onFailure: keep` leaves a failed move in place for inspection.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2103 = new(
        "OFR2103", Severity.Warning,
        "file does not compile in the destination",
        "A trial compilation of the file in the destination project, with the references the move would add, reports errors, so the file stays where it is.",
        "The file uses an assembly the destination does not reference and Offramp cannot add (a .NET Framework reference), different preprocessor symbols or implicit usings, or code that would stay behind.",
        "Add the missing reference to the destination (a project file change in its own pull request), then plan the move again.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2104 = new(
        "OFR2104", Severity.Error,
        "source still depends on moved code",
        "Without the moved files the source project no longer compiles, and it cannot reference the destination (that would be a cycle), so nothing moves.",
        "Production code using a test or helper in a way the analysis could not see, such as through a generated file.",
        "Look at the source errors in the details, move the used code out of the test files, and plan again.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2201 = new(
        "OFR2201", Severity.Warning,
        "test code used by production code",
        "A test or helper file is used by production code (in the project, or in a project other than the destination), so moving it would break that code.",
        "A test class with a method production code calls, a builder shared with production, or a helper another project uses.",
        "Split the production part out of the file, or leave it; the referrers are listed.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2202 = new(
        "OFR2202", Severity.Error,
        "multiple candidate test projects",
        "More than one project is named after the source project plus `move.tests.targetSuffix`, so the destination is ambiguous.",
        "Test projects with the same name in different folders.",
        "Name the destination with `--to`.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2203 = new(
        "OFR2203", Severity.Error,
        "no test project found",
        "No project is named after the source project plus `move.tests.targetSuffix`, and `--create` was not given.",
        "A production project whose tests never had a project of their own.",
        "Name an existing destination with `--to`, or pass `--create` to create `<Name>.Tests` next to the source.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2204 = new(
        "OFR2204", Severity.Warning,
        "destination path collision",
        "The file's destination path already exists, or another moved file maps to it, so the file stays.",
        "A test file with the same relative path in both projects, or two files that differ only by a stripped `Tests` folder.",
        "Rename one of the files in a separate change, or set `move.tests.stripTestsSegment: false`.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2205 = new(
        "OFR2205", Severity.Error,
        "project language not supported",
        "`move tests` analyzes C# projects; the source project is in another language.",
        "A Visual Basic or F# project.",
        "Move the tests by hand.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2206 = new(
        "OFR2206", Severity.Warning,
        "file outside the project folder",
        "The file is compiled into the project through a link but lives outside the project's folder, so it has no place under the destination and stays.",
        "`<Compile Include=\"..\\Common\\X.cs\" />` sharing a file between projects.",
        "Move the shared file by hand, or stop sharing it.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2210 = new(
        "OFR2210", Severity.Info,
        "test-framework packages removable from source",
        "After the move, nothing left in the source project uses the test framework, so its test-framework package references can go.",
        "The last tests moved out of a production project.",
        "Run again with `--prune-packages`, or remove the references by hand.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2151 = new(
        "OFR2151", Severity.Error,
        "file changed since the move; rollback stopped",
        "A file the move wrote (a moved file, an edited project file, or a new file) changed after the move, so undoing it would lose that change. Nothing was rolled back.",
        "Edits made after `move tests --apply`, or a second move over the same files.",
        "Undo the later changes first (for example `git stash`), then roll back; or leave the move in place.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2001 = new(
        "OFR2001", Severity.Warning,
        "move would create a project reference cycle",
        "The file needs a project that depends on the destination, so the destination cannot reference it; the file stays. The cycle path is attached.",
        "Moving code into a lower layer while it still uses a higher one.",
        "Move the needed code down first, or leave the file; the path shows which reference closes the cycle.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2003 = new(
        "OFR2003", Severity.Error,
        "project is frozen",
        "`projects[].frozen` in `offramp.yml` marks the source or destination as frozen: nothing moves into or out of it, and its project file is never edited.",
        "A project owned by another team, or a generated one.",
        "Choose another project, or remove `frozen` if the freeze no longer applies.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2004 = new(
        "OFR2004", Severity.Error,
        "file is not part of the source project",
        "A file named for the move is not compiled by the source project (nor a .resx beside its files).",
        "A path relative to another folder, a file excluded from the project, or the wrong `--from`.",
        "Pass repository-relative paths of files the source project compiles.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2005 = new(
        "OFR2005", Severity.Error,
        "move plan file missing or invalid",
        "`move apply --plan` could not read the plan: the file does not exist, is not JSON, or is not a `move-plan.json` document.",
        "A wrong path, or a plan edited by hand into invalid JSON.",
        "Check the path, or write the plan again with `offramp move plan ... --out PATH`.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2006 = new(
        "OFR2006", Severity.Error,
        "nothing to extract",
        "`move extract` found nothing for a `--types` name (no type of that name in the source project, or several) or a `--files` pattern (no compiled file matches).",
        "A misspelled or partial type name, a type from another project, or a pattern relative to the wrong folder.",
        "Name types fully qualified (`Ns.Type`) and write `--files` patterns relative to the source project's folder.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2007 = new(
        "OFR2007", Severity.Error,
        "new project already exists",
        "`move extract` creates its project, and the project file or its folder already exists (or the workspace model has a project there). Nothing was planned.",
        "A second extract with the same `--new`, or a folder with other files in it.",
        "Choose another `--new` or `--dir`, or move the files into the existing project with `move plan --to`.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2008 = new(
        "OFR2008", Severity.Error,
        "new project's target references did not resolve",
        "`move extract` compiles the new project in memory for each of its target frameworks; for one of them, the SDK or NuGet could not resolve the reference assemblies, so nothing was planned.",
        "A target framework the installed SDK does not know, or no access to the NuGet feed that has its reference packs.",
        "Check `--tfm`, install the SDK for it, or restore once with network access.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2101 = new(
        "OFR2101", Severity.Warning,
        "file needs a co-move",
        "The file uses code declared in another file of the source project that is not moving (with `--co-move none`), or that cannot move, so the file stays.",
        "Moving part of a cluster of files that use each other.",
        "Add the needed files to the move, or use `--co-move closure` (the default).",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2102 = new(
        "OFR2102", Severity.Warning,
        "required package unavailable for destination",
        "The file uses a package with no compile assets for one of the destination's target frameworks, so the file stays.",
        "A .NET Framework-only package used by code moving to a .NET Standard or modern project.",
        "Find a package version or replacement that supports the destination (`offramp deps audit`), then plan again.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2105 = new(
        "OFR2105", Severity.Warning,
        "moved file uses Windows-only APIs",
        "The destination's platform analyzer (CA1416) reports Windows-only APIs in the file for a modern non-Windows target. The file still moves.",
        "Registry, WMI, System.Drawing, or other Windows-only APIs in code moving to a cross-platform project.",
        "Guard the calls with `OperatingSystem.IsWindows()` or mark the code `[SupportedOSPlatform(\"windows\")]` in a separate change.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2110 = new(
        "OFR2110", Severity.Info,
        "partial type co-moved",
        "The file declares part of a partial type that a moving file also declares, so they move together.",
        "Partial classes split across files (generated code, large types).",
        "Nothing to do; the files move as one.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2111 = new(
        "OFR2111", Severity.Warning,
        "destination excludes the file path",
        "The destination's project file removes the path the file would move to from its Compile items, so the file stays.",
        "A `<Compile Remove=\"...\" />` glob in the destination covering the moved folder.",
        "Adjust the destination's Remove pattern in a separate change, then plan again.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2120 = new(
        "OFR2120", Severity.Warning,
        "namespace differs from destination root namespace",
        "The file declares a namespace outside the destination's root namespace. Moves never edit namespaces; `--namespace-mismatch block` keeps such files.",
        "Code moving between projects whose namespaces follow their names.",
        "Keep the namespace (namespaces are not bound to projects), or rename it in a separate change.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2150 = new(
        "OFR2150", Severity.Warning,
        "file changed since plan",
        "A planned file no longer matches the plan (its contents changed, it is gone, or its destination is taken), so `move apply` leaves it, and every planned file that needs it, where it is.",
        "Edits made between `move plan` and `move apply`.",
        "Plan again.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2152 = new(
        "OFR2152", Severity.Error,
        "interrupted move cannot be resumed",
        "`move apply --resume` found no interrupted journal for the plan, or a file the journal still has to write is neither as the journal expects nor as it would leave it.",
        "The run finished or was rolled back already; or files were edited, moved, or restored after the interruption.",
        "Check `.offramp/journal/`. Roll the interrupted journal back with `offramp move rollback --journal PATH` and apply again.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2301 = new(
        "OFR2301", Severity.Warning,
        "string reference to a moved type",
        "A string names a type that moved together with the assembly it moved out of (`\"Ns.Type, Source\"`), in a C# string literal or a configuration or data file. Type forwarders redirect compiled references, not strings resolved at run time.",
        "`Type.GetType(\"...\")`, configuration sections, XAML, dependency-injection or serializer settings written before the move.",
        "Change the string to name the destination assembly, or keep it and rely on the forwarder only where the loader follows forwards.",
        MovesArea);

    public static readonly DiagnosticDescriptor OFR2302 = new(
        "OFR2302", Severity.Error,
        "revision not found",
        "`forwarders --since` names something that is not a commit in the repository, so the source's former public types cannot be read.",
        "A typo, a branch that exists only elsewhere, or a shallow clone without that history.",
        "Pass a commit, branch, or tag that exists locally (`git fetch` it first), or omit `--since` to use the last scan's compilation.",
        MovesArea);
}
