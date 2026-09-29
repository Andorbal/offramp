using Offramp.Core.Progress;

namespace Offramp.Workspace.Scanning;

/// <summary>
/// The analysis build's heartbeat: counts the projects the build finished from its minimal-verbosity output,
/// where MSBuild writes one <c>Name -&gt; output path</c> line for each project it built (once per target
/// framework), and reports them through the build's progress phase (docs/spec/01-cli-conventions.md).
/// </summary>
internal sealed class BuildProgress(IProgressPhase phase, int projects)
{
    private readonly HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Takes one line of the build's standard output.</summary>
    public void OnLine(string line)
    {
        if (FinishedProject(line) is not { } name)
        {
            return;
        }

        int finished;
        lock (_gate)
        {
            if (!_finished.Add(name))
            {
                return;
            }

            finished = _finished.Count;
        }

        phase.Report(finished, Math.Max(projects, finished), name);
    }

    /// <summary>The project a <c>Name -&gt; path</c> line names, or null for any other line (errors, warnings, restore).</summary>
    internal static string? FinishedProject(string line)
    {
        var arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow <= 0 || line.Contains(": error ", StringComparison.Ordinal) || line.Contains(": warning ", StringComparison.Ordinal))
        {
            return null;
        }

        var name = line[..arrow].Trim();
        return name.Length == 0 || name.IndexOfAny([':', '/', '\\', '[', '(']) >= 0 ? null : name;
    }
}
