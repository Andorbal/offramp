using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>codemod list|run</c> on the <c>codemods</c> fixture: rewrites that build, and a second run that changes nothing.</summary>
public sealed class CodemodCommandTests
{
    [Fact]
    public async Task The_catalog_lists_every_codemod()
    {
        using var cli = new CliHarness();

        var run = await cli.RunAsync("codemod", "list", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "codemod-list");
        var codemods = JsonNode.Parse(run.Out)!["result"]!["codemods"]!.AsArray();
        Assert.Equal([.. Enumerable.Range(1, 13).Select(i => $"OFRM{i:000}")], codemods.Select(c => c!["id"]!.GetValue<string>()));
        await Verify(Scrub.Envelope(run.Out, cli.Repo.Path), extension: "json");
    }

    [Fact]
    [ProducesDiagnostic("OFR4501")]
    [ProducesDiagnostic("OFR4510")]
    public async Task Every_default_codemod_rewrites_the_fixture_which_builds_and_a_second_run_changes_nothing()
    {
        var fixture = await ScannedFixtures.ScanAsync("codemods");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("codemod", "run", "--mod", "all", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "codemod-run");
        Assert.Equal(["OFR4501", "OFR4501", "OFR4510"], Codes(dryRun.Out).Where(c => c.StartsWith("OFR45", StringComparison.Ordinal)));
        Assert.Empty((await repository.GitAsync("status", "--porcelain", "--untracked-files=no")).StandardOutput.Trim());
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");

        var applied = await cli.RunAsync("codemod", "run", "--mod", "all", "--apply", "--json");

        Assert.True(applied.ExitCode == 0, applied.Out);
        var result = JsonNode.Parse(applied.Out)!["result"]!;
        Assert.True(result["applied"]!.GetValue<bool>());
        Assert.True(result["verify"]!["passed"]!.GetValue<bool>(), applied.Out);
        var program = repository.Directory.Read("src/Shop/Program.cs");
        Assert.Contains("System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);", program, StringComparison.Ordinal);
        Assert.Contains("Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });", program, StringComparison.Ordinal);
        Assert.Contains("public Mailer(IClock clock, Microsoft.Extensions.Configuration.IConfiguration configuration)", repository.Directory.Read("src/Shop/Mailer.cs"), StringComparison.Ordinal);
        Assert.Contains("<ItemGroup Condition=\"'$(TargetFrameworkIdentifier)' == '.NETFramework'\">", repository.Directory.Read("src/Shared/Shared.csproj"), StringComparison.Ordinal);
        var status = (await repository.GitAsync("status", "--porcelain", "--untracked-files=no")).StandardOutput;
        Assert.All(status.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith(" M ", line, StringComparison.Ordinal));

        // Idempotency: rescan the rewritten code, run again, and nothing changes.
        var rescan = await cli.RunAsync("scan", "--json");
        Assert.True(rescan.ExitCode == 0, rescan.Out);
        var again = await cli.RunAsync("codemod", "run", "--mod", "all", "--json");

        Assert.Equal(0, again.ExitCode);
        var second = JsonNode.Parse(again.Out)!["result"]!;
        Assert.Equal((0, 0, 0), (second["summary"]!["sitesRewritten"]!.GetValue<int>(), second["summary"]!["filesChanged"]!.GetValue<int>(), second["summary"]!["packagesAdded"]!.GetValue<int>()));
        Assert.Null(second["preview"]);
        Assert.Equal(2, second["summary"]!["sitesSkipped"]!.GetValue<int>());
    }

