using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary>Exit codes of <c>docs/spec/01-cli-conventions.md</c>, driven through the real runner with test handlers.</summary>
public sealed class ExitCodeTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    private sealed class TestHandler(Func<CommandContext, CancellationToken, Task<CommandOutcome<JsonObject>>> run)
        : ICommandHandler<object, JsonObject>
    {
        public string CommandPath => "test";

        public JsonTypeInfo<JsonObject> ResultType => OfframpCoreJsonContext.Default.JsonObject;

        public Task<CommandOutcome<JsonObject>> ExecuteAsync(object options, CommandContext context, CancellationToken cancellationToken) =>
            run(context, cancellationToken);

        public void Render(JsonObject result, CommandContext context, HumanOutput output) =>
            output.Headline("Test ran.", Theme.ReadyStyle);
    }

    private async Task<CliRun> RunAsync(
        Func<CommandContext, CancellationToken, Task<CommandOutcome<JsonObject>>> run,
        GlobalSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CommandRunner.RunAsync(
            new TestHandler(run), new object(), settings ?? new GlobalSettings { Json = true },
            _cli.Host(output, error), cancellationToken);
        return new CliRun(exit, output.ToString(), error.ToString());
    }

    private static Task<CommandOutcome<JsonObject>> Completed(CommandContext context, CancellationToken ct) =>
        Task.FromResult(CommandOutcome<JsonObject>.Completed(new JsonObject { ["ok"] = true }));

    [Fact]
    public async Task Success_is_0()
    {
        Assert.Equal(ExitCodes.Success, (await RunAsync(Completed)).ExitCode);
    }

    [Fact]
    public async Task Findings_at_or_above_fail_on_are_1()
    {
        Task<CommandOutcome<JsonObject>> WithWarning(CommandContext context, CancellationToken ct)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0014, "warning finding");
            return Completed(context, ct);
        }

        Assert.Equal(ExitCodes.Success, (await RunAsync(WithWarning)).ExitCode);
        Assert.Equal(ExitCodes.Findings, (await RunAsync(WithWarning, new GlobalSettings { Json = true, FailOn = FailOn.Warning })).ExitCode);
    }

    [Fact]
    public async Task Invalid_configuration_is_a_usage_error_2_for_commands_that_need_it()
    {
        _cli.Repo.Write("offramp.yml", "target: ten\n");

        var run = await RunAsync(Completed);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        var envelope = JsonNode.Parse(run.Out)!;
        Assert.Null(envelope["result"]);
        Assert.Contains(envelope["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR0053");
        SchemaAssert.Valid("envelope", run.Out);
    }

    [Fact]
    public async Task Invalid_configuration_in_human_mode_explains_on_stderr()
    {
        _cli.Repo.Write("offramp.yml", "target: ten\n");

        var run = await RunAsync(Completed, new GlobalSettings());

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal("", run.Out);
        Assert.Contains("did not run", run.Error, StringComparison.Ordinal);
        Assert.Contains("OFR0053", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_failures_are_3()
    {
        var run = await RunAsync((context, _) =>
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0001, "no model");
            return Task.FromResult(CommandOutcome<JsonObject>.Environment());
        });

        Assert.Equal(ExitCodes.Environment, run.ExitCode);
    }

    [Fact]
    [ProducesDiagnostic("OFR0099")]
    public async Task Unexpected_exceptions_are_internal_errors_with_exit_3()
    {
        var run = await RunAsync((_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(ExitCodes.Environment, run.ExitCode);
        var diagnostic = JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray().Single(d => d!["code"]!.GetValue<string>() == "OFR0099")!;
        Assert.Equal("OFR0099", diagnostic["code"]!.GetValue<string>());
        Assert.Contains("boom", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_results_are_4()
    {
        var run = await RunAsync((_, _) => Task.FromResult(new CommandOutcome<JsonObject>(new JsonObject(), OutcomeKind.Partial)));
        Assert.Equal(ExitCodes.Partial, run.ExitCode);
    }

    [Fact]
    public async Task Interruption_is_130()
    {
        using var cts = new CancellationTokenSource();
        var run = await RunAsync(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return CommandOutcome<JsonObject>.Completed(new JsonObject());
        }, cancellationToken: cts.Token);

        Assert.Equal(ExitCodes.Interrupted, run.ExitCode);
    }

    [Theory]
    [InlineData(OutcomeKind.UsageFailure, true, 2)]
    [InlineData(OutcomeKind.EnvironmentFailure, true, 3)]
    [InlineData(OutcomeKind.Partial, true, 4)]
    [InlineData(OutcomeKind.Completed, true, 1)]
    [InlineData(OutcomeKind.Completed, false, 0)]
    public void Exit_code_precedence(OutcomeKind kind, bool withError, int expected)
    {
        IReadOnlyList<Diagnostic> diagnostics = withError
            ? [new Diagnostic { Code = "OFR0010", Severity = Severity.Error, Message = "x", Help = "h" }]
            : [];
        Assert.Equal(expected, CommandRunner.ExitCode(kind, diagnostics, FailOn.Error));
    }
}
