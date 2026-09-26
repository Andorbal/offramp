namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string LoadingArea = "project loading";
    private const string ScanArea = "scan";

    public static readonly DiagnosticDescriptor OFR0002 = new(
        "OFR0002", Severity.Warning,
        "workspace model stale",
        "Files the workspace model was built from (project files, `Directory.*.props/targets`, solutions, `packages.config`, or the log it was read from) changed since the last `scan`.",
        "Edits, a branch switch, or a pull since the model was built.",
        "Run `offramp scan` (or `offramp scan --if-stale`). `--fail-on-stale` turns this into an error.",
        WorkspaceArea);

    public static readonly DiagnosticDescriptor OFR0003 = new(
        "OFR0003", Severity.Error,
        "no binary log to reuse",
        "`scan --no-build` reuses the binary log of the previous scan, and there is none.",
        "No earlier `offramp scan`, or the state directory was cleaned.",
        "Run `offramp scan` without `--no-build`, or pass `--binlog PATH`.",
        ScanArea);

    public static readonly DiagnosticDescriptor OFR0004 = new(
        "OFR0004", Severity.Error,
        "log file not found or unreadable",
        "The binary log or compiler log passed to `scan` does not exist or is not a valid log.",
        "A wrong path, a truncated download, or a file that is not an MSBuild binary log or compiler log.",
        "Pass the path of an existing `.binlog` (from `dotnet build -bl`) or `.complog` (from `complog create`).",
        ScanArea);

    public static readonly DiagnosticDescriptor OFR0021 = new(
        "OFR0021", Severity.Error,
        "project not in the workspace model",
        "A project named on the command line is not part of the scanned solution.",
        "A typo, a path relative to another directory, or a project outside the solution or slice.",
        "Use a repository-relative project path as listed by `offramp scan --json` (`result.projects`), or rescan the right solution.",
        WorkspaceArea);

    public static readonly DiagnosticDescriptor OFR0022 = new(
        "OFR0022", Severity.Error,
        "no solution found",
        "`scan` needs a solution to build and none was given or found in the repository.",
        "A repository without `.sln`/`.slnx` files, or one where the solution lives outside the repository root.",
        "Pass `--solution PATH`, set `solution:` in `offramp.yml`, or pass `--binlog`/`--complog` from a build made elsewhere.",
        WorkspaceArea);

    public static readonly DiagnosticDescriptor OFR0101 = new(
        "OFR0101", Severity.Warning,
        "project could not be loaded",
        "A project listed in the solution has no usable evaluation in the build log, so it is missing from the model. The message carries the reason.",
        "An unsupported project type (for example `.vcxproj` or `.wixproj`), an evaluation error such as a missing SDK or import, or a project filtered out of the build.",
        "Fix the evaluation error the message names, or exclude the project from the solution filter you scan.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0102 = new(
        "OFR0102", Severity.Info,
        "project kind unknown",
        "None of the kind rules matched (for example `OutputType=WinExe` without Windows Forms or WPF), so the project's kind is `unknown`.",
        "An unusual output type or a project Offramp does not recognize.",
        "Set the kind in `offramp.yml`: `projects: [{ path: ..., kind: console }]`.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0103 = new(
        "OFR0103", Severity.Info,
        "model built from a compiler log alone",
        "A compiler log records compiler invocations only, so the model lacks what MSBuild evaluation provides: package references and versions, the SDK, test-project detection, Windows-only build steps, and central package management settings.",
        "`scan --complog` without the binary log the compiler log was made from.",
        "Copy the binary log from the machine that produced the compiler log and run `offramp scan --binlog build.binlog --complog build.complog`.",
        ScanArea);

    public static readonly DiagnosticDescriptor OFR0104 = new(
        "OFR0104", Severity.Warning,
        "package graph unavailable",
        "The project's `project.assets.json` does not exist in this checkout, so its resolved packages (`resolved`) are missing from the model.",
        "Scanning a log built on another machine or in another checkout without restoring here, or a restore that failed.",
        "Run `dotnet restore` on the solution, then scan again.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0110 = new(
        "OFR0110", Severity.Warning,
        "build step needs Windows: sgen",
        "`GenerateSerializationAssemblies` runs sgen, which loads the built assembly under the .NET Framework runtime; the build fails outside Windows (MSB3474).",
        "`<GenerateSerializationAssemblies>On</GenerateSerializationAssemblies>` in the project or an imported props file.",
        "Add the compile-only block to `Directory.Build.props` (`offramp doctor --fix --apply`), which turns sgen off outside Windows; on modern .NET use `Microsoft.XmlSerializer.Generator` or drop it.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0111 = new(
        "OFR0111", Severity.Warning,
        "build step needs Windows: COM reference",
        "`COMReference` items are imported with the type library importer, which only exists on Windows (MSB4803 elsewhere).",
        "A COM type library referenced from the project.",
        "Reference the generated interop assembly as a file, or build the project only on Windows and analyze it from a compiler log captured there.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0112 = new(
        "OFR0112", Severity.Warning,
        "build step needs Windows: EDMX EntityDeploy",
        "`EntityDeploy` items embed an Entity Framework 6 designer model with a build task that ships with Visual Studio.",
        "An `.edmx` model in the project.",
        "Move to code-first mappings, or use the compiler-log route for this project.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0113 = new(
        "OFR0113", Severity.Warning,
        "build step needs Windows: T4 or Fakes",
        "T4 templates transformed at build time (`TransformOnBuild`, TextTemplating targets) or Microsoft Fakes assemblies need Visual Studio build targets.",
        "`TransformOnBuild=true`, an import of `Microsoft.TextTemplating.targets`, or `Fakes` items.",
        "Check the generated output in and turn build-time transformation off, or run those builds on Windows.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0114 = new(
        "OFR0114", Severity.Warning,
        "build step needs Windows: SSDT",
        "SQL Server Data Tools projects (`.sqlproj`) build with Windows-only targets.",
        "A classic SSDT database project in the solution.",
        "Move to `MSBuild.Sdk.SqlProj`, which builds cross-platform, or exclude the project from scans outside Windows.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0115 = new(
        "OFR0115", Severity.Warning,
        "build step needs Windows: build event calling a Windows executable",
        "A pre- or post-build event runs a Windows command (`.exe`, `.bat`, `xcopy`, `%VAR%`, ...), which fails elsewhere.",
        "A `PreBuildEvent`/`PostBuildEvent` written for cmd.exe.",
        "Guard the event with `Condition=\"'$(OS)' == 'Windows_NT'\"` or `'$(OfframpCompileOnly)' != 'true'`, or replace it with MSBuild tasks.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0120 = new(
        "OFR0120", Severity.Warning,
        "project reference cycle",
        "Projects depend on each other in a loop, through `ProjectReference` items or `HintPath` references to each other's build output. The message shows the loop.",
        "A `HintPath` to another project's `bin` folder added to work around a build order problem.",
        "Break the loop: extract the shared code into a new project, or replace the `HintPath` with a `ProjectReference` in one direction only.",
        LoadingArea);

    public static readonly DiagnosticDescriptor OFR0130 = new(
        "OFR0130", Severity.Error,
        "analysis build failed; model partial",
        "The build `scan` ran (or the log it read) has errors, so some projects have no compiler call. The model is written anyway; affected projects are marked `partial: true`.",
        "A compile error, a missing SDK or package, or a Windows-only build step on macOS or Linux.",
        "Fix the first errors listed, add the compile-only block for Windows-only steps (`offramp doctor --fix`), or scan a log captured on Windows.",
        ScanArea);

    public static readonly DiagnosticDescriptor OFR0131 = new(
        "OFR0131", Severity.Error,
        "analysis build timed out",
        "The build `scan` ran did not finish within `verify.timeoutSeconds`.",
        "A very large solution, or a build step waiting for input.",
        "Raise `verify.timeoutSeconds`, scan a solution filter (`offramp slice`), or pass a binary log built elsewhere with `--binlog`.",
        ScanArea);

    public static readonly DiagnosticDescriptor OFR0132 = new(
        "OFR0132", Severity.Warning,
        "compiler calls unavailable for some projects",
        "Some compiler invocations are missing from the compiler log: their inputs were missing when the binary log was converted, or the binary log was captured in another checkout or on another machine and cannot be converted here. Semantic commands skip the projects without one.",
        "Converting a binary log that was built on another machine, or a build that did not compile every project.",
        "Convert the binary log to a compiler log on the machine that built it (`complog create`), then scan with `--binlog` and `--complog`.",
        ScanArea);
}
