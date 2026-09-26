using System.Text.Json.Serialization.Metadata;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using Offramp.Cli.Rendering;

namespace Offramp.Cli.Infrastructure;

/// <summary>How a command ended, beyond its diagnostics.</summary>
public enum OutcomeKind
{
    /// <summary>Ran to completion; the exit code follows the diagnostics and <c>--fail-on</c>.</summary>
    Completed,

    /// <summary>Some operations applied, some skipped (exit 4).</summary>
    Partial,

    /// <summary>Could not run because the environment lacks something (exit 3).</summary>
    EnvironmentFailure,

    /// <summary>Could not run because of how it was invoked (exit 2).</summary>
    UsageFailure,
}

public sealed record CommandOutcome<TResult>(TResult? Result, OutcomeKind Kind = OutcomeKind.Completed)
    where TResult : class
{
    public static CommandOutcome<TResult> Completed(TResult result) => new(result);

    public static CommandOutcome<TResult> Environment() => new(null, OutcomeKind.EnvironmentFailure);

    public static CommandOutcome<TResult> Usage() => new(null, OutcomeKind.UsageFailure);
}

/// <summary>What a command handler can see: the host, global settings, configuration, and where to report.</summary>
public sealed record CommandContext
{
    public required CliHost Host { get; init; }

    public required GlobalSettings Settings { get; init; }

    public required RepositoryRoot Repository { get; init; }

    public required ConfigLoadResult Config { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public required IProgressSink Progress { get; init; }

    /// <summary>Absolute path of the workspace model (<c>--workspace</c> or <c>&lt;state&gt;/workspace.json</c>).</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>True when the command may prompt (a terminal, no <c>--json</c>).</summary>
    public bool Interactive => Host.Interactive && !Settings.Json;
}

/// <summary>
/// One command. The handler binds options, calls a library, and returns the
/// result; <see cref="Render"/> is the human view of the same result.
/// </summary>
public interface ICommandHandler<TOptions, TResult>
    where TResult : class
{
    /// <summary>The command as typed, for example <c>deps audit</c>.</summary>
    string CommandPath { get; }

    JsonTypeInfo<TResult> ResultType { get; }

    /// <summary>True for commands that report on configuration problems instead of refusing to run (doctor).</summary>
    bool RunsWithInvalidConfig => false;

    /// <summary>
    /// False for commands that decide themselves whether configuration diagnostics
    /// apply (init replacing the file makes the old file's problems moot).
    /// </summary>
    bool IncludesConfigDiagnostics => true;

    /// <summary>
    /// True for commands whose primary output with <c>--out</c> is an artifact they
    /// write themselves (a solution filter, an HTML page); the runner then writes the
    /// envelope only with <c>--json</c>, to stdout.
    /// </summary>
    bool WritesOwnOutput => false;

    Task<CommandOutcome<TResult>> ExecuteAsync(TOptions options, CommandContext context, CancellationToken cancellationToken);

    /// <summary>Human rendering: the headline first, then tables.</summary>
    void Render(TResult result, CommandContext context, HumanOutput output);
}
