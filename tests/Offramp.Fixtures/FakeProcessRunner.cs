using Offramp.Core.Processes;

namespace Offramp.Fixtures;

/// <summary>
/// Answers process invocations from registered handlers; anything unregistered
/// behaves like a missing executable. Records every call.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(Func<ProcessSpec, bool> Matches, Func<ProcessSpec, ProcessResult> Handler)> _handlers = [];
    private readonly List<ProcessSpec> _calls = [];

    public IReadOnlyList<ProcessSpec> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Registers a handler for <paramref name="fileName"/> whose arguments start with <paramref name="argumentPrefix"/>. Later registrations win.</summary>
    public FakeProcessRunner On(string fileName, string[] argumentPrefix, Func<ProcessSpec, ProcessResult> handler) =>
        On(spec => spec.FileName == fileName && spec.Arguments.Take(argumentPrefix.Length).SequenceEqual(argumentPrefix), handler);

    /// <summary>Registers a handler for any invocation <paramref name="matches"/> accepts. Later registrations win.</summary>
    public FakeProcessRunner On(Func<ProcessSpec, bool> matches, Func<ProcessSpec, ProcessResult> handler)
    {
        _handlers.Insert(0, (matches, handler));
        return this;
    }

    public FakeProcessRunner On(string fileName, string[] argumentPrefix, int exitCode, string stdout, string stderr = "") =>
        On(fileName, argumentPrefix, _ => new ProcessResult(exitCode, stdout, stderr));

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_calls)
        {
            _calls.Add(spec);
        }

        foreach (var (matches, handler) in _handlers)
        {
            if (matches(spec))
            {
                return Task.FromResult(handler(spec));
            }
        }

        return Task.FromResult(ProcessResult.Missing(spec.FileName));
    }
}
