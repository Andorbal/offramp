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

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.Append(e.Data).Append('\n'); };

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
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, stdout.ToString(), stderr.ToString()) { TimedOut = true };
        }

        // Drain the asynchronous readers.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
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
