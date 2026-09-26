using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Offramp.Cli.Commands;
using Offramp.Cli.Infrastructure;

namespace Offramp.Cli;

/// <summary>Builds the command tree and runs one invocation.</summary>
public static class OfframpCli
{
    public static RootCommand BuildRoot(CliHost host)
    {
        var globals = new GlobalOptions();
        var root = new RootCommand(
            "Offramp: the off-ramp from .NET Framework. Deterministic analysis, pure moves, and scaffolding for incremental migrations to modern .NET.");
        globals.AddTo(root);

        root.Subcommands.Add(DoctorCommand.Create(host, globals));
        root.Subcommands.Add(InitCommand.Create(host, globals));
        root.Subcommands.Add(ScanCommand.Create(host, globals));
        root.Subcommands.Add(SliceCommand.Create(host, globals));
        root.Subcommands.Add(GraphCommand.Create(host, globals));
        root.Subcommands.Add(DepsCommands.Create(host, globals));
        root.Subcommands.Add(ReportCommand.Create(host, globals));
        root.Subcommands.Add(PlanCommand.Create(host, globals));
        root.Subcommands.Add(VerifyCommand.Create(host, globals));
        root.Subcommands.Add(MoveCommands.Create(host, globals));
        root.Subcommands.Add(ForwardersCommand.Create(host, globals));
        root.Subcommands.Add(RedirectsCommands.Create(host, globals));
        root.Subcommands.Add(AuditCommands.Create(host, globals));
        root.Subcommands.Add(IfdefCommands.Create(host, globals));
        root.Subcommands.Add(SeamsCommand.Create(host, globals));
        root.Subcommands.Add(ExtractCommands.Create(host, globals));
        root.Subcommands.Add(RemoteCommand.Create(host, globals));
        root.Subcommands.Add(ServiceCommand.Create(host, globals));
        root.Subcommands.Add(CodemodCommands.Create(host, globals));
        root.Subcommands.Add(CsprojCommands.Create(host, globals));
        root.Subcommands.Add(ConfigCommands.Create(host, globals));
        root.Subcommands.Add(WebCommands.Create(host, globals));
        globals.AddValidators(root);

        var version = root.Options.OfType<VersionOption>().Single();
        version.Action = new PrintVersionAction(host);
        HelpExamples.Install(root, host.Width);
        var help = root.Options.OfType<HelpOption>().Single();
        root.SetAction(parse => ((SynchronousCommandLineAction)help.Action!).Invoke(parse));

        HelpExamples.Add(root,
            "offramp doctor",
            "offramp init --defaults",
            "offramp doctor --json | jq .result.summary");
        return root;
    }

    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        var root = BuildRoot(host);
        var parse = root.Parse(args);
        if (parse.Action is ParseErrorAction)
        {
            foreach (var error in parse.Errors)
            {
                await host.Error.WriteLineAsync("error: " + error.Message);
            }

            var command = parse.CommandResult.Command;
            var path = command == root ? "offramp" : "offramp " + CommandPath(command);
            await host.Error.WriteLineAsync($"Run '{path} --help' for usage.");
            return ExitCodes.Usage;
        }

        var configuration = new InvocationConfiguration
        {
            Output = host.Out,
            Error = host.Error,
            EnableDefaultExceptionHandler = false,
        };
        try
        {
            return await parse.InvokeAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await host.Error.WriteLineAsync("Interrupted.");
            return ExitCodes.Interrupted;
        }
        catch (Exception ex)
        {
            // Last resort: a bug outside a command handler. Handlers report OFR0099 in the envelope.
            await host.Error.WriteLineAsync($"error OFR0099: internal error: {ex.GetType().Name}: {ex.Message}");
            await host.Error.WriteLineAsync("Please report it at https://github.com/Andorbal/offramp/issues with the output of --verbose.");
            if (args.Contains("--verbose") || args.Contains("-v"))
            {
                await host.Error.WriteLineAsync(ex.ToString());
            }

            return ExitCodes.Environment;
        }
    }

    private static string CommandPath(Command command)
    {
        var names = new List<string>();
        for (Symbol? current = command; current is Command c && current is not RootCommand; current = c.Parents.FirstOrDefault())
        {
            names.Add(c.Name);
        }

        names.Reverse();
        return string.Join(' ', names);
    }

    private sealed class PrintVersionAction(CliHost host) : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            host.Out.WriteLine(OfframpVersion.Current);
            return ExitCodes.Success;
        }
    }
}
