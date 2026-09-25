using Offramp.Cli.Commands;
using Offramp.Cli.Infrastructure;
using Offramp.Fixtures;
using Spectre.Console;

namespace Offramp.Cli.Tests;

public sealed record CliRun(int ExitCode, string Out, string Error)
{
    public override string ToString() => $"exit {ExitCode}\n--- stdout\n{Out}\n--- stderr\n{Error}";
}

/// <summary>Runs the CLI in-process against a scratch repository and a fake machine.</summary>
public sealed class CliHarness : IDisposable
{
    public CliHarness(bool gitRepository = true)
    {
        Repo = new ScratchDirectory("cli");
        Machine = new FakeMachine { RepositoryRoot = gitRepository ? Repo.Path : null };
    }

    public ScratchDirectory Repo { get; }

    public FakeMachine Machine { get; }

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    public bool OutputIsTerminal { get; set; }

    public bool ErrorIsTerminal { get; set; }

    public bool InputIsTerminal { get; set; }

    public FakeTimeProvider Time { get; } = new();

    public Func<IAnsiConsole, IInitPrompter>? InitPrompter { get; set; }

    public CliHost Host(TextWriter output, TextWriter error) => new()
    {
        Out = output,
        Error = error,
        OutputIsTerminal = OutputIsTerminal,
        ErrorIsTerminal = ErrorIsTerminal,
        InputIsTerminal = InputIsTerminal,
        Width = 120,
        Environment = Environment,
        WorkingDirectory = Repo.Path,
        Time = Time,
        Processes = Machine.CreateRunner(),
        ReferenceAssemblies = Machine.CreateReferenceAssembliesProbe(),
        InitPrompter = InitPrompter,
        Os = "linux-x64",
    };

    public async Task<CliRun> RunAsync(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var exit = await OfframpCli.RunAsync(args, Host(output, error), TestContext.Current.CancellationToken);
        return new CliRun(exit, output.ToString(), error.ToString());
    }

    public void Dispose() => Repo.Dispose();
}
