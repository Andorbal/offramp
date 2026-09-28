using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

public sealed class WindowsOnlyBuildStepsTests
{
    [Fact]
    public void A_plain_project_has_no_windows_only_steps()
    {
        var evaluation = Evaluation(
            properties: new() { ["GenerateSerializationAssemblies"] = "Auto", ["PostBuildEvent"] = "echo built" });

        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [evaluation]));
    }

    [Fact]
    public void Sgen_auto_counts_only_when_the_target_ran()
    {
        var ran = Evaluation(properties: new() { ["GenerateSerializationAssemblies"] = "Auto" }, targets: ["GenerateSerializationAssemblies"]);

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [ran]));
        Assert.Equal("sgen", step.Id);
        Assert.Equal("OFR0110", step.Descriptor.Code);
    }

    [Fact]
    public void Steps_come_from_imports_and_are_reported_in_order_once()
    {
        var net48 = Evaluation(
            properties: new() { ["PreBuildEvent"] = "xcopy /y a b\r\necho second line" },
            items: new() { ["COMFileReference"] = [new EvaluatedItem("typelib.tlb", new Dictionary<string, string>())] },
            imports: ["C:/VS/MSBuild/Microsoft/VisualStudio/v17.0/TextTemplating/Microsoft.TextTemplating.targets", "C:/VS/SSDT/Microsoft.Data.Tools.Schema.SqlTasks.targets"]);
        var again = net48 with { };

        var steps = WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [net48, again]);

        Assert.Equal(["com", "t4", "ssdt", "build-event"], steps.Select(s => s.Id));
        Assert.Equal("imports Microsoft.TextTemplating.targets", steps[1].Evidence);
        Assert.Equal("PreBuildEvent: xcopy /y a b", steps[3].Evidence);
    }

    [Fact]
    public void The_web_targets_count_when_Visual_Studio_provides_them_and_not_when_the_package_does()
    {
        var visualStudio = Evaluation(imports: ["C:/VS/MSBuild/Microsoft/VisualStudio/v17.0/WebApplications/Microsoft.WebApplication.targets"]);
        var package = Evaluation(imports: ["/home/me/.nuget/packages/msbuild.microsoft.visualstudio.web.targets/14.0.0.3/build/../tools/VSToolsPath/WebApplications/Microsoft.WebApplication.targets"]);

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [visualStudio]));
        Assert.Equal("web-targets", step.Id);
        Assert.Equal("OFR0116", step.Descriptor.Code);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [package]));
    }

    [Fact]
    public void Precompiled_views_need_the_aspnet_compiler()
    {
        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [Evaluation(properties: new() { ["MvcBuildViews"] = "true" })]));

        Assert.Equal("aspnet-compiler", step.Id);
        Assert.Equal("OFR0116", step.Descriptor.Code);
        Assert.Empty(WindowsOnlyBuildSteps.Detect("src/Web/Web.csproj", [Evaluation(properties: new() { ["MvcBuildViews"] = "false" })]));
    }

    [Fact]
    public void A_missing_web_targets_import_is_the_step_even_without_an_evaluation()
    {
        const string message = "The imported project \"/usr/local/share/dotnet/sdk/10.0.100/Microsoft/VisualStudio/v17.0/WebApplications/Microsoft.WebApplication.targets\" was not found.";
        BuildError[] errors =
        [
            new("MSB4019", "The imported project \"/x/Other.targets\" was not found.", "/repo/src/A/A.csproj", null, null, null),
            new("MSB4019", message, "/repo/src/A/A.csproj", null, null, null),
        ];

        var step = Assert.Single(WindowsOnlyBuildSteps.Detect("src/A/A.csproj", [], errors));

        Assert.Equal("web-targets", step.Id);
        Assert.Null(WindowsOnlyBuildSteps.FromEvaluationError("MSB4019", "The imported project \"/x/Other.targets\" was not found."));
        Assert.Null(WindowsOnlyBuildSteps.FromEvaluationError("CS0246", message));
    }

    private static EvaluatedProject Evaluation(
        Dictionary<string, string>? properties = null,
        Dictionary<string, IReadOnlyList<EvaluatedItem>>? items = null,
        string[]? imports = null,
        string[]? targets = null) => new()
        {
            ProjectFile = "/repo/src/A/A.csproj",
            TargetFramework = "net48",
            Properties = properties ?? [],
            Items = items ?? [],
            Imports = imports ?? [],
            TargetsExecuted = new HashSet<string>(targets ?? [], StringComparer.OrdinalIgnoreCase),
        };
}
