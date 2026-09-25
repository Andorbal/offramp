using Offramp.Core.Processes;

namespace Offramp.Fixtures;

/// <summary>
/// Answers process invocations from registered handlers; anything unregistered
/// behaves like a missing executable. Records every call.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string FileName, string[] Prefix, Func<ProcessSpec, ProcessResult> Handler)> _handlers = [];
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
    public FakeProcessRunner On(string fileName, string[] argumentPrefix, Func<ProcessSpec, ProcessResult> handler)
    {
        _handlers.Insert(0, (fileName, argumentPrefix, handler));
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

        foreach (var (fileName, prefix, handler) in _handlers)
        {
            if (fileName == spec.FileName && spec.Arguments.Take(prefix.Length).SequenceEqual(prefix))
            {
                return Task.FromResult(handler(spec));
            }
        }

        return Task.FromResult(ProcessResult.Missing(spec.FileName));
    }
}
