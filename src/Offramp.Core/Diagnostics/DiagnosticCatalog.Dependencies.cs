namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string DependenciesArea = "deps";

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
