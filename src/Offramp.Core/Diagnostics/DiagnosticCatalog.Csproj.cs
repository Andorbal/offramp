namespace Offramp.Core.Diagnostics;

public static partial class DiagnosticCatalog
{
    private const string CsprojArea = "csproj";

    public static readonly DiagnosticDescriptor OFR4301 = new(
        "OFR4301", Severity.Warning,
        "compile items kept explicit",
        "The project's Compile items are not the files the SDK's `**/*.cs` glob would give (a file on disk the project leaves out, or one outside the glob), so the converted project keeps the list and sets EnableDefaultCompileItems to false.",
        "Excluded or abandoned source files left in the folder, files included from elsewhere without a Link.",
        "Delete or move the files the glob would add, then run the conversion again to get a globbed project; or keep the explicit list.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4302 = new(
        "OFR4302", Severity.Info,
        "build step converted for review",
        "A PreBuildEvent, PostBuildEvent, BeforeBuild, or AfterBuild became a target hooked to the same point in the build. The SDK's output layout (bin/<configuration>/<framework>/) can change what relative paths in the command mean.",
        "Copy steps, signing, and code generation in legacy projects.",
        "Read the target and check the paths it uses; better, replace it with MSBuild items or tasks.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4303 = new(
        "OFR4303", Severity.Error,
        "converted project compiles different inputs",
        "The converted project was built in a scratch copy, and its compiler inputs (source files, references, embedded resources) differ from the original build's, or it did not build. `--apply` is refused unless `--accept-diff`.",
        "A glob that picks up a file the project left out, a package whose assemblies differ from the HintPath ones, a resource with a different manifest name.",
        "Read the differences in the result's `verification`; fix the project or the files, or accept them with --accept-diff.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4304 = new(
        "OFR4304", Severity.Warning,
        "project not converted",
        "The project is not converted to SDK style: an ASP.NET web application project (the SDK has no System.Web project support), or a project that is not C#.",
        "ASP.NET MVC and Web Forms applications.",
        "Keep the project as it is and move its routes to ASP.NET Core with `offramp web scaffold`.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4305 = new(
        "OFR4305", Severity.Warning,
        "converted project fails NuGet audit",
        "Restoring the PackageReference way turns NuGet audit on, and the project treats warnings as errors, so its build stops at packages with known vulnerabilities (NU1901–NU1904). The conversion is verified with audit off; the message lists the packages.",
        "Old package versions in a `packages.config` project with `TreatWarningsAsErrors`.",
        "Upgrade the packages (`offramp deps audit` lists them and their replacements), or keep the findings as warnings with `<WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>` until you do.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4306 = new(
        "OFR4306", Severity.Info,
        "shared or generated assembly info file left as is",
        "A file that declares assembly attributes the SDK generates is outside the project's folder, compiled by other projects too, ignored by git, added to the compilation by a build target, or generated code. `csproj modernize` and `codemod run --mod assemblyinfo` do not edit it: its attributes stay, and the project sets the matching `GenerateAssembly<Name>Attribute` properties to false so the SDK does not generate them again. The message and `data` say why the file counts as shared or generated.",
        "A linked SharedAssemblyInfo.cs, GlobalAssemblyInfo.cs, or VersionInfo.cs that versions a whole solution, often written by the build (NAnt, Cake, GitVersion).",
        "Nothing, if the file should keep versioning every project that compiles it. To move the values into project properties, do it once every project that compiles the file is SDK-style: set the properties (in Directory.Build.props for all of them), remove the file, and drop the GenerateAssembly<Name>Attribute properties.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4307 = new(
        "OFR4307", Severity.Warning,
        "package version raised to the one a referenced project brings",
        "The project's packages.config asks for a lower version of a package than a project it references (directly or through others) passes on once it restores the PackageReference way. PackageReference would bring the higher version in, and the lower direct one would be a package downgrade (NU1605, an error by default), so the converted project asks for the higher version. The message names the project it comes from.",
        "Projects that each installed their own version of a common package (Newtonsoft.Json, log4net) with packages.config, which never passed packages on.",
        "Check that the project works with the newer version (the verification build compiles it); better, give the whole solution one version with `offramp deps consolidate`.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4308 = new(
        "OFR4308", Severity.Warning,
        "build customization the SDK overrides",
        "A target in the project body has the name of a target the common targets define (other than BeforeBuild and AfterBuild, which the conversion renames and hooks), or a file the project imports sets properties the SDK owns (TargetFrameworkVersion, OutputPath, IntermediateOutputPath, MSBuildExtensionsPath, and the like) without a condition. In an SDK-style project the SDK's targets come after the project body and win, and the SDK sets those properties itself, so the customization stops working or fights the SDK.",
        "An empty `_CopyFilesMarkedCopyLocal` target that turned copy-local off, an `AfterCompile` step, a shared settings file that every legacy project imports.",
        "Hook a target of another name to the one it replaced (BeforeTargets or AfterTargets); condition the imported properties on '$(UsingMicrosoftNETSdk)' != 'true', or remove them.",
        CsprojArea);

    public static readonly DiagnosticDescriptor OFR4309 = new(
        "OFR4309", Severity.Info,
        "verification used files HEAD does not have",
        "Verification builds in a scratch copy of HEAD with the working tree's changes over it, and the files the scan's build read that HEAD does not have (untracked or ignored by git) are among them: a link or copy that fixes a path's letter case, a file a build step generates, an untracked Directory.Build.props. The conversion is verified for this working tree; a clean checkout lacks the files. The message and `data.files` name them; files of restored packages are only counted (`data.packageFiles`).",
        "Fixes a diagnostic prescribed (OFR0117's link for a path in the wrong letter case, OFR0115's pre-generated output) that are not committed yet, files a build step writes and git ignores.",
        "Nothing, if the files are meant to stay local. Otherwise commit them, or have the build create them, so a clean checkout builds and verifies the same way.",
        CsprojArea);
}
