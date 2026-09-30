using Offramp.Core.Model;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Tests;

/// <summary>
/// The systemweb fixture: an MSBuild.SDK.SystemWeb project, whose SDK imports the Visual Studio web
/// targets unconditionally. <c>dotnet build</c> cannot evaluate it until the compile-only block supplies them.
/// </summary>
public sealed class SystemWebFixtureTests
{
    private const string Site = "src/Site/Site.csproj";

    [Fact]
    [ProducesDiagnostic("OFR0116")]
    public async Task The_missing_web_targets_are_reported_as_a_windows_only_step()
    {
        var scanned = await ScannedFixtures.ScanAsync("systemweb");
        using var _ = scanned.Repository;

        var diagnostic = Assert.Single(scanned.Diagnostics.ToSortedList(), d => d.Code == "OFR0116");
        Assert.Equal(Site, diagnostic.Project);
        Assert.Equal("web-targets", diagnostic.Data["step"]!.ToString());
        Assert.True(WindowsOnlyBuildSteps.AnyIn(scanned.Outcome.Model!));
        Assert.False(scanned.Outcome.Result!.BuildSucceeded);
    }

    /// <summary>
    /// With <c>dotnet build</c> on every OS: on Windows its <c>VSToolsPath</c> points into the SDK as well, so the block's
    /// dotnet build section supplies the web targets there (ADR 0064); Visual Studio's build would use its own.
    /// </summary>
    [Fact]
    public async Task The_compile_only_block_makes_the_web_project_build_with_dotnet_build()
    {

        var scanned = await ScannedFixtures.ScanAsync("systemweb", (root, request) =>
        {
            Assert.True(DoctorRunner.ApplyFix(root).Applied);
            return request;
        });
        using var _ = scanned.Repository;

        var model = scanned.Outcome.Model!;
        var site = model.Projects.Single(p => p.Id == Site);
        Assert.True(scanned.Outcome.Result!.BuildSucceeded, string.Join("\n", scanned.Diagnostics.ToSortedList().Select(d => d.Message)));
        Assert.DoesNotContain(scanned.Diagnostics.ToSortedList(), d => WindowsOnlyBuildSteps.IsStepCode(d.Code));
        Assert.Equal(ProjectKind.Web, site.Kind);
        Assert.False(site.Partial);
        Assert.Empty(site.WindowsOnlyBuildSteps);
        Assert.DoesNotContain(site.PackageReferences, p => p.Id == WindowsOnlyBuildSteps.WebTargetsPackage);
    }
}
