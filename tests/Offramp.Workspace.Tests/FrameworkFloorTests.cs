using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Scanning;
using static Offramp.Workspace.Tests.GraphBuilderTests;

namespace Offramp.Workspace.Tests;

/// <summary>docs/spec/02-workspace-model.md#the-net-framework-floor.</summary>
public sealed class FrameworkFloorTests
{
    [Theory]
    [InlineData("net45", "net45")]
    [InlineData("net461", "net461")]
    [InlineData("net47", "net47")]
    [InlineData("net472", null)]
    [InlineData("net48", null)]
    [InlineData("netstandard2.0", null)]
    [InlineData("net10.0", null)]
    public void Only_net_framework_targets_older_than_472_are_below_the_floor(string tfm, string? below)
    {
        Assert.Equal(below, FrameworkFloor.Below(Project("src/A/A.csproj") with { TargetFrameworks = [tfm] }));
    }

    [Fact]
    [ProducesDiagnostic("OFR0106")]
    public void A_project_below_the_floor_is_reported_once_with_its_lowest_target()
    {
        var diagnostics = new DiagnosticBag();
        var project = Project("src/Old/Old.csproj") with { TargetFrameworks = ["net40", "net461", "net10.0"] };

        var reported = FrameworkFloor.Check(project, diagnostics);

        Assert.NotNull(reported);
        Assert.Equal("OFR0106", reported.Code);
        Assert.Equal("src/Old/Old.csproj", reported.Project);
        Assert.Equal("net40", reported.Data["targetFramework"]!.GetValue<string>());
        Assert.Equal("Targets net40, below net472: raise it before porting or referencing portable projects.", reported.Message);
        Assert.Null(FrameworkFloor.Check(Project("src/New/New.csproj") with { TargetFrameworks = ["net48", "net10.0"] }, diagnostics));
        Assert.Single(diagnostics.ToSortedList());
    }
}
