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
