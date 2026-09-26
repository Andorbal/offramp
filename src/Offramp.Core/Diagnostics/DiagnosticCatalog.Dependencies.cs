namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string DependenciesArea = "deps";

    public static readonly DiagnosticDescriptor OFR1001 = new(
        "OFR1001", Severity.Error,
        "no package version supports the target",
        "No published version of the package has assets compatible with the target framework, so the projects using it cannot move to the target with it.",
        "A package that only ever shipped .NET Framework assets (for example Microsoft.AspNet.WebApi.Core).",
        "Replace the package with its successor (the message names one when rules/package-map.yml or deps.packageMap knows it), or isolate the code that uses it behind a seam.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1002 = new(
        "OFR1002", Severity.Warning,
        "in-use version does not support the target",
        "A version of the package in use has no assets for the target framework, but a newer version does.",
        "An old version that predates the package's .NET Standard or modern .NET support.",
        "Upgrade to the version the message names or later (`deps consolidate` picks one version for the solution).",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1003 = new(
        "OFR1003", Severity.Warning,
        "package deprecated",
        "The feed marks the package, or the version in use, as deprecated; the message carries the reasons and the alternate the feed suggests.",
        "A package its authors no longer maintain (reason Legacy), or one with critical bugs.",
        "Move to the alternate the feed suggests, or record the decision to keep it.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1004 = new(
        "OFR1004", Severity.Warning,
        "package assets are Windows-only",
        "The assets NuGet would pick for the target are marked [SupportedOSPlatform(\"windows\")] or reference Windows-only assemblies (Windows Forms, WPF, System.Web, System.Drawing, the registry, directory services).",
        "A package that wraps Windows APIs, such as System.Drawing.Common on .NET 6 and later.",
        "Fine if the application stays on Windows; otherwise choose a cross-platform alternative before containerizing.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1005 = new(
        "OFR1005", Severity.Warning,
        "package not found on any feed",
        "None of the configured feeds has the package, so its support for the target is unknown.",
        "A private package on a feed missing from nuget.config, or a package removed from its feed.",
        "Add the feed to nuget.config (or `deps.feeds`), or ignore the package with `deps.ignore`.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1006 = new(
        "OFR1006", Severity.Warning,
        "feed unreachable; result partial",
        "A NuGet feed could not be queried, so any answer that depends on it is incomplete.",
        "No network, a feed that is down, or missing credentials for a private feed.",
        "Check `nuget.config`, network access, and credential providers, then re-run.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1200 = new(
        "OFR1200", Severity.Error,
        "package not referenced",
        "`deps consolidate --package` names a package no project in the workspace model references directly.",
        "A typo, a package that only arrives transitively, or a model scanned before the reference was added.",
        "Check the id (`offramp deps audit` lists them), or run `offramp scan` again.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1203 = new(
        "OFR1203", Severity.Warning,
        "pin kept a package below the otherwise-selected version",
        "A pin in offramp.yml keeps a project on an older version than the one the rest of the solution consolidates to; under central package management the project gets `VersionOverride`.",
        "A deliberate pin (its reason is quoted).",
        "Nothing, while the pin's reason holds; remove the pin to consolidate the project too.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1210 = new(
        "OFR1210", Severity.Error,
        "pin conflicts with a transitive lower bound",
        "A pinned version is lower than a range another package in the same graph asks for, so restore would report a downgrade (NU1605). The chain from the direct reference to the range is attached.",
        "A pin older than what a dependency now requires.",
        "Isolate the pinned project from that dependency, raise the pin, or add `NoWarn NU1605` to that project with a recorded reason.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1211 = new(
        "OFR1211", Severity.Error,
        "restore verification failed",
        "`dotnet restore` of the proposed project files, in a scratch copy of the repository, reported NU1605 (downgrade), NU1107 (version conflict), NU1608 (outside a dependency's range), NU1010 (missing PackageVersion), or a restore error that the current files do not. Nothing was applied; the warnings are quoted verbatim.",
        "A constraint the workspace model does not show (a conditional reference, a package's own dependencies at the new version, an SDK-implicit reference).",
        "Read the quoted warnings; pin or consolidate the package they name, then run again.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1212 = new(
        "OFR1212", Severity.Error,
        "no version satisfies every constraint",
        "No published version of the package is at least every lower bound, within every upper bound, and supports every target framework of the projects that reference it. The package keeps its versions.",
        "An upper bound from one dependency below the lower bound from another, or a package whose newer versions dropped a framework still in use.",
        "Read the attached constraints; upgrade or replace the package that imposes the bound, or split the projects.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1220 = new(
        "OFR1220", Severity.Warning,
        "family member lacks the family version",
        "A package in a `deps.families` family has no published version equal to the family's (the highest member version), so it keeps its own consolidated version.",
        "Families whose members version independently, or a member discontinued before the family's version.",
        "Narrow the family prefix, or replace the member.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1301 = new(
        "OFR1301", Severity.Warning,
        "project outside the solution would inherit CPM",
        "A project file that is not part of the scanned solution sits below a `Directory.Packages.props`, so central package management applies to it too, and its `PackageReference` versions stop working.",
        "A monorepo with unrelated projects under the same root as the migrating solution.",
        "Use a non-default file name for the central versions (`deps.cpm.file`) and opt the solution's projects in with `DirectoryPackagesPropsPath`, as `deps consolidate` does when this fires.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1302 = new(
        "OFR1302", Severity.Warning,
        "nested Directory.Packages.props shadows the root",
        "A `Directory.Packages.props` below the root one is found first by the projects under it and does not import the root file, so they see different versions.",
        "A copied props file in a subfolder.",
        "Import the parent file (`<Import Project=\"$([MSBuild]::GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))\" />`) or delete the nested file.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1303 = new(
        "OFR1303", Severity.Warning,
        "packages.config project cannot use CPM",
        "The project still uses `packages.config`, which central package management does not apply to.",
        "A legacy project not yet migrated to `PackageReference`.",
        "Migrate the project to `PackageReference` (`offramp csproj modernize`, or Visual Studio's migration).",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1401 = new(
        "OFR1401", Severity.Info,
        "loose DLL is another project's output",
        "A `Reference` with a `HintPath` points at a DLL whose assembly name is a project's in the solution; a `ProjectReference` builds it instead of trusting a copied file.",
        "A project's output copied into a lib folder before the projects shared a solution.",
        "Apply `deps resolve-dlls`, which swaps the reference.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1402 = new(
        "OFR1402", Severity.Info,
        "loose DLL matched to a package",
        "A `Reference` with a `HintPath` points at a DLL that a package ships (same assembly name and public key, at the referenced version or higher, for every target framework of the project).",
        "A package's DLL copied into a lib folder by hand.",
        "Apply `deps resolve-dlls`, which swaps the reference for a `PackageReference`.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1403 = new(
        "OFR1403", Severity.Warning,
        "loose DLL unmatched",
        "No project builds the DLL and no package named like the assembly ships it. Its metadata (version, target framework, public key token) is attached for a person to decide.",
        "A vendor or in-house DLL with no package, or a package whose id differs from the assembly name.",
        "Find the package or source it came from, or publish it to a private feed.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1404 = new(
        "OFR1404", Severity.Error,
        "loose Framework-only DLL with no replacement",
        "A DLL referenced by `HintPath` is built for .NET Framework, and no project or package replaces it, so the project cannot move to the target while it depends on it.",
        "A vendor library that never shipped for .NET Standard or modern .NET.",
        "Ask the vendor for a modern build, replace the library, or isolate its use behind a seam (`offramp seams`).",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1501 = new(
        "OFR1501", Severity.Info,
        "binding redirect added",
        "Assemblies in the application's package graph reference a version of the assembly other than the one deployed, and the configuration file has no redirect for it; `redirects sync` adds one to the deployed version.",
        "A package upgrade or consolidation.",
        "Nothing; review the diff and apply.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1502 = new(
        "OFR1502", Severity.Info,
        "binding redirect changed",
        "An existing redirect's range or target does not match the graph (the deployed version, from 0.0.0.0 up to the highest version referenced); `redirects sync` updates it.",
        "A package upgrade or consolidation after the redirect was written.",
        "Nothing; review the diff and apply.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1503 = new(
        "OFR1503", Severity.Info,
        "binding redirect pruned",
        "With `--prune`, a redirect for an assembly no package in the application's graph provides is removed.",
        "A package removed, or a redirect copied from another application.",
        "Nothing; review the diff and apply.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1504 = new(
        "OFR1504", Severity.Warning,
        "stale binding redirect",
        "A redirect names an assembly no package in the application's graph provides, so it redirects to a version the build does not deploy. It is kept unless `--prune` is given.",
        "A package removed, a redirect copied from another application, or a redirect for a framework assembly.",
        "Run `offramp redirects sync --prune`, or keep the redirect when a framework assembly needs it.",
        DependenciesArea);
}
