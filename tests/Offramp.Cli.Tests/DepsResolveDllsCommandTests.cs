using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;
using Offramp.Fixtures.Feeds;

namespace Offramp.Cli.Tests;

/// <summary><c>deps resolve-dlls</c> on <c>loose-dlls</c>: a project's output, a package's DLL, and two vendor DLLs (one blocking).</summary>
public sealed class DepsResolveDllsCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR1401")]
    [ProducesDiagnostic("OFR1402")]
    [ProducesDiagnostic("OFR1403")]
    [ProducesDiagnostic("OFR1404")]
    public async Task Loose_dlls_become_project_and_package_references_and_the_rest_are_reported()
    {
        var fixture = await ScannedFixtures.ScanAsync("loose-dlls");
        using var repository = fixture.Repository;
        VersionsFeed.WriteFolderFeed(repository.Directory.Combine(".offramp", "recorded-feed"));
        repository.Directory.Write("offramp.yml", "version: 1\ndeps:\n  feeds: [ .offramp/recorded-feed ]\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("deps", "resolve-dlls", "--json");

        Assert.Equal(1, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "deps-resolve-dlls");
        var node = JsonNode.Parse(dryRun.Out)!;
        var references = node["result"]!["projects"]![0]!["references"]!.AsArray();
        Assert.Equal(
            ["Legacy.Core project src/Legacy.Core/Legacy.Core.csproj", "Newtonsoft.Json package 13.0.1", "Vendor.Common none false", "Vendor.Reporting none true"],
            references.Select(r => r!["name"] + " " + r["resolution"]!["kind"] + " " + (r["resolution"]!["project"] ?? r["resolution"]!["version"] ?? r["blocker"]!.ToJsonString())));
        Assert.Equal(["OFR1401", "OFR1402", "OFR1403", "OFR1404"], node["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).Where(c => c.StartsWith("OFR14", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        var applied = await cli.RunAsync("deps", "resolve-dlls", "--apply", "--json");

        Assert.True(JsonNode.Parse(applied.Out)!["result"]!["applied"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(applied.Out)!["result"]!["verify"]!["passed"]!.GetValue<bool>(), applied.Out);
        var app = repository.Directory.Read("src/App/App.csproj");
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />", app, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"..\\Legacy.Core\\Legacy.Core.csproj\" />", app, StringComparison.Ordinal);
        Assert.DoesNotContain("lib\\Newtonsoft.Json.dll", app, StringComparison.Ordinal);
        Assert.Contains("lib\\Vendor.Reporting.dll", app, StringComparison.Ordinal);
        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/App/App.csproj", "-nologo", "-v:q"]) { WorkingDirectory = repository.Path, Timeout = TimeSpan.FromMinutes(5) });
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
    }

    /// <summary>
    /// NHibernate field test (P1 #6): `--apply` exited 0 with `applied: true` and the solution no longer
    /// built. It now verifies the edited projects and restores every file when verification fails.
    /// </summary>
    [Fact]
    [ProducesDiagnostic("OFR1408")]
    public async Task A_failed_verification_rolls_the_references_back()
    {
        var fixture = await ScannedFixtures.ScanAsync("loose-dlls");
        using var repository = fixture.Repository;
        VersionsFeed.WriteFolderFeed(repository.Directory.Combine(".offramp", "recorded-feed"));
        var command = OperatingSystem.IsWindows() ? "exit /b 1" : "exit 1";
        repository.Directory.Write("offramp.yml", $"version: 1\ndeps:\n  feeds: [ .offramp/recorded-feed ]\nverify:\n  mode: command\n  command: \"{command}\"\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        cli.Machine.Setup.Add(r => r.On(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", [], spec => ProcessRunner.Instance.RunAsync(spec).GetAwaiter().GetResult()));
        var before = repository.Directory.Read("src/App/App.csproj");

        var run = await cli.RunAsync("deps", "resolve-dlls", "--apply", "--json");

        Assert.Equal(1, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "deps-resolve-dlls");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal((false, true, false), (result["applied"]!.GetValue<bool>(), result["rolledBack"]!.GetValue<bool>(), result["verify"]!["passed"]!.GetValue<bool>()));
        Assert.Contains(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR1408");
        Assert.Equal(before, repository.Directory.Read("src/App/App.csproj"));
    }

    [Fact]
    public async Task A_dll_packages_config_installs_is_that_exact_package_and_is_left_alone()
    {
        // Billing references packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.dll, which ships
        // assembly version 13.0.0.0 like every 13.x; its packages.config names the version.
        var fixture = await ScannedFixtures.ScanAsync("legacy-csproj");
        using var repository = fixture.Repository;
        VersionsFeed.WriteFolderFeed(repository.Directory.Combine(".offramp", "recorded-feed"));
        repository.Directory.Write("offramp.yml", "version: 1\ndeps:\n  feeds: [ .offramp/recorded-feed ]\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var result = await cli.RunAsync("deps", "resolve-dlls", "--json");

        Assert.Equal(0, result.ExitCode);
        SchemaAssert.ValidEnvelope(result.Out, "deps-resolve-dlls");
        var node = JsonNode.Parse(result.Out)!;
        var newtonsoft = Assert.Single(node["result"]!["projects"]!.AsArray().SelectMany(p => p!["references"]!.AsArray()), r => r!["name"]!.GetValue<string>() == "Newtonsoft.Json")!;
        Assert.Equal(
            ("packagesConfig", "Newtonsoft.Json", "13.0.3", false),
            (newtonsoft["resolution"]!["kind"]!.GetValue<string>(), newtonsoft["resolution"]!["package"]!.GetValue<string>(), newtonsoft["resolution"]!["version"]!.GetValue<string>(), newtonsoft["blocker"]!.GetValue<bool>()));
        Assert.Equal(1, node["result"]!["summary"]!["packagesConfig"]!.GetValue<int>());
        Assert.Null(node["result"]!["preview"]);
        Assert.DoesNotContain(node["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>().StartsWith("OFR14", StringComparison.Ordinal));
    }
}
