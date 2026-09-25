using System.Text.Json.Nodes;
using Offramp.Fixtures;
using Offramp.Workspace.Environment;

namespace Offramp.Cli.Tests;

public sealed class DoctorCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    /// <summary>Roadmap M0 acceptance: <c>offramp doctor --json</c> validates against its schema.</summary>
    [Fact]
    public async Task Json_output_validates_against_the_envelope_and_doctor_schemas()
    {
        var run = await _cli.RunAsync("doctor", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "doctor");
    }

    [Fact]
    public async Task Json_envelope_matches_the_snapshot()
    {
        var run = await _cli.RunAsync("doctor", "--json");
        await Verify(Scrub.Envelope(run.Out, _cli.Repo.Path), extension: "json");
    }

    [Fact]
    public async Task Human_output_matches_the_snapshot()
    {
        var run = await _cli.RunAsync("doctor");

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("\u001b[", run.Out, StringComparison.Ordinal);
        await Verify(Scrub.Text(run.Out, _cli.Repo.Path), extension: "txt");
    }

    [Fact]
    public async Task A_failing_check_exits_1_and_says_what_to_do()
    {
        _cli.Machine.SelectedSdk = "8.0.404";

        var run = await _cli.RunAsync("doctor");

        Assert.Equal(1, run.ExitCode);
        Assert.StartsWith("Doctor found 1 failing check", run.Out, StringComparison.Ordinal);
        Assert.Contains("Install the .NET 10 SDK", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warnings_exit_0_by_default_and_1_with_fail_on_warning()
    {
        _cli.Machine.GitVersion = null;

        Assert.Equal(0, (await _cli.RunAsync("doctor")).ExitCode);
        Assert.Equal(1, (await _cli.RunAsync("doctor", "--fail-on", "warning")).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("doctor", "--fail-on", "never")).ExitCode);
    }

    [Fact]
    public async Task Doctor_runs_and_reports_even_when_the_configuration_is_invalid()
    {
        _cli.Repo.Write("offramp.yml", "verify:\n  mode: compile\n");

        var run = await _cli.RunAsync("doctor", "--json");

        Assert.Equal(1, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal("fail", result["checks"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "config")!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Overridden_severities_are_marked_in_the_output()
    {
        _cli.Machine.RepositoryRoot = null;
        _cli.Repo.Write("offramp.yml", "rules:\n  OFR0015: { severity: error, reason: \"moves must be staged\" }\n");

        var run = await _cli.RunAsync("doctor", "--json");

        Assert.Equal(1, run.ExitCode);
        var diagnostic = JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == "OFR0015")!;
        Assert.Equal("error", diagnostic["severity"]!.GetValue<string>());
        Assert.True(diagnostic["overridden"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unreachable_feed_is_reported_without_failing()
    {
        _cli.Machine.ReferenceAssemblies = new ReferenceAssembliesResult(ReferenceAssembliesState.FeedUnreachable, "corp");

        var run = await _cli.RunAsync("doctor", "--json");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("\"OFR1006\"", run.Out, StringComparison.Ordinal);
    }
}
