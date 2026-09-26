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
}
