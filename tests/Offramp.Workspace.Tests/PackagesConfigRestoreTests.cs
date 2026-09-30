using System.Text;
using System.Xml.Linq;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Workspace.Doctor;
using PackagesConfigRestore = Offramp.Workspace.Doctor.PackagesConfigRestore;

namespace Offramp.Workspace.Tests;

/// <summary>
/// <see cref="PackagesConfigRestore"/>: the files that let <c>dotnet restore</c> restore <c>packages.config</c>, so that a
/// fresh clone of a legacy solution builds with a plain <c>dotnet build</c>, without Offramp
/// (docs/decisions/0064-dotnet-build-and-packages-config-without-offramp.md).
/// </summary>
public sealed class PackagesConfigRestoreTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("packages-config-restore");

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Doctor_fix_writes_the_targets_file_and_the_solution_import_once()
    {
        var plan = PackagesConfigRestore.Plan(_repo.Path);

        Assert.Equal([PackagesConfigRestore.TargetsFileName, PackagesConfigRestore.SolutionTargetsFileName], plan.Select(f => f.File));
        Assert.All(plan, f => Assert.StartsWith("--- /dev/null", f.Diff, StringComparison.Ordinal));
        Assert.False(File.Exists(_repo.Combine(PackagesConfigRestore.TargetsFileName)));

        var applied = PackagesConfigRestore.Apply(_repo.Path);

        Assert.All(applied, f => Assert.True(f.Applied));
        Assert.Equal(PackagesConfigRestore.TargetsContent, _repo.Read(PackagesConfigRestore.TargetsFileName));
        var solution = XDocument.Parse(_repo.Read(PackagesConfigRestore.SolutionTargetsFileName));
        Assert.Equal("$(MSBuildThisFileDirectory)" + PackagesConfigRestore.TargetsFileName, (string?)solution.Root!.Element("Import")!.Attribute("Project"));
        Assert.True(PackagesConfigRestore.IsInPlace(_repo.Path));
        Assert.Empty(PackagesConfigRestore.Plan(_repo.Path));
        XDocument.Parse(PackagesConfigRestore.TargetsContent);
    }

    /// <summary>
    /// MSBuild expands <c>$(...)</c>, <c>@(...)</c>, and <c>%(...)</c> in an inline task's code as it reads the file: on
    /// Windows a path with backslashes broke the compile (CS1009), elsewhere the code silently compared the wrong text.
    /// </summary>
    [Fact]
    public void The_inline_task_code_spells_nothing_msbuild_would_expand()
    {
        var content = PackagesConfigRestore.TargetsContent;
        var start = content.IndexOf("<![CDATA[", StringComparison.Ordinal);
        var code = content[start..content.IndexOf("]]>", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("$(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("@(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("%(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_solution_file_the_repository_has_gains_only_the_import()
    {
        const string existing = "<Project>\r\n  <PropertyGroup>\r\n    <Answer>42</Answer>\r\n  </PropertyGroup>\r\n</Project>\r\n";
        _repo.WriteBytes(PackagesConfigRestore.SolutionTargetsFileName, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(existing)]);

        PackagesConfigRestore.Apply(_repo.Path);

        var bytes = File.ReadAllBytes(_repo.Combine(PackagesConfigRestore.SolutionTargetsFileName));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.StartsWith("<Project>\r\n  <PropertyGroup>\r\n    <Answer>42</Answer>\r\n  </PropertyGroup>\r\n  <!-- packages.config for dotnet restore", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("</Project>\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_checkout_with_crlf_line_endings_is_current_and_an_older_file_is_replaced()
    {
        PackagesConfigRestore.Apply(_repo.Path);
        File.WriteAllText(_repo.Combine(PackagesConfigRestore.TargetsFileName), PackagesConfigRestore.TargetsContent.ReplaceLineEndings("\r\n"));

        Assert.True(PackagesConfigRestore.IsInPlace(_repo.Path));

        File.WriteAllText(_repo.Combine(PackagesConfigRestore.TargetsFileName), "<Project>\r\n  <!-- an older Offramp -->\r\n</Project>\r\n");
        var replaced = Assert.Single(PackagesConfigRestore.Apply(_repo.Path));

        Assert.Equal(PackagesConfigRestore.TargetsFileName, replaced.File);
        Assert.Equal(PackagesConfigRestore.TargetsContent.ReplaceLineEndings("\r\n"), File.ReadAllText(_repo.Combine(PackagesConfigRestore.TargetsFileName)));
    }

    [Fact]
    public void Doctor_fix_adds_the_restore_with_the_block_only_for_packages_config_projects()
    {
        var without = DoctorRunner.PlanFix(_repo.Path);
        var with = DoctorRunner.PlanFix(_repo.Path, packagesConfig: true);

        Assert.Empty(without.PackagesConfigFiles);
        Assert.DoesNotContain(PackagesConfigRestore.TargetsFileName, without.Diff, StringComparison.Ordinal);
        Assert.Equal(2, with.PackagesConfigFiles.Count);
        Assert.Contains("+  <Import Project=\"$(MSBuildThisFileDirectory)" + PackagesConfigRestore.TargetsFileName, with.Diff, StringComparison.Ordinal);

        var applied = DoctorRunner.ApplyFix(_repo.Path, packagesConfig: true);

        Assert.True(applied.Applied);
        Assert.All(applied.PackagesConfigFiles, f => Assert.True(f.Applied));
        Assert.False(DoctorRunner.PlanFix(_repo.Path, packagesConfig: true).HasChanges);
    }

    /// <summary>
    /// A fresh clone of the mvc5 fixture: a legacy MVC 5 site on packages.config, whose HintPaths point into
    /// packages/, including a package with a four-part version (Microsoft.Web.Infrastructure 1.0.0.0). With only the
    /// compile-only block, a plain <c>dotnet build</c> fails, since nothing fills packages/.
    /// </summary>
    [Fact]
    public async Task Without_the_restore_a_plain_build_of_a_packages_config_solution_fails()
    {
        using var repository = await FixtureRepository.CreateAsync("mvc5");
        DoctorRunner.ApplyFix(repository.Path);

        var build = await BuildAsync(repository.Path);

        Assert.False(build.Succeeded, "The build passed without packages/; this test would prove nothing.");
        Assert.Contains("CS0246", build.StandardOutput, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repository.Path, "packages")));
    }

    [Fact]
    public async Task After_doctor_fix_a_plain_build_restores_packages_config_and_succeeds()
    {
        using var repository = await FixtureRepository.CreateAsync("mvc5");
        var fix = DoctorRunner.ApplyFix(repository.Path, packagesConfig: true);

        var build = await BuildAsync(repository.Path);
        var again = await BuildAsync(repository.Path);

        Assert.Equal([PackagesConfigRestore.TargetsFileName, PackagesConfigRestore.SolutionTargetsFileName], fix.PackagesConfigFiles.Select(f => f.File));
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
        Assert.Contains("Offramp: laid out", build.StandardOutput, StringComparison.Ordinal);
        foreach (var package in new[] { "Microsoft.AspNet.Mvc.5.2.9", "Microsoft.Web.Infrastructure.1.0.0.0", "Newtonsoft.Json.13.0.3" })
        {
            Assert.True(Directory.Exists(Path.Combine(repository.Path, "packages", package)), package);
        }

        Assert.True(File.Exists(Path.Combine(repository.Path, "packages", "Microsoft.AspNet.Mvc.5.2.9", "Microsoft.AspNet.Mvc.5.2.9.nupkg")));
        Assert.False(File.Exists(Path.Combine(repository.Path, "packages", "Microsoft.AspNet.Mvc.5.2.9", ".nupkg.metadata")));

        // A second build finds everything in place and lays out nothing.
        Assert.True(again.Succeeded, again.StandardOutput + again.StandardError);
        Assert.DoesNotContain("Offramp: laid out", again.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_built_on_its_own_finds_its_packages_folder_from_its_hint_paths()
    {
        using var repository = await FixtureRepository.CreateAsync("mvc5");
        DoctorRunner.ApplyFix(repository.Path, packagesConfig: true);

        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/Shop.Web/Shop.Web.csproj", "-nologo", "-v:minimal", "-nodeReuse:false"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(10),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        });

        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
        Assert.True(Directory.Exists(Path.Combine(repository.Path, "packages", "Microsoft.AspNet.Mvc.5.2.9")));
    }

    /// <summary>
    /// HintPaths written from <c>$(MSBuildThisFileDirectory)</c>, as some projects write them: the task reads the
    /// project file as text, so it resolves that property itself. MSBuild expands <c>$(...)</c> in an inline task's
    /// code too, so the code must not spell it (on Windows the expansion broke the compile; elsewhere it matched nothing).
    /// </summary>
    [Fact]
    public async Task A_project_built_on_its_own_resolves_hint_paths_written_from_its_own_folder()
    {
        using var repository = await FixtureRepository.CreateAsync("mvc5");
        var project = Path.Combine(repository.Path, "src", "Shop.Web", "Shop.Web.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("<HintPath>..\\..\\packages\\", "<HintPath>$(MSBuildThisFileDirectory)..\\..\\packages\\", StringComparison.Ordinal));
        DoctorRunner.ApplyFix(repository.Path, packagesConfig: true);

        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/Shop.Web/Shop.Web.csproj", "-nologo", "-v:minimal", "-nodeReuse:false"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(10),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        });

        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
        Assert.True(Directory.Exists(Path.Combine(repository.Path, "packages", "Microsoft.AspNet.Mvc.5.2.9")));
    }

    /// <summary>
    /// legacy-shared lists Newtonsoft.Json 13.0.3 in one project and 12.0.1 in another. Restored from an empty NuGet
    /// cache, as on a fresh machine, both are downloaded and laid out: NuGet keeps one download item per package, so
    /// the generated project names both versions in one item.
    /// </summary>
    [Fact]
    public async Task Two_versions_of_a_package_in_two_projects_are_both_restored_from_an_empty_cache()
    {
        using var repository = await FixtureRepository.CreateAsync("legacy-shared");
        using var cache = new ScratchDirectory("empty-nuget-cache");
        DoctorRunner.ApplyFix(repository.Path, packagesConfig: true);

        var restore = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["restore", "LegacyShared.sln", "-nologo", "-v:minimal", "-nodeReuse:false"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(10),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1", ["NUGET_PACKAGES"] = cache.Path },
        });

        Assert.True(restore.Succeeded, restore.StandardOutput + restore.StandardError);
        Assert.True(Directory.Exists(Path.Combine(repository.Path, "packages", "Newtonsoft.Json.12.0.1", "lib")), restore.StandardOutput);
        Assert.True(Directory.Exists(Path.Combine(repository.Path, "packages", "Newtonsoft.Json.13.0.3", "lib")), restore.StandardOutput);
    }

    private static Task<ProcessResult> BuildAsync(string root) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "Mvc5.sln", "-nologo", "-v:minimal", "-nodeReuse:false"])
        {
            WorkingDirectory = root,
            Timeout = TimeSpan.FromMinutes(10),
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        });
}
