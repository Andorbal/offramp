using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis.TestCode;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary>The <c>move</c> command group (docs/spec/commands/move.md).</summary>
public static class MoveCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var move = new Command("move", "Move code between projects without changing a byte of it: renames are staged with git mv, project edits left for review.");
        move.Subcommands.Add(MovePlanCommand.Create(host, globals));
        move.Subcommands.Add(MoveApplyCommand.Create(host, globals));
        move.Subcommands.Add(MoveTestsCommand.Create(host, globals));
        move.Subcommands.Add(MoveRollbackCommand.Create(host, globals));
        move.Subcommands.Add(MoveExtractCommand.Create(host, globals));
        return move;
    }
}

public sealed record MoveTestsOptions(string Project, string? To, bool Create, TestConfidence? IncludeHelpers, bool PrunePackages, bool Apply, string? Verify)
{
    /// <summary>True when <c>--include-helpers</c> was given; otherwise <c>move.tests.helperMinConfidence</c> applies.</summary>
    public bool HelpersGiven { get; init; }
}

/// <summary><c>offramp move tests</c>: test code out of a production project, purely.</summary>
public sealed class MoveTestsCommand : ICommandHandler<MoveTestsOptions, MoveTestsResult>
{
    public string CommandPath => "move tests";

    public JsonTypeInfo<MoveTestsResult> ResultType => RefactoringJsonContext.Default.MoveTestsResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The production project holding tests (path or name).", HelpName = "PROJECT", Required = true };
        var to = new Option<string?>("--to") { Description = "The test project to move them to (default: the project named <Name>" + ".Tests).", HelpName = "PROJECT" };
        var create = new Option<bool>("--create") { Description = "Create <Name>.Tests next to the project when no test project exists." };
        var helpers = new Option<string?>("--include-helpers") { Description = "Move helpers down to this confidence: high (default, from move.tests.helperMinConfidence), medium, low, or none.", HelpName = "LEVEL" };
        helpers.AcceptOnlyFromAmong("high", "medium", "low", "none");
        var prune = new Option<bool>("--prune-packages") { Description = "Remove the project's test-framework packages when nothing left in it uses them." };
        var apply = new Option<bool>("--apply") { Description = "Perform the move. Without it, show the plan and the project-file diff (dry run)." };
        var verify = new Option<string?>("--verify") { Description = "Verify after applying: end (default, from move.verify) or none.", HelpName = "WHEN" };
        verify.AcceptOnlyFromAmong("none", "end", "per-project");
        var command = new Command("tests", "Find test code in a production project and move it, byte for byte, to its test project.")
        {
            project, to, create, helpers, prune, apply, verify,
        };
        command.SetAction((parse, ct) =>
        {
            var settings = globals.Bind(parse);
            var level = parse.GetValue(helpers);
            return CommandRunner.RunAsync(
                new MoveTestsCommand(),
                new MoveTestsOptions(
                    parse.GetValue(project)!, parse.GetValue(to), parse.GetValue(create),
                    level is null ? null : level == "none" ? null : Enum.Parse<TestConfidence>(level, ignoreCase: true),
                    parse.GetValue(prune), parse.GetValue(apply), parse.GetValue(verify))
                {
                    HelpersGiven = level is not null,
                },
                settings, host, ct);
        });
        HelpExamples.Add(command,
            "offramp move tests --project Foo",
            "offramp move tests --project Bar --create --apply",
            "offramp move tests --project Foo --include-helpers medium --prune-packages --apply");
        return command;
    }

    public async Task<CommandOutcome<MoveTestsResult>> ExecuteAsync(MoveTestsOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<MoveTestsResult>.Environment();
        }

        var source = MoveCommandSupport.Resolve(options.Project, model, context);
        var to = options.To is null ? null : MoveCommandSupport.Resolve(options.To, model, context);
        if (source is null || (options.To is not null && to is null))
        {
            return CommandOutcome<MoveTestsResult>.Usage();
        }

        var threshold = options.HelpersGiven ? options.IncludeHelpers : Enum.Parse<TestConfidence>(config.Move.Tests.HelperMinConfidence, ignoreCase: true);
        var plan = await TestMovePlanner.PlanAsync(new MoveTestsRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            Source = source,
            To = to,
            Create = options.Create,
            IncludeHelpers = threshold,
            PrunePackages = options.PrunePackages,
            Diagnostics = context.Diagnostics,
        }, cancellationToken);
        if (plan is null)
        {
            return context.Diagnostics.Contains("OFR0004") ? CommandOutcome<MoveTestsResult>.Environment() : CommandOutcome<MoveTestsResult>.Usage();
        }

        if (!options.Apply)
        {
            return CommandOutcome<MoveTestsResult>.Completed(plan.Result with { Preview = plan.ChangeSet?.Preview() });
        }

        var outcome = await MoveExecutor.ApplyAsync(plan, new MoveExecution
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            VerifyPolicy = options.Verify ?? config.Move.Verify,
            Git = context.Host.GitService,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Now = context.Host.Time.GetUtcNow(),
        }, cancellationToken);
        return outcome.Partial ? new CommandOutcome<MoveTestsResult>(outcome.Result, OutcomeKind.Partial) : CommandOutcome<MoveTestsResult>.Completed(outcome.Result);
    }

    public void Render(MoveTestsResult result, CommandContext context, HumanOutput output)
    {
        var tests = result.Moves.Count(m => m.Kind == TestFileKind.Test);
        var helpers = result.Moves.Count - tests;
        var what = string.Create(CultureInfo.InvariantCulture, $"{result.Moves.Count} file{(result.Moves.Count == 1 ? "" : "s")} ({tests} test{(tests == 1 ? "" : "s")}, {helpers} helper{(helpers == 1 ? "" : "s")})");
        if (result.To is null)
        {
            output.Headline($"Nothing to move: no destination for the tests of {result.Project}.", Theme.BlockingStyle);
            return;
        }

        var headline = result.RolledBack ? $"Rolled back: {what} moved from {result.Project} to {result.To}, but verification failed."
            : result.Applied ? $"Moved {what} from {result.Project} to {result.To}{(result.Created ? " (created)" : "")}."
            : result.Moves.Count == 0 ? $"Nothing to move from {result.Project}."
            : $"Would move {what} from {result.Project} to {result.To}{(result.Created ? " (to be created)" : "")}.";
        output.Headline(headline, result.RolledBack ? Theme.BlockingStyle : result.Moves.Count == 0 ? Theme.DecisionStyle : Theme.ReadyStyle);

        if (result.Moves.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("File");
            table.AddColumn("To");
            table.AddColumn("Kind");
            foreach (var move in result.Moves)
            {
                table.AddRow(Markup.Escape(move.File), Markup.Escape(move.To), $"{Wire(move.Kind)} [dim]({Wire(move.Confidence)})[/]");
            }

            output.Write(table);
        }

        foreach (var skipped in result.Skipped)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Stays:[/] {Markup.Escape(skipped.File)} [dim]{Markup.Escape(skipped.Code)}[/]");
        }

        foreach (var candidate in result.Candidates)
        {
            output.MarkupLine($"[dim]Review:[/] {Markup.Escape(candidate.File)} [dim]({Wire(candidate.Confidence)}: {Markup.Escape(string.Join("; ", candidate.Reasons))}; --include-helpers medium moves it)[/]");
        }

        if (result.Preview is { Length: > 0 } preview)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);

            output.Line();
            output.MarkupLine("[dim]Dry run. Apply with[/] offramp move tests --project " + Markup.Escape(result.Project) + " --apply" + (result.Created ? " --create" : ""));
        }

        if (result.Applied)
        {
            var projects = result.ProjectEdits.Select(e => e.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            output.MarkupLine($"[dim]Staged:[/] {result.Moves.Count} rename{(result.Moves.Count == 1 ? "" : "s")} (git mv). [dim]Unstaged:[/] {Markup.Escape(string.Join(", ", projects))}.");
            output.MarkupLine("[dim]Suggested commits: the project files first, then the renames (a pure-move commit).[/]");
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal!)}");
        }
    }

    private static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}

