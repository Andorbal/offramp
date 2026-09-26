using System.CommandLine;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Refactoring;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Forwarders;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record ForwardersOptions(string From, string To, string? Since);

/// <summary><c>offramp forwarders</c>: type forwarders in the source for types that moved to the destination.</summary>
public sealed class ForwardersCommand : ICommandHandler<ForwardersOptions, ForwardersResult>
{
    public string CommandPath => "forwarders";

    public JsonTypeInfo<ForwardersResult> ResultType => RefactoringJsonContext.Default.ForwardersResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var from = new Option<string>("--from") { Description = "The project the types moved out of (path or name).", HelpName = "PROJECT", Required = true };
        var to = new Option<string>("--to") { Description = "The project they moved to (path or name).", HelpName = "PROJECT", Required = true };
        var since = new Option<string?>("--since") { Description = "Read the source's former public types at this commit (default: the compilation of the last scan).", HelpName = "GIT_REF" };
        var command = new Command("forwarders", "Keep binary consumers working after types moved: write TypeForwardedTo attributes in the source for types now in the destination, and report strings that name them with the old assembly.")
        {
            from, to, since,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new ForwardersCommand(), new ForwardersOptions(parse.GetValue(from)!, parse.GetValue(to)!, parse.GetValue(since)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp forwarders --from src/Foo/Foo.csproj --to src/Foo.Core/Foo.Core.csproj",
            "offramp forwarders --from Foo --to Foo.Core --since main --apply");
        return command;
    }

    public async Task<CommandOutcome<ForwardersResult>> ExecuteAsync(ForwardersOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<ForwardersResult>.Environment();
        }

        var from = MoveCommandSupport.Resolve(options.From, model, context);
        var to = MoveCommandSupport.Resolve(options.To, model, context);
        if (from is null || to is null)
        {
            return CommandOutcome<ForwardersResult>.Usage();
        }

        var plan = await ForwarderPlanner.PlanAsync(new ForwardersRequest
        {
            RepositoryRoot = root,
            Model = model,
            From = from,
            To = to,
            Since = options.Since,
            Git = context.Host.GitService,
            Diagnostics = context.Diagnostics,
        }, cancellationToken);
        if (plan is null)
        {
            return context.Diagnostics.Contains("OFR0004") ? CommandOutcome<ForwardersResult>.Environment() : CommandOutcome<ForwardersResult>.Usage();
        }

        if (!context.Settings.Apply || plan.ChangeSet is null)
        {
            return CommandOutcome<ForwardersResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, ForwarderPlanner.Command, context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ForwardersResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(ForwardersResult result, CommandContext context, HumanOutput output)
    {
        var count = result.Forwarded.Count;
        var what = string.Create(CultureInfo.InvariantCulture, $"{count} type{(count == 1 ? "" : "s")}");
        var headline = count == 0 ? $"Nothing to forward: no public type of {result.From} lives in {result.To} now."
            : result.File is null ? $"Cannot forward {what} from {result.From} to {result.To}."
            : result.Applied ? $"Forwarded {what} from {result.From} to {result.To} in {result.File}."
            : $"Would forward {what} from {result.From} to {result.To} in {result.File}.";
        output.Headline(headline, count > 0 && result.File is null ? Theme.BlockingStyle : count == 0 ? Theme.DecisionStyle : Theme.ReadyStyle);

        foreach (var type in result.Forwarded)
        {
            output.MarkupLine($"  {Markup.Escape(type.Type)} [dim]→ {Markup.Escape(type.File)}[/]");
        }

        foreach (var reference in result.StringReferences)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]String:[/] {Markup.Escape(reference.File)}:{reference.Line} [dim]{Markup.Escape(reference.Text)}[/]");
        }

        if (result.Preview is { Length: > 0 } preview)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);
            output.Line();
            output.MarkupLine($"[dim]Dry run. Apply with[/] offramp forwarders --from {Markup.Escape(result.From)} --to {Markup.Escape(result.To)}{(result.Since is null ? "" : " --since " + Markup.Escape(result.Since))} --apply");
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Build it with[/] offramp verify --projects {Markup.Escape(result.From)}[dim]; undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal!)}");
        }
    }
}
