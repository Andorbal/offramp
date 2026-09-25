using System.Text.Json.Nodes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

public sealed class GlobalBehaviorTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Version_prints_the_version_without_build_metadata()
    {
        var run = await _cli.RunAsync("--version");

        Assert.Equal(0, run.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?\n$", run.Out);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("doctor", "--help")]
    [InlineData("init", "--help")]
    public async Task Help_includes_examples(params string[] args)
    {
        var run = await _cli.RunAsync(args);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Examples:", run.Out, StringComparison.Ordinal);
        Assert.Contains("--json", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_arguments_shows_help()
    {
        var run = await _cli.RunAsync();

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("doctor", run.Out, StringComparison.Ordinal);
        Assert.Contains("init", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("doctor", "--bogus")]
    [InlineData("frobnicate")]
    [InlineData("doctor", "--target", "3")]
    [InlineData("doctor", "--target", "ten")]
    [InlineData("doctor", "--fail-on", "sometimes")]
    [InlineData("doctor", "--llm", "--no-llm")]
    [InlineData("init", "--apply", "--dry-run")]
    public async Task Usage_errors_exit_2_with_a_message_on_stderr(params string[] args)
    {
        var run = await _cli.RunAsync(args);

        Assert.Equal(2, run.ExitCode);
        Assert.Equal("", run.Out);
        Assert.Contains("error:", run.Error, StringComparison.Ordinal);
        Assert.Contains("--help", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configuration_precedence_reaches_the_envelope()
    {
        _cli.Repo.Write("offramp.yml", "target: 8\n");

        Assert.Equal("net8.0", await Target());

        _cli.Environment["OFFRAMP_TARGET"] = "9";
        Assert.Equal("net9.0", await Target());

        Assert.Equal("net11.0", await Target("--target", "11"));

        async Task<string> Target(params string[] extra)
        {
            var run = await _cli.RunAsync(["doctor", "--json", .. extra]);
            return JsonNode.Parse(run.Out)!["offramp"]!["target"]!.GetValue<string>();
        }
    }

    [Fact]
    public async Task Solution_flag_is_recorded_repository_relative()
    {
        var run = await _cli.RunAsync("doctor", "--json", "--solution", "src/App.sln");

        var header = JsonNode.Parse(run.Out)!["offramp"]!;
        Assert.Equal("src/App.sln", header["solution"]!.GetValue<string>());
        Assert.Equal("src/App.sln", header["effectiveConfig"]!["solution"]!.GetValue<string>());
    }

    [Fact]
    public async Task Llm_flag_enables_llm_in_the_effective_configuration()
    {
        var run = await _cli.RunAsync("doctor", "--json", "--llm");
        Assert.True(JsonNode.Parse(run.Out)!["offramp"]!["effectiveConfig"]!["llm"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Out_writes_the_envelope_to_a_file()
    {
        var run = await _cli.RunAsync("doctor", "--json", "--out", "reports/doctor.json");

        Assert.Equal("", run.Out);
        SchemaAssert.ValidEnvelope(_cli.Repo.Read("reports/doctor.json"), "doctor");
    }

    [Fact]
    public async Task Json_mode_writes_ndjson_progress_to_stderr_only()
    {
        var run = await _cli.RunAsync("doctor", "--json");

        var lines = run.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            SchemaAssert.Valid("progress", line);
        }

        Assert.Equal(["phase", "done", "phase", "done", "phase", "done"], lines.Select(l => JsonNode.Parse(l)!["event"]!.GetValue<string>()));
        Assert.StartsWith("{\n  \"$schema\"", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quiet_suppresses_progress()
    {
        var run = await _cli.RunAsync("doctor", "--json", "--quiet");
        Assert.Equal("", run.Error);
    }

    [Fact]
    public async Task Redirected_stderr_gets_one_plain_line_per_phase()
    {
        var run = await _cli.RunAsync("doctor");

        Assert.Equal(
            "[1/3] Checking .NET SDKs\n[2/3] Checking .NET Framework reference assemblies\n[3/3] Checking git and repository\n",
            run.Error);
    }

    [Fact]
    public async Task Colors_appear_on_a_terminal_and_disappear_with_no_color()
    {
        _cli.OutputIsTerminal = true;
        var colored = await _cli.RunAsync("doctor");
        Assert.Contains("\u001b[", colored.Out, StringComparison.Ordinal);

        _cli.Environment["NO_COLOR"] = "1";
        var plain = await _cli.RunAsync("doctor");
        Assert.DoesNotContain("\u001b[", plain.Out, StringComparison.Ordinal);

        _cli.Environment.Remove("NO_COLOR");
        _cli.Environment["TERM"] = "dumb";
        var dumb = await _cli.RunAsync("doctor");
        Assert.DoesNotContain("\u001b[", dumb.Out, StringComparison.Ordinal);
    }

    /// <summary>Linux environments often carry both spellings of proxy variables.</summary>
    [Fact]
    public async Task Case_variant_environment_variables_do_not_break_rendering()
    {
        _cli.Environment["https_proxy"] = "http://127.0.0.1:1";
        _cli.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
        _cli.Environment["GITHUB_ACTIONS"] = "true";

        foreach (var terminal in new[] { false, true })
        {
            _cli.OutputIsTerminal = terminal;
            var run = await _cli.RunAsync("doctor");
            Assert.Equal(0, run.ExitCode);
            Assert.StartsWith(terminal ? "[" : "Doctor passed", run.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Json_output_never_contains_ansi_escapes_even_on_a_terminal()
    {
        _cli.OutputIsTerminal = true;
        _cli.ErrorIsTerminal = true;

        var run = await _cli.RunAsync("doctor", "--json");

        Assert.DoesNotContain("\u001b[", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Envelope_is_identical_across_runs_apart_from_timing()
    {
        var first = Scrub.Envelope((await _cli.RunAsync("doctor", "--json")).Out);
        var second = Scrub.Envelope((await _cli.RunAsync("doctor", "--json")).Out);
        Assert.Equal(first, second);
    }
}
