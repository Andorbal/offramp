namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string IdeArea = "ide";

    public static readonly DiagnosticDescriptor OFR6001 = new(
        "OFR6001", Severity.Info,
        "new type could live in its counterpart",
        "A type added in new code to a .NET Framework-only project needs nothing from .NET Framework: its file moves to a portable counterpart the project can reference as it is, so it would not need migrating later.",
        "Adding a class where the rest of the feature lives, out of habit, when the project has a .NET 8/10 friendly counterpart.",
        "Move the file to the counterpart (the quick fix, or `offramp move plan --files FILE --to COUNTERPART`). Its namespace stays; the project keeps using it through its reference.",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6002 = new(
        "OFR6002", Severity.Warning,
        "project map entry does not resolve",
        "A `projectMap` entry (in offramp.yml or the editor's settings) names no project of the workspace model, names one that several projects share, or maps a project to itself, so it is ignored.",
        "A typo, a renamed or removed project, or a name used by two projects.",
        "Use the project's repository-relative path, or a name only one project has. `offramp ide check` lists the counterparts each project ends up with.",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6003 = new(
        "OFR6003", Severity.Info,
        "more than a move",
        "The file could reach the counterpart, but not by a move alone: the counterpart would need a project or package reference it does not have, the file travels with a resource file, or its destination path is taken. The editor only offers moves that are nothing but a move, so it offers none here.",
        "Code that uses a library the counterpart does not reference, designer-generated code, or a file of the same name already in the counterpart.",
        "Plan the move on the command line, which adds references and co-moves (`offramp move plan`), or add the reference to the counterpart first.",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6004 = new(
        "OFR6004", Severity.Warning,
        "counterpart cannot take the project's code",
        "A counterpart (from the project map, or a portable project referenced directly) is dropped because code from the project could not live there: it is .NET Framework-only or modern-only, the project cannot reference it for one of its targets, it depends on the project, it is frozen, it is not C#, or the scan recorded no compilation for it.",
        "A map entry pointing at the wrong project, or a counterpart that references the project it serves.",
        "Point the entry at a netstandard2.0 or multi-targeted project that the source can reference and that does not depend on it.",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6005 = new(
        "OFR6005", Severity.Info,
        "no counterpart for a .NET Framework project",
        "New code in this .NET Framework-only project is checked for APIs modern .NET lacks, but no type can be suggested elsewhere: the project has no project map entry and references no portable project.",
        "A repository without a portable library next to this project yet, or without a project map.",
        "Add a `projectMap` entry for the project (offramp.yml or the editor's settings), creating the portable project first if needed (`offramp move extract` can).",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6006 = new(
        "OFR6006", Severity.Warning,
        "file not in the workspace model",
        "The file belongs to no project of the workspace model, so nothing is reported for it.",
        "A project added since the last scan, or a file outside every project.",
        "Run `offramp scan` again.",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6007 = new(
        "OFR6007", Severity.Warning,
        "new-code base unavailable",
        "What counts as new code is decided against a git base, and that base is not available: outside a git repository every line counts as new; when the configured ref does not resolve, the base falls back to HEAD.",
        "No git repository, a clone without the remote default branch (`origin/HEAD`), or a misspelled `ide.newCode.base`.",
        "Set `ide.newCode.base` (or `--base`) to a ref that exists, for example `origin/main`, or run `git remote set-head origin --auto`.",
        IdeArea);

    public static readonly DiagnosticDescriptor OFR6008 = new(
        "OFR6008", Severity.Error,
        "file has unsaved changes",
        "A move from the editor renames the file on disk with git mv, so the editor's unsaved text would be lost or moved out of step; nothing was moved.",
        "Choosing the move while the file has changes that are not saved.",
        "Save the file and choose the move again.",
        IdeArea);
}
