using System.Text;
using System.Text.Json.Nodes;
using Offramp.Cli.Rendering;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Output;
using Offramp.Core.Paths;
using Offramp.Core.Progress;

namespace Offramp.Cli.Infrastructure;

/// <summary>
/// The one execution path every command takes: locate the repository, load
/// configuration, pick a progress sink, run the handler, then write the JSON
/// envelope or the human rendering of the same result, and map to an exit code.
/// </summary>
public static class CommandRunner
{
    public static async Task<int> RunAsync<TOptions, TResult>(
        ICommandHandler<TOptions, TResult> handler,
        TOptions options,
        GlobalSettings settings,
        CliHost host,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var startedAt = host.Time.GetUtcNow();
        var startTimestamp = host.Time.GetTimestamp();

        var repository = await RepositoryLocator.LocateAsync(host.WorkingDirectory, host.GitService, cancellationToken);
        var config = ConfigLoader.Load(new ConfigSources
        {
            RepositoryRoot = repository.Path,
            ExplicitPath = ResolveConfigPath(settings, host),
            Environment = host.Environment,
            CommandLine = CommandLineOverlay(settings, repository, host),
        });

        var diagnostics = new DiagnosticBag(config.IsValid ? config.Config.SeverityOverrides() : null);
        if (handler.IncludesConfigDiagnostics || (!config.IsValid && !handler.RunsWithInvalidConfig))
        {
            diagnostics.AddRange(config.Diagnostics);
        }

        var workspacePath = settings.Workspace is not null
            ? Path.GetFullPath(settings.Workspace, host.WorkingDirectory)
            : Path.Combine(repository.Path, config.Config.Paths.State, "workspace.json");

        var context = new CommandContext
        {
            Host = host,
            Settings = settings,
            Repository = repository,
            Config = config,
            Diagnostics = diagnostics,
            Progress = NullProgressSink.Instance,
            WorkspacePath = workspacePath,
        };

        CommandOutcome<TResult> outcome;
        if (!config.IsValid && !handler.RunsWithInvalidConfig)
        {
            outcome = CommandOutcome<TResult>.Usage();
        }
        else
        {
            try
            {
                outcome = await ExecuteWithProgressAsync(handler, options, context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await host.Error.WriteLineAsync("Interrupted.");
                return ExitCodes.Interrupted;
            }
            catch (Exception ex)
            {
                diagnostics.Report(DiagnosticCatalog.OFR0099,
                    $"Unexpected {ex.GetType().Name}: {ex.Message}",
                    data: [KeyValuePair.Create<string, JsonNode?>("exception", ex.GetType().FullName)]);
                if (settings.Verbose)
                {
                    await host.Error.WriteLineAsync(ex.ToString());
                }

                outcome = CommandOutcome<TResult>.Environment();
            }
        }

        var duration = host.Time.GetElapsedTime(startTimestamp);
        var header = new EnvelopeHeader
        {
            Version = OfframpVersion.Current,
            Command = handler.CommandPath,
            Target = config.Config.TargetFramework,
            RepositoryRoot = repository.Path,
            Solution = config.Config.Solution ?? ModelSolution(workspacePath),
            WorkspaceHash = WorkspaceHash(workspacePath),
            StartedAt = EnvelopeHeader.FormatTimestamp(startedAt),
            DurationMs = (long)duration.TotalMilliseconds,
            EffectiveConfig = config.Effective,
        };

        var sorted = diagnostics.ToSortedList();
        var envelope = EnvelopeWriter.Write(header, outcome.Result, handler.ResultType, sorted);
        var envelopeSettings = handler.WritesOwnOutput ? settings with { Out = null } : settings;
        if (settings.Json)
        {
            await WritePrimaryAsync(envelopeSettings, host, envelope, host.Out);
        }
        else
        {
            RenderHuman(handler, outcome, context, sorted, diagnostics.Summary());
            if (envelopeSettings.Out is not null)
            {
                await WritePrimaryAsync(envelopeSettings, host, envelope, host.Out);
            }
        }

        return ExitCode(outcome.Kind, sorted, settings.FailOn);
    }

    /// <summary>
    /// Exit code precedence: usage (2), environment (3), partial (4), findings
    /// at or above <c>--fail-on</c> (1), success (0).
    /// </summary>
    public static int ExitCode(OutcomeKind kind, IReadOnlyList<Diagnostic> diagnostics, FailOn failOn) => kind switch
    {
        OutcomeKind.UsageFailure => ExitCodes.Usage,
        OutcomeKind.EnvironmentFailure => ExitCodes.Environment,
        OutcomeKind.Partial => ExitCodes.Partial,
        _ => diagnostics.Any(d => d.Severity.Meets(failOn)) ? ExitCodes.Findings : ExitCodes.Success,
    };

    /// <summary>The cache for this run: <c>&lt;state&gt;/cache</c>, or nothing with <c>--no-cache</c>.</summary>
    public static ICache Cache(CommandContext context) => context.Settings.NoCache
        ? NullCache.Instance
        : new FileCache(Path.Combine(context.Repository.Path, context.Config.Config.Paths.State, "cache"));

    private static async Task<CommandOutcome<TResult>> ExecuteWithProgressAsync<TOptions, TResult>(
        ICommandHandler<TOptions, TResult> handler, TOptions options, CommandContext context, CancellationToken cancellationToken)
        where TResult : class
    {
        var settings = context.Settings;
        var host = context.Host;
        if (settings.Quiet)
        {
            return await handler.ExecuteAsync(options, context, cancellationToken);
        }

        if (settings.Json)
        {
            var ndjson = new NdjsonProgressSink(host.Error, host.Time, settings.Verbose);
            return await handler.ExecuteAsync(options, context with { Progress = ndjson }, cancellationToken);
        }

        if (host.ErrorIsTerminal && handler is not IInteractiveCommand)
        {
            var errorConsole = ConsoleFactory.Create(host, host.Error, isTerminal: true);
            return await SpectreProgressSink.RunAsync(errorConsole, settings.Verbose,
                sink => handler.ExecuteAsync(options, context with { Progress = sink }, cancellationToken));
        }

        var plain = new PlainProgressSink(host.Error, settings.Verbose);
        return await handler.ExecuteAsync(options, context with { Progress = plain }, cancellationToken);
    }

    private static void RenderHuman<TOptions, TResult>(
        ICommandHandler<TOptions, TResult> handler,
        CommandOutcome<TResult> outcome,
        CommandContext context,
        IReadOnlyList<Diagnostic> diagnostics,
        DiagnosticSummary summary)
        where TResult : class
    {
        var host = context.Host;
        if (outcome.Result is not null && handler is IRawOutput<TResult> raw && raw.RawOutput(outcome.Result, context) is { } text)
        {
            // The primary output is a document for another tool (dot, mermaid, a filter): stdout gets
            // exactly that, so it can be piped; diagnostics go to stderr.
            host.Out.Write(text);
            if (diagnostics.Count > 0)
            {
                var side = new HumanOutput(ConsoleFactory.Create(host, host.Error, host.ErrorIsTerminal));
                side.Diagnostics(diagnostics);
                side.Summary(summary, null);
            }

            return;
        }

        if (outcome.Result is not null)
        {
            var output = new HumanOutput(ConsoleFactory.Create(host, host.Out, host.OutputIsTerminal));
            handler.Render(outcome.Result, context, output);
            if (handler is not IRendersOwnDiagnostics)
            {
                output.Diagnostics(diagnostics);
            }

            output.Summary(summary, handler is INextStep<TResult> next ? next.NextStep(outcome.Result, context) : null);
            return;
        }

        // No result: the reason goes to stderr so redirected stdout stays clean.
        var error = new HumanOutput(ConsoleFactory.Create(host, host.Error, host.ErrorIsTerminal));
        var headline = outcome.Kind switch
        {
            OutcomeKind.UsageFailure => $"offramp {handler.CommandPath} did not run: fix the configuration or options below.",
            OutcomeKind.EnvironmentFailure => $"offramp {handler.CommandPath} could not run.",
            _ => $"offramp {handler.CommandPath} produced no result.",
        };
        error.Headline(headline, Theme.BlockingStyle);
        error.Diagnostics(diagnostics);
        error.Summary(summary, null);
    }

    private static async Task WritePrimaryAsync(GlobalSettings settings, CliHost host, string content, TextWriter fallback)
    {
        if (settings.Out is null)
        {
            await fallback.WriteAsync(content);
            await fallback.FlushAsync();
            return;
        }

        var path = Path.GetFullPath(settings.Out, host.WorkingDirectory);
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
    }

    private static string? ResolveConfigPath(GlobalSettings settings, CliHost host)
    {
        var path = settings.Config;
        if (path is null && host.Environment.TryGetValue(ConfigLoader.ConfigPathVariable, out var fromEnvironment)
            && !string.IsNullOrWhiteSpace(fromEnvironment))
        {
            path = fromEnvironment;
        }

        return path is null ? null : Path.GetFullPath(path, host.WorkingDirectory);
    }

    /// <summary>Flags that map to configuration keys; applied last, so they win.</summary>
    private static JsonObject CommandLineOverlay(GlobalSettings settings, RepositoryRoot repository, CliHost host)
    {
        var overlay = new JsonObject();
        if (settings.Target is { } target)
        {
            overlay["target"] = target;
        }

        if (settings.Solution is { } solution)
        {
            var absolute = Path.GetFullPath(solution, host.WorkingDirectory);
            overlay["solution"] = RepoPaths.ToRepositoryRelative(repository.Path, absolute);
        }

        if (settings.Llm is { } llm)
        {
            overlay["llm"] = new JsonObject { ["enabled"] = llm };
        }

        return overlay;
    }

    /// <summary>The hash the envelope reports for a workspace model file, or null when there is none.</summary>
    internal static string? WorkspaceHash(string workspacePath) =>
        File.Exists(workspacePath) ? "sha256:" + ContentHash.Sha256File(workspacePath) : null;

    /// <summary>The solution the workspace model was built from, when none is configured (auto-detected by scan).</summary>
    private static string? ModelSolution(string workspacePath)
    {
        if (!File.Exists(workspacePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(workspacePath);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("solution", out var solution) && solution.ValueKind == System.Text.Json.JsonValueKind.String
                ? solution.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Commands whose human output can be a document for another tool (DOT, Mermaid, a
/// solution filter): when <see cref="RawOutput"/> returns text, stdout gets exactly that
/// and diagnostics go to stderr.
/// </summary>
public interface IRawOutput<in TResult>
{
    string? RawOutput(TResult result, CommandContext context);
}

/// <summary>Commands that prompt; they never run under the live progress display.</summary>
public interface IInteractiveCommand
{
}

/// <summary>Commands whose rendering already shows every diagnostic (the generic list is skipped).</summary>
public interface IRendersOwnDiagnostics
{
}

/// <summary>Commands that suggest the next command in the human summary.</summary>
public interface INextStep<in TResult>
{
    string? NextStep(TResult result, CommandContext context);
}
