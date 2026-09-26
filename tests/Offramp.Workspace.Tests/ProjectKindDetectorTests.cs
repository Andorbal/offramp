using Offramp.Core.Model;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

public sealed class ProjectKindDetectorTests
{
    public static TheoryData<string, ProjectFacts, ProjectKind, string> Cases => new()
    {
        { "is-test-project", new ProjectFacts { IsTestProject = true, OutputType = "Exe" }, ProjectKind.Test, "IsTestProject=true" },
        { "test-package", Facts(packages: ["xunit"]), ProjectKind.Test, "PackageReference xunit" },
        { "test-guid", new ProjectFacts { ProjectTypeGuids = "{3AC096D0-A1C2-E12C-1390-A8335801FDAB};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}" }, ProjectKind.Test, "ProjectTypeGuids {3AC096D0-A1C2-E12C-1390-A8335801FDAB}" },
        { "test-beats-web", new ProjectFacts { Sdk = "Microsoft.NET.Sdk.Web", PackageIds = Set("NUnit") }, ProjectKind.Test, "PackageReference NUnit" },
        { "web-sdk", new ProjectFacts { Sdk = "Microsoft.NET.Sdk.Web", OutputType = "Exe" }, ProjectKind.Web, "Sdk=Microsoft.NET.Sdk.Web" },
        { "web-guid", new ProjectFacts { ProjectTypeGuids = "{349c5851-65df-11da-9384-00065b846f21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}" }, ProjectKind.Web, "ProjectTypeGuids {349C5851-65DF-11DA-9384-00065B846F21}" },
        { "web-config", new ProjectFacts { AssemblyReferences = Set("System.Web"), HasWebConfig = true }, ProjectKind.Web, "Reference System.Web + OutputType=Library + web.config" },
        { "system-web-library-is-not-web", new ProjectFacts { AssemblyReferences = Set("System.Web") }, ProjectKind.Library, "OutputType=Library" },
        { "use-winforms", new ProjectFacts { UseWindowsForms = true, OutputType = "WinExe" }, ProjectKind.Winforms, "UseWindowsForms=true" },
        { "winforms-reference", new ProjectFacts { AssemblyReferences = Set("System.Windows.Forms"), OutputType = "WinExe" }, ProjectKind.Winforms, "Reference System.Windows.Forms + OutputType=WinExe" },
        { "winforms-library-is-library", new ProjectFacts { AssemblyReferences = Set("System.Windows.Forms") }, ProjectKind.Library, "OutputType=Library" },
        { "use-wpf", new ProjectFacts { UseWpf = true, OutputType = "WinExe" }, ProjectKind.Wpf, "UseWPF=true" },
        { "wpf-guid", new ProjectFacts { ProjectTypeGuids = "{60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}", OutputType = "WinExe" }, ProjectKind.Wpf, "ProjectTypeGuids {60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}" },
        { "service-reference", new ProjectFacts { AssemblyReferences = Set("System.ServiceProcess"), OutputType = "Exe" }, ProjectKind.Service, "Reference System.ServiceProcess + OutputType=Exe" },
        { "topshelf", Facts(packages: ["Topshelf"], outputType: "Exe"), ProjectKind.Service, "PackageReference Topshelf" },
        { "worker-sdk", new ProjectFacts { Sdk = "Microsoft.NET.Sdk.Worker", OutputType = "Exe" }, ProjectKind.Service, "Sdk=Microsoft.NET.Sdk.Worker" },
        { "console", new ProjectFacts { OutputType = "Exe" }, ProjectKind.Console, "OutputType=Exe" },
        { "library-default", new ProjectFacts(), ProjectKind.Library, "OutputType=Library" },
        { "module-is-unknown", new ProjectFacts { OutputType = "Module" }, ProjectKind.Unknown, "OutputType=Module" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Kind_follows_the_spec_order(string name, ProjectFacts facts, ProjectKind kind, string evidence)
    {
        _ = name;

        var detected = ProjectKindDetector.Detect(facts);

        Assert.Equal(kind, detected.Kind);
        Assert.Equal(evidence, detected.Evidence);
    }

    [Fact]
    public void Sdk_name_comes_from_the_most_specific_sdk_property()
    {
        Assert.Equal("Microsoft.NET.Sdk.Web", ProjectKindDetector.SdkName(p => p is "UsingMicrosoftNETSdkWeb" or "UsingMicrosoftNETSdk"));
        Assert.Equal("Microsoft.NET.Sdk", ProjectKindDetector.SdkName(p => p == "UsingMicrosoftNETSdk"));
        Assert.Null(ProjectKindDetector.SdkName(_ => false));
    }

    private static ProjectFacts Facts(string[] packages, string? outputType = null) =>
        new() { PackageIds = Set(packages), OutputType = outputType };

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}
