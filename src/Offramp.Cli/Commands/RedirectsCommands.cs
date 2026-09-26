using System.CommandLine;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Refactoring;
using Offramp.Refactoring.Dependencies.Redirects;
using Offramp.Refactoring.ChangeSets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary>The <c>redirects</c> command group (docs/spec/commands/deps.md#redirects-sync).</summary>
public static class RedirectsCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var redirects = new Command("redirects", "Binding redirects for .NET Framework applications.");
        redirects.Subcommands.Add(RedirectsSyncCommand.Create(host, globals));
        return redirects;
    }
}

public sealed record RedirectsSyncOptions(IReadOnlyList<string> Apps, bool Prune);

/// <summary><c>offramp redirects sync</c>: redirects computed from the resolved graph instead of accumulated by hand.</summary>
public sealed class RedirectsSyncCommand : ICommandHandler<RedirectsSyncOptions, RedirectsResult>
{
    public string CommandPath => "redirects sync";

    public JsonTypeInfo<RedirectsResult> ResultType => RefactoringJsonContext.Default.RedirectsResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var apps = new Option<string[]>("--app") { Description = "The application project to sync (path or name; repeatable). Default: every .NET Framework application.", HelpName = "PROJECT", AllowMultipleArgumentsPerToken = true };
        var prune = new Option<bool>("--prune") { Description = "Remove redirects for assemblies no package in the graph provides." };
        var command = new Command("sync", "Rewrite the assemblyBinding section of app.config/web.config from the resolved package graph; everything else in the file stays byte for byte.")
        {
            apps, prune,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new RedirectsSyncCommand(), new RedirectsSyncOptions(parse.GetValue(apps) ?? [], parse.GetValue(prune)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp redirects sync",
            "offramp redirects sync --app src/Web/Web.csproj --prune --apply");
        return command;
    }

    public async Task<CommandOutcome<RedirectsResult>> ExecuteAsync(RedirectsSyncOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var (model, _, _) = DepsCommands.Load(context, null);
        if (model is null)
        {
            return CommandOutcome<RedirectsResult>.Environment();
        }

        var apps = new List<string>();
        foreach (var app in options.Apps)
        {
            if (MoveCommandSupport.Resolve(app, model, context) is not { } id)
            {
                return CommandOutcome<RedirectsResult>.Usage();
            }

            apps.Add(id);
        }

        var plan = RedirectPlanner.Plan(new RedirectsRequest
        {
            RepositoryRoot = context.Repository.Path,
            Model = model,
            Apps = apps.Count == 0 ? null : apps,
            Prune = options.Prune,
            Diagnostics = context.Diagnostics,
        });
        if (!context.Settings.Apply || context.Settings.DryRun || plan.ChangeSet is null)
        {
            return CommandOutcome<RedirectsResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(context.Repository.Path, context.Host.GitService).ApplyAsync(plan.ChangeSet, "redirects sync", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<RedirectsResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(RedirectsResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        var changes = s.Added + s.Changed + s.Pruned;
        output.Headline(
            result.Apps.Count == 0 ? "No .NET Framework application to sync."
            : changes == 0 ? string.Create(CultureInfo.InvariantCulture, $"Redirects match the graph ({s.Unchanged} unchanged{(s.Stale > 0 ? $", {s.Stale} stale" : "")}).")
            : string.Create(CultureInfo.InvariantCulture, $"{(result.Applied ? "Synced" : "Would sync")} redirects: {s.Added} added, {s.Changed} changed, {s.Pruned} pruned{(s.Stale > 0 ? $", {s.Stale} stale" : "")}."),
            s.Stale > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var app in result.Apps)
        {
            output.MarkupLine($"[bold]{Markup.Escape(app.Project)}[/] [dim]{Markup.Escape(app.ConfigFile ?? app.Skipped ?? "")}[/]");
            foreach (var redirect in app.Redirects)
            {
                var style = redirect.Action switch
                {
                    RedirectAction.Added or RedirectAction.Changed => Theme.ReadyStyle,
                    RedirectAction.Pruned or RedirectAction.Stale => Theme.DecisionStyle,
                    _ => "dim",
                };
                output.MarkupLine($"  [{style}]{redirect.Action.ToString().ToLowerInvariant(),-9}[/] {Markup.Escape(redirect.Assembly)} [dim]{Markup.Escape(redirect.OldVersion ?? "")} → {Markup.Escape(redirect.NewVersion ?? "")}[/]");
            }
        }

        if (result.Preview is { Length: > 0 } preview)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);
            output.Line();
            output.MarkupLine("[dim]Dry run. Apply with[/] --apply");
        }
    }
}
