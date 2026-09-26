using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>audit api-compat</c> on <c>dual-target</c>, with the real SDK and ApiCompat tool.</summary>
public sealed class AuditApiCompatCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR3501")]
    public async Task A_member_only_one_target_has_is_reported_and_identical_targets_are_clean()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds().WithRealTools();

        var clean = await cli.RunAsync("audit", "api-compat", "--project", "src/Shared/Shared.csproj", "--json");
        var clock = repository.Directory.Read("src/Shared/Clock.cs");
        repository.Directory.Write("src/Shared/Clock.cs", clock.Replace("#else", "    public static string Zone() => \"local\";\n#else", StringComparison.Ordinal));
        var divergent = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--json");

        Assert.Equal(0, clean.ExitCode);
        SchemaAssert.ValidEnvelope(clean.Out, "api-compat");
        Assert.Empty(JsonNode.Parse(clean.Out)!["result"]!["differences"]!.AsArray());
        Assert.Equal(0, divergent.ExitCode);
        var result = JsonNode.Parse(divergent.Out)!;
        var difference = Assert.Single(result["result"]!["differences"]!.AsArray())!;
        Assert.Equal(("CP0002", "string Shared.Clock.Zone()", "net48"), (difference["code"]!.GetValue<string>(), difference["member"]!.GetValue<string>(), difference["onlyOn"]!.GetValue<string>()));
        Assert.Equal("Member 'string Shared.Clock.Zone()' exists on net48 but not on net10.0", difference["message"]!.GetValue<string>());
        Assert.Contains(result["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR3501");
        result["result"]!["tool"] = "{Tool}";
        await Verify(result["result"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), extension: "json");
    }

    [Fact]
    [ProducesDiagnostic("OFR3502")]
    public async Task The_working_tree_is_compared_with_a_baseline_revision()
    {
        var fixture = await ScannedFixtures.ScanAsync("dual-target");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds().WithRealTools();
        var formatter = repository.Directory.Read("src/Shared/Formatter.cs");
        repository.Directory.Write("src/Shared/Formatter.cs", formatter.Replace(
            "    public string Format(Order order) => JsonConvert.SerializeObject(order);\n",
            "    public string Format(Order order) => JsonConvert.SerializeObject(order);\n\n    public string Name() => \"shared\";\n",
            StringComparison.Ordinal));

        var run = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--baseline", "HEAD", "--json");

        Assert.True(run.ExitCode == 0, run.ToString());
        SchemaAssert.ValidEnvelope(run.Out, "api-compat");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal(("baseline", "HEAD", "working tree"), (result["mode"]!.GetValue<string>(), result["left"]!["label"]!.GetValue<string>(), result["right"]!["label"]!.GetValue<string>()));
        var added = Assert.Single(result["differences"]!.AsArray())!;
        Assert.Equal(("string Shared.Formatter.Name()", "working tree"), (added["member"]!.GetValue<string>(), added["onlyOn"]!.GetValue<string>()));
    }

    [Fact]
    [ProducesDiagnostic("OFR3503")]
    public async Task A_single_target_project_without_a_baseline_has_nothing_to_compare()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("audit", "api-compat", "--project", "src/Tool/Tool.csproj", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR3503", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR3504")]
    public async Task A_side_that_does_not_build_is_an_environment_failure()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        using var cli = new CliHarness(fixture.Repository.Directory);
        cli.Machine.Setup.Add(r => r
            .On("dotnet", ["--version"], 0, "10.0.100\n")
            .On("dotnet", ["tool", "install"], spec =>
            {
                var directory = spec.Arguments[spec.Arguments.ToList().IndexOf("--tool-path") + 1];
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, OperatingSystem.IsWindows() ? "apicompat.exe" : "apicompat"), "");
                return new ProcessResult(0, "installed", "");
            })
            .On("dotnet", ["build"], 1, "Shared.cs(3,1): error CS1002: ; expected", ""));

        var run = await cli.RunAsync("audit", "api-compat", "--project", "Shared", "--json");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("did not build for net48: Shared.cs(3,1): error CS1002: ; expected", run.Out, StringComparison.Ordinal);
    }
}