    [Fact]
    [ProducesDiagnostic("OFR4502")]
    [ProducesDiagnostic("OFR4503")]
    public async Task Unknown_and_experimental_codemods_are_refused_and_opt_in_ones_run_when_named()
    {
        var fixture = await ScannedFixtures.GetAsync("codemods");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var unknown = await cli.RunAsync("codemod", "run", "--mod", "sqlclient,sql-client", "--json");
        var experimental = await cli.RunAsync("codemod", "run", "--mod", "thread-abort", "--json");
        var allowed = await cli.RunAsync("codemod", "run", "--mod", "OFRM007", "--experimental", "--json");
        var optIn = await cli.RunAsync("codemod", "run", "--mod", "string-comparison", "--json");

        Assert.Equal((2, 2), (unknown.ExitCode, experimental.ExitCode));
        Assert.Contains("OFR4502", Codes(unknown.Out));
        Assert.Contains("OFR4503", Codes(experimental.Out));
        Assert.Equal(0, allowed.ExitCode);
        Assert.Contains("_threadCancellation.Cancel();", JsonNode.Parse(allowed.Out)!["result"]!["preview"]!.GetValue<string>(), StringComparison.Ordinal);
        var names = JsonNode.Parse(optIn.Out)!["result"]!;
        Assert.Equal(["string-comparison"], names["codemods"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("name.StartsWith(\"Internal.\", System.StringComparison.Ordinal)", names["preview"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4504")]
    [ProducesDiagnostic("OFR4505")]
    public async Task Files_changed_since_the_scan_and_packages_config_projects_are_left_alone()
    {
        var fixture = await ScannedFixtures.ScanAsync("codemods", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, "src/Shop/packages.config"), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<packages>\n</packages>\n");
            return request;
        });
        using var repository = fixture.Repository;
        File.AppendAllText(repository.Directory.Combine("src", "Shop", "Program.cs"), "// edited after the scan\n");
        using var cli = new CliHarness(repository.Directory);

        var run = await cli.RunAsync("codemod", "run", "--mod", "process-start-url,sqlclient", "--json");

        Assert.Equal(1, run.ExitCode);
        var node = JsonNode.Parse(run.Out)!;
        var stale = Assert.Single(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4504");
        Assert.Equal("src/Shop/Program.cs", stale!["file"]!.GetValue<string>());
        Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4505" && d["message"]!.GetValue<string>().Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal));
        var sites = node["result"]!["projects"]![0]!["sites"]!.AsArray();
        Assert.Contains(sites, s => s!["codemod"]!.GetValue<string>() == "process-start-url" && s["outcome"]!.GetValue<string>() == "skipped");
        Assert.Contains(sites, s => s!["codemod"]!.GetValue<string>() == "sqlclient" && s["outcome"]!.GetValue<string>() == "rewritten");
        Assert.Empty(node["result"]!["projects"]![0]!["packages"]!.AsArray());
    }

    [Fact]
    [ProducesDiagnostic("OFR4507")]
    public async Task A_failed_verification_rolls_the_codemods_back()
    {
        var fixture = await ScannedFixtures.ScanAsync("codemods");
        using var repository = fixture.Repository;
        var command = OperatingSystem.IsWindows() ? "exit /b 1" : "exit 1";
        repository.Directory.Write("offramp.yml", $"version: 1\nverify:\n  mode: command\n  command: \"{command}\"\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        cli.Machine.Setup.Add(r => r.On(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", [], spec => ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult()));
        var before = MoveCommandTests.Tree(fixture.Root);

        var run = await cli.RunAsync("codemod", "run", "--mod", "process-start-url", "--apply", "--json");

        Assert.Equal(1, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.True(result["rolledBack"]!.GetValue<bool>());
        Assert.False(result["applied"]!.GetValue<bool>());
        Assert.Contains("OFR4507", Codes(run.Out));
        Assert.Equal(before, MoveCommandTests.Tree(fixture.Root));
    }

    [Fact]
    public async Task Duplicate_assembly_attributes_move_to_the_project_file_and_the_project_builds_again()
    {
        // The duplicates break the build (CS0579), but the compiler call is still recorded.
        var fixture = await ScannedFixtures.ScanAsync("codemods", (root, request) =>
        {
            Directory.CreateDirectory(Path.Combine(root, "src/Shared/Properties"));
            File.WriteAllText(Path.Combine(root, "src/Shared/Properties/AssemblyInfo.cs"),
                "using System.Reflection;\nusing System.Runtime.InteropServices;\n\n[assembly: AssemblyCompany(\"Contoso\")]\n[assembly: AssemblyVersion(\"2.1.0.0\")]\n[assembly: ComVisible(false)]\n");
            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("codemod", "run", "--mod", "assemblyinfo", "--project", "Shared", "--apply", "--json");

        Assert.True(run.ExitCode == 0, run.Out);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.True(result["verify"]!["passed"]!.GetValue<bool>(), run.Out);
        Assert.Equal("using System.Reflection;\nusing System.Runtime.InteropServices;\n\n[assembly: ComVisible(false)]\n", repository.Directory.Read("src/Shared/Properties/AssemblyInfo.cs"));
        var project = repository.Directory.Read("src/Shared/Shared.csproj");
        Assert.Contains("<Company>Contoso</Company>", project, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>2.1.0.0</AssemblyVersion>", project, StringComparison.Ordinal);
    }

    /// <summary>
    /// --format-mode end to end: the Offramp.Analyzers package, packed from this repository,
    /// referenced by the fixture, and applied by <c>dotnet format analyzers</c>; a project
    /// without the package is skipped, and a project dotnet format cannot load is reported.
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR4506")]
    [ProducesDiagnostic("OFR4508")]
    public async Task Format_mode_applies_the_codemods_from_the_packed_analyzer_package()
    {
        // A version of its own, so NuGet's global packages folder never serves an older build of it.
        var version = "0.0.1-codemodtest." + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var feed = new ScratchDirectory();
        var pack = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet",
            ["pack", RepositoryFiles.Path("src", "Offramp.Analyzers.CodeFixes", "Offramp.Analyzers.CodeFixes.csproj"), "-o", feed.Path, "-nologo", "-v:q", "-p:MinVerVersionOverride=" + version])
        {
            Timeout = TimeSpan.FromMinutes(10),
        }, TestContext.Current.CancellationToken);
        Assert.True(pack.ExitCode == 0, pack.StandardOutput + pack.StandardError);
        using (var package = System.IO.Compression.ZipFile.OpenRead(Path.Combine(feed.Path, $"Offramp.Analyzers.{version}.nupkg")))
        {
            // Both assemblies as analyzers, nothing to compile against, and the properties OFRM013 reads.
            var entries = package.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("analyzers/dotnet/cs/Offramp.Analyzers.dll", entries);
            Assert.Contains("analyzers/dotnet/cs/Offramp.Analyzers.CodeFixes.dll", entries);
            Assert.Contains("build/Offramp.Analyzers.props", entries);
            Assert.Contains("README.md", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith("lib/", StringComparison.Ordinal));
        }

        try
        {
            var fixture = await ScannedFixtures.ScanAsync("codemods", (root, request) =>
            {
                File.WriteAllText(Path.Combine(root, "nuget.config"),
                    $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <add key=\"local\" value=\"{feed.Path}\" />\n  </packageSources>\n</configuration>\n");
                var project = Path.Combine(root, "src/Shop/Shop.csproj");
                File.WriteAllText(project, File.ReadAllText(project).Replace("<ProjectReference", $"<PackageReference Include=\"Offramp.Analyzers\" Version=\"{version}\" PrivateAssets=\"all\" />\n    <ProjectReference", StringComparison.Ordinal));
                return request;
            });
            using var repository = fixture.Repository;
            using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
            cli.Machine.Setup.Add(r => r.On("dotnet", ["format"], spec => ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult()));

            var dryRun = await cli.RunAsync("codemod", "run", "--mod", "process-start-url", "--format-mode", "--project", "Shop", "--project", "Shared", "--json");

            Assert.True(dryRun.ExitCode == 0, dryRun.Out);
            SchemaAssert.ValidEnvelope(dryRun.Out, "codemod-run");
            var node = JsonNode.Parse(dryRun.Out)!;
            Assert.Equal("format", node["result"]!["mode"]!.GetValue<string>());
            var site = Assert.Single(node["result"]!["projects"]![0]!["sites"]!.AsArray());
            Assert.Equal(("process-start-url", "src/Shop/Program.cs"), (site!["codemod"]!.GetValue<string>(), site["file"]!.GetValue<string>()));
            Assert.Contains(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4506" && d["project"]!.GetValue<string>() == "src/Shared/Shared.csproj");
            Assert.DoesNotContain("UseShellExecute", repository.Directory.Read("src/Shop/Program.cs"), StringComparison.Ordinal);

            var applied = await cli.RunAsync("codemod", "run", "--mod", "process-start-url", "--format-mode", "--project", "Shop", "--apply", "--json");

            Assert.True(applied.ExitCode == 0, applied.Out);
            Assert.Contains("Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });", repository.Directory.Read("src/Shop/Program.cs"), StringComparison.Ordinal);

            // A project dotnet format cannot load.
            File.WriteAllText(repository.Directory.Combine("src", "Shop", "Shop.csproj"), "<Project>\n");
            var broken = await cli.RunAsync("codemod", "run", "--mod", "process-start-url", "--format-mode", "--project", "Shop", "--json");

            Assert.Equal(1, broken.ExitCode);
            Assert.Contains(JsonNode.Parse(broken.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4508" && d["project"]!.GetValue<string>() == "src/Shop/Shop.csproj");
        }
        finally
        {
            var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            var cached = Path.Combine(packages, "offramp.analyzers", version);
            if (Directory.Exists(cached))
            {
                Directory.Delete(cached, recursive: true);
            }
        }
    }

    private static List<string> Codes(string json) =>
        [.. JsonNode.Parse(json)!["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
}
