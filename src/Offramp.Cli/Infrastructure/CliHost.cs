using System.Collections;
using Offramp.Core.Git;
using Offramp.Core.Processes;
using Offramp.Workspace.Environment;
using Spectre.Console;

namespace Offramp.Cli.Infrastructure;

/// <summary>
/// Everything the CLI touches outside itself: streams, terminal facts,
/// environment, clock, and external tools. Tests construct one with fakes.
/// </summary>
public sealed record CliHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    public TextReader In { get; init; } = TextReader.Null;

    public bool OutputIsTerminal { get; init; }

    public bool ErrorIsTerminal { get; init; }

    public bool InputIsTerminal { get; init; }

    /// <summary>Columns available when stdout is a terminal; otherwise output is laid out for this width.</summary>
    public int Width { get; init; } = 120;

    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public required string WorkingDirectory { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public IProcessRunner Processes { get; init; } = ProcessRunner.Instance;

    public IGitService? Git { get; init; }

    /// <summary>Where progress goes instead of stderr (<c>mcp serve</c> turns it into MCP notifications); null chooses by terminal and <c>--json</c>.</summary>
    public Offramp.Core.Progress.IProgressSink? Progress { get; init; }

    /// <summary>The HTTP client LLM calls go through; null uses a shared one.</summary>
    public HttpClient? LlmHttp { get; init; }

    public IReferenceAssembliesProbe ReferenceAssemblies { get; init; } = new ReferenceAssembliesProbe();

    /// <summary>Runtime identifier reported by doctor, for example <c>linux-x64</c>.</summary>
    public string Os { get; init; } = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    /// <summary>Creates the prompter for <c>init</c>'s interview; replaced in tests.</summary>
    public Func<IAnsiConsole, Commands.IInitPrompter>? InitPrompter { get; init; }

    /// <summary>Creates the prompter for <c>guide</c>'s session; replaced in tests.</summary>
    public Func<IAnsiConsole, Commands.IGuidePrompter>? GuidePrompter { get; init; }

    public IGitService GitService => Git ?? new GitService(Processes);

    public bool Interactive => InputIsTerminal && OutputIsTerminal;

    /// <summary>The real process: console streams and the process environment.</summary>
    public static CliHost FromProcess()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        var outputIsTerminal = !Console.IsOutputRedirected;
        if (outputIsTerminal || !Console.IsErrorRedirected)
        {
            // Spectre's detection switches a Windows console into VT mode as a side effect;
            // Offramp's own consoles then emit ANSI directly (see ConsoleFactory).
            _ = AnsiConsole.Profile.Capabilities.Ansi;
        }

        int width;
        try
        {
            width = outputIsTerminal ? Math.Max(Console.WindowWidth, 40) : 120;
        }
        catch (IOException)
        {
            width = 120;
        }

        return new CliHost
        {
            Out = Console.Out,
            Error = Console.Error,
            In = Console.In,
            OutputIsTerminal = outputIsTerminal,
            ErrorIsTerminal = !Console.IsErrorRedirected,
            InputIsTerminal = !Console.IsInputRedirected,
            Width = width,
            Environment = environment,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
    }
}
