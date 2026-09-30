using Offramp.Fixtures;
using Offramp.Workspace.Doctor;

namespace Offramp.Workspace.Tests;

/// <summary>
/// Legacy (non-SDK) projects on macOS and Linux with nothing but the compile-only block and the
/// restore <c>scan</c> runs (docs/decisions/0037-legacy-projects-outside-windows.md). The fixtures'
/// own Directory.Build.props, which gives legacy projects the reference assemblies on every OS, is
/// replaced by an empty one first, and the packages folder the fixture helper fills is removed.
/// </summary>
public sealed class LegacyOutsideWindowsTests
{
    [Fact]
    public async Task A_packages_config_solution_builds_with_the_block_and_the_restore_scan_runs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The compile-only block applies outside Windows only.");

        var scanned = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            OnlyTheBlock(root);
            Directory.Delete(Path.Combine(root, "packages"), recursive: true);
            return request;
        });
        using var _ = scanned.Repository;

        var diagnostics = scanned.Diagnostics.ToSortedList();
        Assert.True(scanned.Outcome.Result!.BuildSucceeded, string.Join("\n", diagnostics.Select(d => d.Message)));
        Assert.All(scanned.Outcome.Model!.Projects, p => Assert.False(p.Partial, p.Id));
        var restored = Assert.Single(diagnostics, d => d.Code == "OFR0106");
        Assert.Equal("[\"Newtonsoft.Json.13.0.3\"]", restored.Data["packages"]!.ToJsonString());
        Assert.DoesNotContain(diagnostics, d => d.Code == "OFR0122");
    }

    [Fact]
    public async Task A_shared_settings_file_that_moves_the_msbuild_extensions_path_keeps_the_block_out_and_is_named()
    {
        // Open Live Writer: writer.build.settings sets MSBuildExtensionsPath, so no project imported
        // Directory.Build.props, 25 projects failed with MSB3644, and nothing said why.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The compile-only block applies outside Windows only.");

        var scanned = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            OnlyTheBlock(root);
            File.WriteAllText(Path.Combine(root, "src", "build.settings"),
                "<Project>\n  <PropertyGroup>\n    <MSBuildExtensionsPath>$(MSBuildToolsPath)\\MsBuildExtensions</MSBuildExtensionsPath>\n  </PropertyGroup>\n</Project>\n");
            var project = Path.Combine(root, "src", "Billing", "Billing.csproj");
            var original = File.ReadAllText(project);
            File.WriteAllText(project, original.Replace("  <Import Project=\"$(MSBuildExtensionsPath)", "  <Import Project=\"..\\build.settings\" />\n  <Import Project=\"$(MSBuildExtensionsPath)", StringComparison.Ordinal));
            Assert.NotEqual(original, File.ReadAllText(project));
            return request;
        });
        using var _ = scanned.Repository;

        var gap = Assert.Single(scanned.Diagnostics.ToSortedList(), d => d.Code == "OFR0122");
        Assert.Equal("src/Billing/Billing.csproj", gap.Project);
        Assert.Equal("src/build.settings", gap.File);
        Assert.Equal("\"msbuild-extensions-path\"", gap.Data["cause"]!.ToJsonString());
        Assert.False(scanned.Outcome.Result!.BuildSucceeded);
    }

    [Fact]
    public async Task A_legacy_visual_basic_project_gets_its_runtime_from_the_reference_assemblies()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The compile-only block applies outside Windows only.");

        var scanned = await ScannedFixtures.ScanAsync("webforms", (root, request) =>
        {
            OnlyTheBlock(root);
            var vbproj = Path.Combine(root, "src", "Portal.Utilities", "Portal.Utilities.vbproj");
            File.WriteAllText(vbproj, File.ReadAllText(vbproj).Replace("    <VBRuntime>Embed</VBRuntime>\n", "", StringComparison.Ordinal));
            Assert.DoesNotContain("VBRuntime", File.ReadAllText(vbproj), StringComparison.Ordinal);
            return request;
        });
        using var _ = scanned.Repository;

        var diagnostics = scanned.Diagnostics.ToSortedList();
        Assert.True(scanned.Outcome.Result!.BuildSucceeded, string.Join("\n", diagnostics.Select(d => d.Message)));
        Assert.False(scanned.Outcome.Model!.Projects.Single(p => p.Id == "src/Portal.Utilities/Portal.Utilities.vbproj").Partial);
    }

    [Fact]
    public async Task Without_the_legacy_section_the_same_solution_does_not_build()
    {
        // The check above can fail: with only the first two sections, legacy projects find no reference assemblies.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has the .NET Framework targeting packs.");

        var scanned = await ScannedFixtures.ScanAsync("legacy-csproj", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, CompileOnlyConditional.FileName),
                "<Project>\n" + string.Concat(CompileOnlyConditional.CompileOnlyLines.Concat(CompileOnlyConditional.WebTargetsLines).Select(l => "  " + l + "\n")) + "</Project>\n");
            return request;
        });
        using var _ = scanned.Repository;

        Assert.False(scanned.Outcome.Result!.BuildSucceeded);
    }

    private static void OnlyTheBlock(string root)
    {
        File.WriteAllText(Path.Combine(root, CompileOnlyConditional.FileName), "<Project>\n</Project>\n");
        Assert.True(DoctorRunner.ApplyFix(root).Applied);
    }
}
