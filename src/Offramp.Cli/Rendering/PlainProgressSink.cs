using Offramp.Core.Progress;

namespace Offramp.Cli.Rendering;

/// <summary>stderr is not a terminal and <c>--json</c> is off: one plain line per phase.</summary>
public sealed class PlainProgressSink(TextWriter writer, bool verbose) : IProgressSink
{
    private readonly object _gate = new();

    public IProgressPhase BeginPhase(string name, int index, int of)
    {
        WriteLine($"[{index}/{of}] {name}");
        return new Phase(name);
    }

    public void Log(ProgressLevel level, string message)
    {
        if (level == ProgressLevel.Debug && !verbose)
        {
            return;
        }

        var prefix = level switch
        {
            ProgressLevel.Error => "error: ",
            ProgressLevel.Warning => "warning: ",
            ProgressLevel.Debug => "debug: ",
            _ => "",
        };
        WriteLine(prefix + message);
    }

    private void WriteLine(string text)
    {
        lock (_gate)
        {
            writer.Write(text);
            writer.Write('\n');
            writer.Flush();
        }
    }

    private sealed class Phase(string name) : IProgressPhase
    {
        public string Name { get; } = name;

        public void Report(int current, int total, string? item = null)
        {
        }

        public void Dispose()
        {
        }
    }
}
