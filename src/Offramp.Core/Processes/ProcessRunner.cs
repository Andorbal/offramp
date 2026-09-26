using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Offramp.Core.Processes;

/// <summary>A process to run: no shell, arguments passed as a list.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    public TimeSpan? Timeout { get; init; }

    public override string ToString() => FileName + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string arg) => arg.Contains(' ', StringComparison.Ordinal) ? "\"" + arg + "\"" : arg;
}

/// <summary>What happened when a process ran.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The executable could not be started (not installed or not on PATH).</summary>
    public bool NotFound { get; init; }

    public bool TimedOut { get; init; }

    public bool Succeeded => !NotFound && !TimedOut && ExitCode == 0;

    public static ProcessResult Missing(string fileName) =>
        new(-1, "", $"'{fileName}' could not be started.") { NotFound = true };
}

/// <summary>Runs external tools (dotnet, git). Replaced by a fake in tests.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>The real process runner.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Keep tool output stable and machine-readable.
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // MSBuild worker nodes that outlive a build would hold its output pipes open.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var (key, value) in spec.Environment)
        {
            if (value is null)
            {
                info.Environment.Remove(key);
            }
            else
            {
                info.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => Collect(stdout, outputClosed, e.Data);
        process.ErrorDataReceived += (_, e) => Collect(stderr, errorClosed, e.Data);
        process.Exited += (_, _) => exited.TrySetResult();

        try
        {
            if (!process.Start())
            {
                return ProcessResult.Missing(spec.FileName);
            }
        }
        catch (Win32Exception)
        {
            return ProcessResult.Missing(spec.FileName);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // The process exiting, not its pipes closing: see below.
            await exited.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, Text(stdout), Text(stderr)) { TimedOut = true };
        }

        // Drain the readers, for a bounded time: processes the child started (MSBuild nodes, the
        // compiler server) can inherit its output pipes and hold them open long after it exits.
        await Task.WhenAny(Task.WhenAll(outputClosed.Task, errorClosed.Task), Task.Delay(DrainTimeout, cancellationToken)).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, Text(stdout), Text(stderr));
    }

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static void Collect(StringBuilder output, TaskCompletionSource closed, string? line)
    {
        if (line is null)
        {
            closed.TrySetResult();
            return;
        }

        lock (output)
        {
            output.Append(line).Append('\n');
        }
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
