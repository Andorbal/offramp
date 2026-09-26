using System.CommandLine;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Paths;
using Offramp.Ide;
using Offramp.Ide.Lsp;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp ide</c>: the editor integration's engine on the command line, and its language server (docs/spec/commands/ide.md).</summary>
public static class IdeCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var ide = new Command("ide", "What the VS Code, Visual Studio, and Rider extensions show: new code in .NET Framework projects checked for APIs modern .NET lacks, and types that could live in a portable counterpart.");
        ide.Subcommands.Add(IdeCheckCommand.Create(host, globals));

        // Language clients commonly pass --stdio (vscode-languageclient's stdio transport does); stdio is the only transport.
        var stdio = new Option<bool>("--stdio") { Description = "Accepted for language clients that pass it; the server always uses stdio.", Hidden = true };
        var serve = new Command("serve", "Serve the editor integration as a Language Server Protocol server over stdio, for the IDE extensions. Logs go to stderr.")
        {
            stdio,
        };
        serve.SetAction(async (_, ct) =>
        {
            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            return await OfframpLanguageServer.RunAsync(input, output, ServerOptions(host), ct);
        });
        HelpExamples.Add(serve, "offramp ide serve");
        ide.Subcommands.Add(serve);
        return ide;
    }

    /// <summary>The language server's host services: this CLI's processes, git, environment, and its command tree for <c>scan</c>.</summary>
    public static IdeServerOptions ServerOptions(CliHost host) => new()
    {
        Processes = host.Processes,
        Git = host.GitService,
        Environment = host.Environment,
        Time = host.Time,
        ServerVersion = OfframpVersion.Current,
        Log = host.Error,
        ScanAsync = async (root, progress, cancellationToken) =>
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var invocation = host with
            {
                Out = output,
                Error = error,
                OutputIsTerminal = false,
                ErrorIsTerminal = false,
                InputIsTerminal = false,
                WorkingDirectory = root,
                Progress = progress,
            };
            return await OfframpCli.RunAsync(["scan", "--json"], invocation, cancellationToken);
        },
    };
}

public sealed record IdeCheckOptions(IReadOnlyList<string> Files, string? Base, string? Scope);

/// <summary><c>offramp ide check</c>: the file reports an editor would show, as a JSON contract (and a gate for CI).</summary>
public sealed class IdeCheckCommand : ICommandHandler<IdeCheckOptions, IdeCheckResult>
{
    public string CommandPath => "ide check";

    public JsonTypeInfo<IdeCheckResult> ResultType => IdeJsonContext.Default.IdeCheckResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var files = new Option<string[]>("--file")
        {
            Description = "Files to report on (relative to the current directory); default: every C# file changed since the new-code base.",
            HelpName = "PATH",
            AllowMultipleArgumentsPerToken = true,
        };
        var baseRef = new Option<string?>("--base") { Description = "The git ref new code is compared with (its merge base with HEAD); default ide.newCode.base (auto: origin/HEAD, else HEAD).", HelpName = "REF" };
        var scope = new Option<string?>("--scope") { Description = "lines (default, from ide.newCode.scope): changed lines; files: every line of a changed file; all: everything.", HelpName = "SCOPE" };
        scope.AcceptOnlyFromAmong(NewCode.Lines, NewCode.Files, NewCode.All);
        var command = new Command("check", "Report what the editor would show: findings on new code, new types that could live in a portable counterpart, and files that could move there. Changes nothing.")
        {
            files, baseRef, scope,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new IdeCheckCommand(),
            new IdeCheckOptions(parse.GetValue(files) ?? [], parse.GetValue(baseRef), parse.GetValue(scope)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp ide check",
            "offramp ide check --file src/Foo/Pricing/PriceCalculator.cs --json",
            "offramp ide check --base origin/main --fail-on error");
        return command;
    }

    public async Task<CommandOutcome<IdeCheckResult>> ExecuteAsync(IdeCheckOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<IdeCheckResult>.Environment();
        }

        var files = options.Files
            .Select(f => RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(f, context.Host.WorkingDirectory)))
            .ToList();
        using var engine = await IdeEngine.CreateAsync(new IdeEngineOptions
        {
            RepositoryRoot = root,
            Model = model,
            WorkspacePath = context.WorkspacePath,
            Config = config,
            Settings = new IdeSettings { NewCode = new IdeNewCodeSettings { Base = options.Base, Scope = options.Scope } },
            Git = context.Host.GitService,
            Processes = context.Host.Processes,
            References = new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)),
            Time = context.Host.Time,
        }, cancellationToken);
        var result = await IdeCheck.RunAsync(engine, files.Count > 0 ? files : null, context.Diagnostics, cancellationToken);
        return CommandOutcome<IdeCheckResult>.Completed(result);
    }

    public void Render(IdeCheckResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        var headline = s.Files == 0
            ? "No changed C# files since " + Base(result.Base) + "."
            : string.Create(CultureInfo.InvariantCulture,
                $"{s.Files} file{Plural(s.Files)}, {s.NewLines} new line{Plural(s.NewLines)} since {Base(result.Base)}: {s.Findings} finding{Plural(s.Findings)} on new code, {s.NewMovableTypes} new type{Plural(s.NewMovableTypes)} that could live in a portable project.");
        output.Headline(headline, s.Findings > 0 ? Theme.BlockingStyle : s.NewMovableTypes > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);

        var findings = result.Files.SelectMany(f => f.Findings.Select(x => (File: f.File, Finding: x))).ToList();
        if (findings.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Where");
            table.AddColumn("Code");
            table.AddColumn("Finding");
            foreach (var (file, finding) in findings)
            {
                table.AddRow(
                    new Markup(Markup.Escape(string.Create(CultureInfo.InvariantCulture, $"{file}:{finding.Line}"))),
                    new Markup($"[{HumanOutput.Style(finding.Severity)}]{finding.Code}[/]"),
                    new Markup(Markup.Escape(finding.Message)));
            }

            output.Write(table);
        }

        var movable = result.Files.Where(f => f.Moves.Any(m => m.Movable)).ToList();
        if (movable.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("File");
            table.AddColumn("Types");
            table.AddColumn("Can move to");
            foreach (var file in movable)
            {
                table.AddRow(
                    new Markup(Markup.Escape(file.File)),
                    new Markup(Markup.Escape(string.Join(", ", file.Types.Select(t => IdeCheck.ShortName(t.Name) + (t.New ? " (new)" : ""))))),
                    new Markup(Markup.Escape(string.Join(", ", file.Moves.Where(m => m.Movable).Select(m => IdeCheck.ProjectName(m.To))))));
            }

            output.Write(table);
        }

        foreach (var project in result.Counterparts.Where(c => c.Counterparts.Count == 0 && result.Files.Any(f => f.Project == c.Project && f.NewLines.Count > 0)))
        {
            output.MarkupLine($"[{Theme.DimStyle}]{Markup.Escape(project.Project)} has no counterpart; add a projectMap entry to get move suggestions.[/]");
        }
    }

    private static string Base(IdeBase @base) => @base.Commit is null ? "(no git base)" : $"{@base.Ref} ({@base.Commit[..Math.Min(8, @base.Commit.Length)]})";

    private static string Plural(int count) => count == 1 ? "" : "s";
}
