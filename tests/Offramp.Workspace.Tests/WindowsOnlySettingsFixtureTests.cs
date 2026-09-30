using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Tests;

/// <summary>
/// The windows-only-settings fixture: Windows-only settings in the project files themselves, which win over the
/// compile-only block. The proof that matters is a plain <c>dotnet build</c> of the whole solution, with no Offramp
/// properties and no scan, as anyone who clones the repository would run it
/// (docs/decisions/0063-condition-windows-only-settings-in-project-files.md).
/// </summary>
public sealed class WindowsOnlySettingsFixtureTests
{
    private const string Solution = "WindowsSettings.sln";

    [Fact]
    public async Task The_block_alone_does_not_turn_off_what_a_project_file_sets()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The compile-only block applies outside Windows only.");
        using var repository = await FixtureRepository.CreateAsync("windows-only-settings");

        var fix = DoctorRunner.ApplyFix(repository.Path);
        var build = await BuildAsync(repository.Path, "Release");

        Assert.True(fix.Applied);
        Assert.Empty(fix.ProjectFiles);
        Assert.False(build.Succeeded, "A plain build passed with the settings unconditioned; this test would prove nothing.");
        Assert.Contains("MSB3474", build.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_doctor_fix_a_plain_build_of_the_whole_solution_succeeds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The compile-only block applies outside Windows only.");
        using var repository = await FixtureRepository.CreateAsync("windows-only-settings");
        var projects = (await SolutionReader.ReadAsync(Path.Combine(repository.Path, Solution), CancellationToken.None)).ProjectPaths
            .Select(p => RepoPaths.ToRepositoryRelative(repository.Path, p));

        var fix = DoctorRunner.ApplyFix(repository.Path, DoctorRunner.GuardFiles(repository.Path, null, projects));

        Assert.Equal(["src/Contracts/Contracts.csproj", "src/Legacy/Legacy.csproj", "src/Site/Site.csproj"], fix.ProjectFiles.Select(f => f.File));
        Assert.All(fix.ProjectFiles, f => Assert.True(f.Applied));
        Assert.Equal(
            ["GenerateSerializationAssemblies", "Exec in target PostBuild", "GenerateSerializationAssemblies", "PostBuildEvent", "MvcBuildViews", "PostBuildEvent"],
            fix.ProjectFiles.SelectMany(f => f.Guards).Select(g => g.Setting));
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var build = await BuildAsync(repository.Path, configuration);
            Assert.True(build.Succeeded, $"dotnet build -c {configuration}:\n{build.StandardOutput}{build.StandardError}");
        }

        // The build events were skipped, not run through /bin/sh.
        Assert.False(Directory.Exists(Path.Combine(repository.Path, "drop")));

        // Only the conditions changed: git sees one changed line per setting.
        var diff = await repository.GitAsync("diff", "--numstat", "--", "src");
        Assert.Equal(["2\t2\tsrc/Contracts/Contracts.csproj", "2\t2\tsrc/Legacy/Legacy.csproj", "2\t2\tsrc/Site/Site.csproj"], diff.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Task<ProcessResult> BuildAsync(string root, string configuration) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", Solution, "-c", configuration, "-nologo", "-v:minimal", "-nodeReuse:false"])
        {
            WorkingDirectory = root,
            Timeout = TimeSpan.FromMinutes(10),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        });
}
