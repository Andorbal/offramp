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
        var app = repository.Directory.Read("src/App/App.csproj");
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />", app, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"..\\Legacy.Core\\Legacy.Core.csproj\" />", app, StringComparison.Ordinal);
        Assert.DoesNotContain("lib\\Newtonsoft.Json.dll", app, StringComparison.Ordinal);
        Assert.Contains("lib\\Vendor.Reporting.dll", app, StringComparison.Ordinal);
        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/App/App.csproj", "-nologo", "-v:q"]) { WorkingDirectory = repository.Path, Timeout = TimeSpan.FromMinutes(5) });
        Assert.True(build.Succeeded, build.StandardOutput + build.StandardError);
    }
}
