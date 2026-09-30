using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;
using Offramp.Workspace.Restore;
using Offramp.Workspace.Scanning;

namespace Offramp.Workspace.Tests;

/// <summary>
/// The restore <c>scan</c> runs outside Windows for packages.config projects
/// (docs/decisions/0037-legacy-projects-outside-windows.md), against a local folder feed and an
/// empty global packages folder, so nothing leaves the machine.
/// </summary>
public sealed class PackagesConfigRestorerTests : IDisposable
{
    private readonly ScratchDirectory _repo = new("packages-config-restore");

    public PackagesConfigRestorerTests()
    {
        WriteNupkg("feed/legacy.widgets.1.0.0.nupkg", Package("Legacy.Widgets", "1.0.0.0", "lib/net45/Legacy.Widgets.txt"));
        _repo.Write("nuget.config", """
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="feed" />
              </packageSources>
              <config>
                <add key="globalPackagesFolder" value="global-packages" />
              </config>
            </configuration>
            """);
        _repo.Write("App.slnx", "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n");
        _repo.Write("src/App/App.csproj", "<Project />");
        _repo.Write("src/App/packages.config", """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Legacy.Widgets" version="1.0.0.0" targetFramework="net472" />
              <package id="Missing.Package" version="2.0.0" targetFramework="net472" />
            </packages>
            """);
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Packages_land_in_the_solution_packages_folder_as_nuget_restore_lays_them_out()
    {
        var result = await RestoreAsync();

        Assert.NotNull(result);
        Assert.Equal(_repo.Combine("packages"), result.PackagesFolder);
        Assert.Equal(["Legacy.Widgets.1.0.0.0"], result.Restored);
        Assert.Equal("x", File.ReadAllText(_repo.Combine("packages/Legacy.Widgets.1.0.0.0/lib/net45/Legacy.Widgets.txt")));
        Assert.True(File.Exists(_repo.Combine("packages/Legacy.Widgets.1.0.0.0/Legacy.Widgets.1.0.0.0.nupkg")));

        var missing = Assert.Single(result.Failed);
        Assert.Equal(("Missing.Package", "2.0.0"), (missing.Id, missing.Version));
        Assert.Equal("not in the global packages folder or on local.", missing.Reason);
    }

    [Fact]
    public async Task A_package_folder_that_exists_in_any_letter_case_is_never_touched()
    {
        _repo.Write("packages/legacy.widgets.1.0.0.0/lib/net45/Legacy.Widgets.txt", "mine");

        var result = await RestoreAsync();

        Assert.Empty(result!.Restored);
        Assert.Equal("mine", File.ReadAllText(_repo.Combine("packages/legacy.widgets.1.0.0.0/lib/net45/Legacy.Widgets.txt")));
        Assert.Equal(["legacy.widgets.1.0.0.0"], Directory.GetDirectories(_repo.Combine("packages")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_global_packages_folder_is_used_before_the_feeds()
    {
        WriteNupkg("global-packages/cached.only/1.2.0/cached.only.1.2.0.nupkg", Package("Cached.Only", "1.2.0", "lib/net45/Cached.txt"));
        _repo.Write("src/App/packages.config", """
            <packages>
              <package id="Cached.Only" version="1.2.0" targetFramework="net472" />
            </packages>
            """);

        var result = await RestoreAsync();

        Assert.Equal(["Cached.Only.1.2.0"], result!.Restored);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(_repo.Combine("packages/Cached.Only.1.2.0/lib/net45/Cached.txt")));
    }

    /// <summary>The packages.config restore took about 200 silent seconds on Open Live Writer; it reports each package.</summary>
    [Fact]
    public async Task Each_package_is_reported_as_it_is_restored()
    {
        var progress = new List<string>();

        await PackagesConfigRestorer.RestoreAsync(
            _repo.Combine("App.slnx"), [_repo.Combine("src/App/App.csproj")], new RecordingPhase(progress), TestContext.Current.CancellationToken);

        Assert.Equal(["0/2 Legacy.Widgets 1.0.0.0", "1/2 Missing.Package 2.0.0", "2/2"], progress);
        Assert.True(PackagesConfigRestorer.HasPackagesConfig([_repo.Combine("src/App/App.csproj")]));
        Assert.False(PackagesConfigRestorer.HasPackagesConfig([_repo.Combine("src/Other/Other.csproj")]));
    }

    private sealed class RecordingPhase(List<string> reports) : Offramp.Core.Progress.IProgressPhase
    {
        public string Name => "restore";

        public void Report(int current, int total, string? item = null) =>
            reports.Add(item is null ? $"{current}/{total}" : $"{current}/{total} {item}");

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_solution_without_packages_config_needs_no_restore()
    {
        File.Delete(_repo.Combine("src/App/packages.config"));

        Assert.Null(await RestoreAsync());
        Assert.False(Directory.Exists(_repo.Combine("packages")));
    }

    [Fact]
    [ProducesDiagnostic("OFR0105")]
    [ProducesDiagnostic("OFR0106")]
    public async Task Scan_restores_before_it_builds_outside_windows_and_reports_what_it_could_not()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "nuget restore fills the packages folder on Windows.");
        var bag = new DiagnosticBag();
        var processes = new FakeProcessRunner();
        var request = ScannedFixtures.Request(_repo.Path, bag, processes) with
        {
            Config = new Offramp.Core.Configuration.OfframpConfig { Solution = "App.slnx" },
        };

        await ScanRunner.RunAsync(request, TestContext.Current.CancellationToken);

        var restored = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR0106");
        Assert.Equal("Restored 1 packages.config package(s) into packages/, as nuget restore does on Windows.", restored.Message);
        var failed = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR0105");
        Assert.Equal("Missing.Package 2.0.0 from packages.config could not be restored into packages/: not in the global packages folder or on local.", failed.Message);
        Assert.True(Directory.Exists(_repo.Combine("packages/Legacy.Widgets.1.0.0.0")));

        // The build that follows keeps NuGet.exe from running through Mono.
        var build = Assert.Single(processes.Calls, c => c.FileName == "dotnet" && c.Arguments[0] == "build");
        Assert.Contains("-p:RestorePackages=false", build.Arguments);
    }

    private Task<PackagesConfigRestoreResult?> RestoreAsync() =>
        PackagesConfigRestorer.RestoreAsync(_repo.Combine("App.slnx"), [_repo.Combine("src/App/App.csproj")], TestContext.Current.CancellationToken);

    private void WriteNupkg(string relativePath, RecordedPackage package)
    {
        var path = _repo.Combine(relativePath.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, FeedMaterializer.Nupkg(package));
    }

    private static RecordedPackage Package(string id, string version, string file) => new()
    {
        Id = id,
        Version = version,
        Synthetic = true,
        Files = [new RecordedFile { Path = file, Content = "x" }],
    };
}
