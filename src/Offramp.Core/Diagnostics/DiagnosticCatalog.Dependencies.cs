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
        "The assets NuGet would pick for the target are marked [SupportedOSPlatform(\"windows\")] or reference Windows-only assemblies (Windows Forms, WPF, System.Web, System.Drawing, directory services), call a library only Windows has by P/Invoke (user32, shell32, msdelta, ...; not kernel32, ntdll, advapi32, or ole32, which portable code guards), or declare a `[ComImport]` class; or the package has nothing in lib/ or ref/ and its runtime-specific code (`runtimes/<rid>/`) is for Windows only. The message names the package for `linux-x64` when the id ends in a Windows runtime identifier and the feed has one.",
        "A package that wraps Windows APIs, such as System.Drawing.Common on .NET 6 and later, or a native package such as LibSassHost.Native.win-x64.",
        "Fine if the application stays on Windows; otherwise choose a cross-platform alternative, or add the native package for the other operating systems, before containerizing.",
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

    public static readonly DiagnosticDescriptor OFR1007 = new(
        "OFR1007", Severity.Error,
        "only versions older than the one in use support the target",
        "The version in use does not support the target and no newer version does; only older versions do. Moving back to one is a downgrade, so `deps audit` does not propose it: the package is `replace` or `blocked`.",
        "A package that dropped its .NET Standard or modern .NET build in a later release.",
        "Replace the package with its successor (the message names one when the package map knows it), ask its authors for a modern build, or isolate the code that uses it behind a seam. Moving back to the older version is a decision to make with its release notes, not an upgrade.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1008 = new(
        "OFR1008", Severity.Info,
        "DLL references the audit does not see",
        "Projects reference DLLs by `HintPath` that no packages.config installs: checked-in or copied DLLs, which are dependencies too, but not packages, so `deps audit` has nothing to say about them. `deps resolve-dlls` matches them to packages and projects.",
        "A codebase from before NuGet, with third-party DLLs in a lib folder (NHibernate 4.1: 15 references, no package).",
        "Run `offramp deps resolve-dlls`, apply what it finds, and audit again.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1009 = new(
        "OFR1009", Severity.Warning,
        "package has nothing for any framework, and the package map replaces it",
        "The versions in use have no assemblies, no framework-specific assets, no dependency groups, and no native code (only build or tool files), so they \"support\" every target only because there is nothing to judge. The package map names what replaces the package, so its status is `replace`.",
        "A build-time helper for .NET Framework, such as Microsoft.Bcl.Build, whose targets fail under the .NET SDK's MSBuild.",
        "Remove the package, or move to what the message names.",
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

    public static readonly DiagnosticDescriptor OFR1204 = new(
        "OFR1204", Severity.Info,
        "packages.config project keeps its version",
        "A project on `packages.config` uses the package at another version than the one `deps consolidate` selects. Consolidation changes `PackageReference` versions only, so the project keeps its version.",
        "A legacy project not yet converted to `PackageReference`.",
        "Convert the project with `offramp csproj modernize`, then consolidate again; or update it with NuGet in Visual Studio.",
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
        "A `Reference` with a `HintPath` points at a DLL that a package ships (same assembly name and public key, at the referenced version or higher, for every target framework of the project). The message says what matched: the same file, the same file or informational version, the closest build (the assembly version only), or a newer version, which is an upgrade.",
        "A package's DLL copied into a lib folder by hand.",
        "Apply `deps resolve-dlls`, which swaps the reference for a `PackageReference`.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1403 = new(
        "OFR1403", Severity.Warning,
        "loose DLL unmatched",
        "No project builds the DLL and no package named like the assembly ships it. Its metadata (version, target framework, public key token) is attached for a person to decide. An unsigned DLL is matched only by its file (the same bytes, file version, or informational version), since anyone can publish an assembly of that name; the message names the package that has the name.",
        "A vendor or in-house DLL with no package, a package whose id differs from the assembly name, or an unsigned DLL built from source.",
        "Find the package or source it came from, or publish it to a private feed.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1404 = new(
        "OFR1404", Severity.Error,
        "loose Framework-only DLL with no replacement",
        "A DLL referenced by `HintPath` is built for .NET Framework (its `TargetFrameworkAttribute` says so, or, for a DLL without one, it references the .NET Framework's `mscorlib`), and no project or package replaces it, so the project cannot move to the target while it depends on it.",
        "A vendor library that never shipped for .NET Standard or modern .NET.",
        "Ask the vendor for a modern build, replace the library, or isolate its use behind a seam (`offramp seams`).",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1405 = new(
        "OFR1405", Severity.Warning,
        "loose DLL is a COM interop assembly",
        "A DLL referenced by `HintPath` was generated from a COM type library (it has `ImportedFromTypeLibAttribute`, which tlbimp writes). No package replaces it; COM interop works on modern .NET, but on Windows only.",
        "An interop assembly for a Windows component (Internet Explorer's SHDocVw, Office, a vendor's ActiveX control) checked in instead of generated by the build.",
        "Keep it and target net10.0-windows for the code that uses it, or reference the type library with a `COMReference` so the build generates the interop assembly; isolate COM use behind a seam if the code must run elsewhere.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1406 = new(
        "OFR1406", Severity.Warning,
        "loose DLL reference declared outside the project file",
        "The `Reference` comes from a file the project imports (a Directory.Build.props, a shared .props or .settings file), not from the project file, so `deps resolve-dlls` leaves it alone: editing the project would add a second reference and remove none. One diagnostic per assembly and declaring file, with the projects it reaches.",
        "A reference shared by every project, declared once, such as a `HintPath` into the NuGet global packages folder for legacy projects.",
        "Change the reference in the file the message names (for SDK-style projects, a `PackageReference` there), or leave it if it is how legacy projects get the package outside Windows.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1407 = new(
        "OFR1407", Severity.Warning,
        "package reference not added to a legacy project outside Windows",
        "Outside Windows the .NET SDK restores a legacy (non-SDK) project's `PackageReference` items but never gives their assemblies to the compiler: that is `ResolveNuGetPackageAssets`, in Visual Studio's `Microsoft.NuGet.targets`, which the SDK does not ship. So `deps resolve-dlls` leaves the project's `Reference` items as they are instead of breaking its build.",
        "Running `deps resolve-dlls` on Linux or macOS on a solution of legacy projects (NHibernate 4.1 had 2,505 errors after `--apply`).",
        "Convert the project with `offramp csproj modernize` first and run `deps resolve-dlls` again, or apply it on Windows, where Visual Studio's MSBuild resolves the package's assemblies.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1408 = new(
        "OFR1408", Severity.Error,
        "verification failed; resolve-dlls rolled back",
        "After `deps resolve-dlls --apply` replaced the references, the configured verification (a restore and build of the edited projects and their direct dependents, or `verify.command`) failed, and `verify.onFailure: rollback` restored every file from the journal.",
        "A package that restores but does not give the compiler what the DLL did (another assembly version, a missing framework), or a build that was already broken.",
        "Read the verification errors; fix them, or apply the references one project at a time (`--project`). `verify.onFailure: keep` leaves the change in place.",
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

    public static readonly DiagnosticDescriptor OFR1505 = new(
        "OFR1505", Severity.Warning,
        "deployed assembly older than a reference to it",
        "The application's packages deploy an assembly at a lower version than other deployed assemblies reference. A redirect would send those references down to a version that may lack what they call, so none is written.",
        "packages.config lists a package at a lower version than a package depending on it needs (installed with dependencies ignored), or a build step copies a newer DLL in from elsewhere.",
        "Update the package that ships the assembly to the version its dependents reference, then run `offramp redirects sync` again.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1506 = new(
        "OFR1506", Severity.Warning,
        "application skipped: partial model",
        "The application, or a project it references, is partial in the workspace model: its build failed during `scan`, so its references and packages are not all known. Redirects computed from that would be wrong, and `--prune` would remove live ones, so the application's configuration file is left alone.",
        "A build that fails outside Windows (letter case, Windows-only steps), or a missing package.",
        "Fix the build errors `scan` reported (`OFR0130` and the step diagnostics), run `offramp scan` again, then `offramp redirects sync`.",
        DependenciesArea);

    public static readonly DiagnosticDescriptor OFR1507 = new(
        "OFR1507", Severity.Info,
        "hosted project's configuration left alone",
        "The project is hosted by another web project (it builds into the host's folder and is loaded by the host's application), so the runtime reads the host's `web.config`, never the project's. `redirects sync` leaves the project's configuration file alone and computes the host's redirects with the hosted project's packages.",
        "A plugin, module, or area project with a `web.config` of its own, as Visual Studio's templates create.",
        "Nothing to do; run `redirects sync` for the host. The hosted project's binding redirects can be deleted by hand.",
        DependenciesArea);
}
