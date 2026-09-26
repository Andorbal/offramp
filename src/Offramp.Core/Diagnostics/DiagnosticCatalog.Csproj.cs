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
}
