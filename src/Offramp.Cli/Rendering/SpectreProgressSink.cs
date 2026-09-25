using Offramp.Core.Progress;
using Spectre.Console;

namespace Offramp.Cli.Rendering;

/// <summary>
/// The live display on a terminal: one line per phase with a bar, the current
/// item, elapsed time, and an ETA. Spectre redraws at most 10 times per second.
/// </summary>
public sealed class SpectreProgressSink(ProgressContext context, IAnsiConsole console, bool verbose) : IProgressSink
{
    public IProgressPhase BeginPhase(string name, int index, int of)
    {
        var task = context.AddTask(Markup.Escape($"[{index}/{of}] {name}"), autoStart: true, maxValue: 1);
        task.IsIndeterminate = true;
        return new Phase(name, task, index, of);
    }

    public void Log(ProgressLevel level, string message)
    {
        if (level == ProgressLevel.Debug && !verbose)
        {
            return;
        }

        var style = level switch
        {
            ProgressLevel.Error => Theme.BlockingStyle,
            ProgressLevel.Warning => Theme.DecisionStyle,
            _ => Theme.DimStyle,
        };
        console.MarkupLine($"[{style}]{Markup.Escape(message)}[/]");
    }

    /// <summary>Runs <paramref name="work"/> under a live progress display on <paramref name="console"/>.</summary>
    public static Task<T> RunAsync<T>(IAnsiConsole console, bool verbose, Func<IProgressSink, Task<T>> work) =>
        console.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(
                new SpinnerColumn(),
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new RemainingTimeColumn())
            .StartAsync(ctx => work(new SpectreProgressSink(ctx, console, verbose)));

    private sealed class Phase(string name, ProgressTask task, int index, int of) : IProgressPhase
    {
        public string Name { get; } = name;

        public void Report(int current, int total, string? item = null)
        {
            task.IsIndeterminate = false;
            task.MaxValue = Math.Max(total, 1);
            task.Value = current;
            task.Description = Markup.Escape(item is null ? $"[{index}/{of}] {Name}" : $"[{index}/{of}] {Name}: {item}");
        }

        public void Dispose()
        {
            task.IsIndeterminate = false;
            task.Value = task.MaxValue;
            task.Description = Markup.Escape($"[{index}/{of}] {Name}");
            task.StopTask();
        }
    }
}
