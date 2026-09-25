using System.Text.Json;
using Offramp.Core.Json;

namespace Offramp.Core.Progress;

/// <summary>
/// Writes the NDJSON progress protocol to a writer (stderr in the CLI, a buffer in
/// tests). Progress events are throttled to 10 per second per phase; the final
/// update of a phase (<c>current == total</c>) is always written.
/// </summary>
public sealed class NdjsonProgressSink : IProgressSink
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly TextWriter _writer;
    private readonly TimeProvider _time;
    private readonly bool _verbose;
    private readonly object _gate = new();

    public NdjsonProgressSink(TextWriter writer, TimeProvider time, bool verbose = false)
    {
        _writer = writer;
        _time = time;
        _verbose = verbose;
    }

    public IProgressPhase BeginPhase(string name, int index, int of)
    {
        Write(new PhaseEvent(name, index, of));
        return new Phase(this, name, _time.GetTimestamp());
    }

    public void Log(ProgressLevel level, string message)
    {
        if (level == ProgressLevel.Debug && !_verbose)
        {
            return;
        }

        Write(new LogEvent(level, message));
    }

    private void Write(ProgressEventBase evt)
    {
        var line = JsonSerializer.Serialize(evt, OfframpCoreJsonContext.Compact.ProgressEventBase);
        lock (_gate)
        {
            _writer.Write(line);
            _writer.Write('\n');
            _writer.Flush();
        }
    }

    private sealed class Phase(NdjsonProgressSink owner, string name, long started) : IProgressPhase
    {
        private long _lastReport = long.MinValue;
        private bool _disposed;

        public string Name { get; } = name;

        public void Report(int current, int total, string? item = null)
        {
            var now = owner._time.GetTimestamp();
            var final = current >= total;
            if (!final && _lastReport != long.MinValue
                && owner._time.GetElapsedTime(_lastReport, now) < MinInterval)
            {
                return;
            }

            _lastReport = now;
            owner.Write(new ProgressEvent(Name, current, total, item));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var elapsed = owner._time.GetElapsedTime(started);
            owner.Write(new DoneEvent(Name, (long)elapsed.TotalMilliseconds));
        }
    }
}
