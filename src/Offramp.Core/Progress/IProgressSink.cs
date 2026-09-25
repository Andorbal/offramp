using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Core.Progress;

/// <summary>
/// Where long operations report progress. Libraries never write to the console;
/// the CLI picks a sink (Spectre display on a TTY, NDJSON on stderr with
/// <c>--json</c>, plain lines when stderr is redirected, nothing with <c>--quiet</c>).
/// See <c>docs/spec/01-cli-conventions.md#progress-protocol-stderr</c>.
/// </summary>
public interface IProgressSink
{
    /// <summary>Starts phase <paramref name="index"/> of <paramref name="of"/>. Dispose the result to finish it.</summary>
    IProgressPhase BeginPhase(string name, int index, int of);

    void Log(ProgressLevel level, string message);
}

/// <summary>A running phase. Disposing it emits the <c>done</c> event.</summary>
public interface IProgressPhase : IDisposable
{
    string Name { get; }

    /// <summary>Reports determinate progress. Sinks throttle to at most 10 updates per second.</summary>
    void Report(int current, int total, string? item = null);
}

[JsonConverter(typeof(CamelCaseEnumConverter<ProgressLevel>))]
public enum ProgressLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Discards everything. Used with <c>--quiet</c> and in library tests.</summary>
public sealed class NullProgressSink : IProgressSink
{
    public static readonly NullProgressSink Instance = new();

    public IProgressPhase BeginPhase(string name, int index, int of) => new NullPhase(name);

    public void Log(ProgressLevel level, string message)
    {
    }

    private sealed class NullPhase(string name) : IProgressPhase
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