public sealed record MoveRollbackOptions(string Journal);

/// <summary><c>offramp move rollback --journal PATH</c>: undo an applied move from its journal.</summary>
public sealed class MoveRollbackCommand : ICommandHandler<MoveRollbackOptions, MoveRollbackResult>
{
    public string CommandPath => "move rollback";

    public JsonTypeInfo<MoveRollbackResult> ResultType => RefactoringJsonContext.Default.MoveRollbackResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var journal = new Option<string>("--journal") { Description = "The journal an applied move wrote (.offramp/journal/...).", HelpName = "PATH", Required = true };
        var command = new Command("rollback", "Undo an applied move from its journal: renames reversed, project files restored byte for byte, new files deleted.")
        {
            journal,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new MoveRollbackCommand(), new MoveRollbackOptions(parse.GetValue(journal)!), globals.Bind(parse), host, ct));
        HelpExamples.Add(command, "offramp move rollback --journal .offramp/journal/20260926-040000-move-tests.json");
        return command;
    }

    public async Task<CommandOutcome<MoveRollbackResult>> ExecuteAsync(MoveRollbackOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var path = Offramp.Core.Paths.RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(options.Journal, context.Host.WorkingDirectory));
        var applier = new ChangeSetApplier(root, context.Host.GitService);
        if (!File.Exists(Offramp.Core.Paths.RepoPaths.ToAbsolute(root, path)))
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0004, $"No journal at {path}.", data: [KeyValuePair.Create<string, JsonNode?>("journal", path)]);
            return CommandOutcome<MoveRollbackResult>.Usage();
        }

        var journal = applier.Read(path);
        var undone = journal.Steps.Count(s => s.Done);
        try
        {
            await applier.RollbackAsync(path, cancellationToken);
        }
        catch (RollbackConflictException conflict)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR2151,
                $"{string.Join(", ", conflict.Paths)} changed since the move; nothing was rolled back.",
                data: [KeyValuePair.Create<string, JsonNode?>("files", new JsonArray([.. conflict.Paths.Select(p => (JsonNode?)p)]))]);
            return CommandOutcome<MoveRollbackResult>.Completed(new MoveRollbackResult { Journal = path, Command = journal.Command, Undone = 0, Changed = conflict.Paths });
        }

        return CommandOutcome<MoveRollbackResult>.Completed(new MoveRollbackResult { Journal = path, Command = journal.Command, Undone = undone, Changed = [] });
    }

    public void Render(MoveRollbackResult result, CommandContext context, HumanOutput output)
    {
        if (result.Changed.Count > 0)
        {
            output.Headline($"Not rolled back: {string.Join(", ", result.Changed)} changed since the move.", Theme.BlockingStyle);
            return;
        }

        output.Headline(result.Undone == 0 ? $"Nothing to roll back in {result.Journal}." : $"Rolled back {result.Command}: {result.Undone} step{(result.Undone == 1 ? "" : "s")} undone.", Theme.ReadyStyle);
    }
}
