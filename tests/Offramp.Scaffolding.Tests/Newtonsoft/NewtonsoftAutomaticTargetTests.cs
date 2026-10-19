using NuGet.Versioning;
using Offramp.Analysis.Newtonsoft;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Scaffolding.Newtonsoft;

namespace Offramp.Scaffolding.Tests.Newtonsoft;

public sealed class NewtonsoftAutomaticTargetTests
{
    [Fact]
    [ProducesDiagnostic("OFR3724")]
    public void Mixed_releases_cap_the_target_and_leave_supported_projects_unchanged()
    {
        var model = Model("7.0.1", "13.0.3", "13.0.4");
        var diagnostics = new DiagnosticBag();
        var target = NewtonsoftUpgrader.SelectAutomaticTarget(model, NuGetVersion.Parse("13.0.4"), "13.0.1", exactVersion: false, diagnostics);
        var releases = model.Projects.Select(p => NewtonsoftReferences.For(p, "net8.0")!).ToList();

        Assert.Equal(NuGetVersion.Parse("13.0.3"), target);
        Assert.False(NewtonsoftUpgrader.ShouldLeaveUnchanged(releases[0], target, "13.0.1", exactVersion: false));
        Assert.True(NewtonsoftUpgrader.ShouldLeaveUnchanged(releases[1], target, "13.0.1", exactVersion: false));
        Assert.True(NewtonsoftUpgrader.ShouldLeaveUnchanged(releases[2], target, "13.0.1", exactVersion: false));
        Assert.Contains(diagnostics.ToSortedList(), d => d.Code == "OFR3724" && d.Message.Contains("13.0.3", StringComparison.Ordinal));
    }

    [Fact]
    public void All_old_releases_keep_the_newest_target()
    {
        var model = Model("7.0.1", "12.0.3");
        var target = NewtonsoftUpgrader.SelectAutomaticTarget(model, NuGetVersion.Parse("13.0.4"), "13.0.1", exactVersion: false, new DiagnosticBag());

        Assert.Equal(NuGetVersion.Parse("13.0.4"), target);
        Assert.All(model.Projects, project => Assert.False(NewtonsoftUpgrader.ShouldLeaveUnchanged(
            NewtonsoftReferences.For(project, "net8.0")!, target, "13.0.1", exactVersion: false)));
    }

    [Fact]
    public void Higher_than_floor_release_also_limits_the_target_and_is_left_unchanged()
    {
        var model = Model("7.0.1", "13.0.4", "13.0.5");
        var target = NewtonsoftUpgrader.SelectAutomaticTarget(model, NuGetVersion.Parse("13.0.6"), "13.0.1", exactVersion: false, new DiagnosticBag());

        Assert.Equal(NuGetVersion.Parse("13.0.4"), target);
        Assert.False(NewtonsoftUpgrader.ShouldLeaveUnchanged(NewtonsoftReferences.For(model.Projects[0], "net8.0")!, target, "13.0.1", exactVersion: false));
        Assert.True(NewtonsoftUpgrader.ShouldLeaveUnchanged(NewtonsoftReferences.For(model.Projects[1], "net8.0")!, target, "13.0.1", exactVersion: false));
        Assert.True(NewtonsoftUpgrader.ShouldLeaveUnchanged(NewtonsoftReferences.For(model.Projects[2], "net8.0")!, target, "13.0.1", exactVersion: false));
    }

    [Fact]
    public void Explicit_version_keeps_the_requested_target_and_rewrites_exactly()
    {
        var model = Model("7.0.1", "13.0.3");
        var requested = NuGetVersion.Parse("13.0.4");
        var target = NewtonsoftUpgrader.SelectAutomaticTarget(model, requested, "13.0.1", exactVersion: true, new DiagnosticBag());

        Assert.Equal(requested, target);
        Assert.False(NewtonsoftUpgrader.ShouldLeaveUnchanged(NewtonsoftReferences.For(model.Projects[0], "net8.0")!, target, "13.0.1", exactVersion: true));
        Assert.False(NewtonsoftUpgrader.ShouldLeaveUnchanged(NewtonsoftReferences.For(model.Projects[1], "net8.0")!, target, "13.0.1", exactVersion: true));
    }

    private static WorkspaceModel Model(params string[] versions)
    {
        var projects = versions.Select((version, index) =>
        {
            var id = $"src/Project{index}.csproj";
            return new ProjectInfo
            {
                Id = id,
                Name = $"Project{index}",
                TargetFrameworks = ["net8.0"],
                CompilerCalls = new SortedDictionary<string, CompilerCallRef>(StringComparer.Ordinal)
                {
                    ["net8.0"] = new CompilerCallRef("workspace.complog", id, "net8.0"),
                },
                PackageReferences = [new PackageReferenceInfo { Id = "Newtonsoft.Json", Version = version }],
            };
        }).ToList();

        return new WorkspaceModel
        {
            CreatedAt = "",
            RepositoryRoot = "",
            Source = new WorkspaceSource(WorkspaceSourceKind.Complog, "workspace.complog", null),
            Sdk = new SdkInfo("", ""),
            Projects = projects,
        };
    }
}
