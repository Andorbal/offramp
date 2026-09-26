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
}
