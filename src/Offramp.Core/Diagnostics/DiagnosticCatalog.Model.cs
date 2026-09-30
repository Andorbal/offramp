namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    public static readonly DiagnosticDescriptor OFR0023 = new(
        "OFR0023", Severity.Info,
        "solution chosen among several",
        "No solution was configured and the repository has several, none alone at the root; `init` and `scan` chose one and say why: the only one without a Web Site project (which .NET's MSBuild cannot build), the one that contains every other one's projects, or the one with the most projects. `data.candidates` lists the solutions found.",
        "A repository with a main solution next to smaller ones (a minimal or sample solution, utilities), all below the root.",
        "Nothing to do when the choice is right. Otherwise pass `--solution PATH` or set `solution:` in `offramp.yml`.",
        WorkspaceArea);

    public static readonly DiagnosticDescriptor OFR0024 = new(
        "OFR0024", Severity.Info,
        "project is not C#, Visual Basic, or F#",
        "The solution has a project of another kind (C++, an installer, a database, JavaScript). Offramp does not migrate it, so it is left out of the workspace model, its framework counts, `plan`, and `report`, whether MSBuild evaluated it or not. `data.kind` names the kind; a C++ project built for .NET with `CLRSupport=NetCore` is named here too.",
        "A native C++ project, a WiX or Visual Studio Installer project, a SQL Server Database Project evaluated on Windows, a Node.js or Python project, or a shared project (`.shproj`) in the solution.",
        "Nothing to do for Offramp. Such a project keeps building as it does; migrate the .NET projects around it.",
        ScanArea);

    public static readonly DiagnosticDescriptor OFR0025 = new(
        "OFR0025", Severity.Warning,
        "C++/CLI project needs migrating",
        "A C++ project sets `CLRSupport` (`true`, `Pure`, or `Safe`): it compiles .NET Framework code (C++/CLI), so it is part of the migration, but Offramp does not analyze or convert it and leaves it out of the model and the plan. .NET runs C++/CLI on Windows only, built with `CLRSupport=NetCore`, and does not support `Pure` or `Safe`.",
        "A mixed-mode assembly that wraps a native library for the .NET projects of the solution.",
        "Port it to .NET's C++/CLI support (`CLRSupport=NetCore` with a `TargetFramework`, Windows only), or replace it with P/Invoke or a .NET library, before migrating the projects that reference it.",
        ScanArea);
}
