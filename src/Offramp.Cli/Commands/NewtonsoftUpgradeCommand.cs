using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Scaffolding;
using Offramp.Scaffolding.Newtonsoft;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record NewtonsoftUpgradeOptions(IReadOnlyList<string> Projects, string? Version, string? OnDiff, bool AllProjects = false);

/// <summary><c>offramp newtonsoft upgrade</c> (docs/spec/commands/newtonsoft.md#newtonsoft-upgrade).</summary>
public sealed class NewtonsoftUpgradeCommand : ICommandHandler<NewtonsoftUpgradeOptions, NewtonsoftUpgradeResult>
{
    public string CommandPath => "newtonsoft upgrade";

    public JsonTypeInfo<NewtonsoftUpgradeResult> ResultType => ScaffoldingJsonContext.Default.NewtonsoftUpgradeResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string[]>("--project") { Description = "Only these projects (path or name). Repeatable; their test projects should come along.", HelpName = "PROJECT" };
        var version = new Option<string?>("--version") { Description = "Upgrade to this release instead of the newest in newtonsoft.target.", HelpName = "VERSION" };
        var onDiff = new Option<string?>("--on-diff") { Description = "keep (default, newtonsoft.onDiff) or rollback when probes observe a change." };
        onDiff.AcceptOnlyFromAmong("keep", "rollback");
        var allProjects = new Option<bool>("--all-projects") { Description = "Include projects outside deployed application scope when reporting findings." };
        var command = new Command("upgrade", "Move every project to the target Newtonsoft.Json (PackageReference, packages.config, HintPath, redirects), then restore, build, and compare the probes.")
        {
            project, version, onDiff, allProjects,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new NewtonsoftUpgradeCommand(),
            new NewtonsoftUpgradeOptions(parse.GetValue(project) ?? [], parse.GetValue(version), parse.GetValue(onDiff), parse.GetValue(allProjects)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp newtonsoft upgrade",
            "offramp newtonsoft upgrade --apply",
            "offramp newtonsoft upgrade --version 13.0.3 --on-diff rollback --apply");
        return command;
    }

    public async Task<CommandOutcome<NewtonsoftUpgradeResult>> ExecuteAsync(NewtonsoftUpgradeOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<NewtonsoftUpgradeResult>.Environment();
        }

        var projects = new List<string>();
        foreach (var value in options.Projects)
        {
            if (ProjectLookup.Resolve(value, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                return CommandOutcome<NewtonsoftUpgradeResult>.Usage();
            }

            projects.Add(id);
        }

        if (await NewtonsoftCommands.TargetAsync(context, model, options.Version, cancellationToken) is not { Version: { } target } release)
        {
            return CommandOutcome<NewtonsoftUpgradeResult>.Usage();
        }

        if (release.Package is null)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR3760, $"Newtonsoft.Json {target} is in neither the global packages folder nor the configured feeds.");
            return CommandOutcome<NewtonsoftUpgradeResult>.Environment();
        }

        var request = new NewtonsoftUpgradeRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            Target = target,
            TargetPackage = release.Package,
            Projects = projects,
            ExactVersion = options.Version is not null,
            AllProjects = options.AllProjects || string.Equals(config.Newtonsoft.Scope, "all", StringComparison.OrdinalIgnoreCase),
            OnDiff = options.OnDiff ?? config.Newtonsoft.OnDiff,
            Processes = context.Host.Processes,
            Git = context.Host.GitService,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Time = context.Host.Time,
        };
        var (result, changeSet) = await NewtonsoftUpgrader.PlanAsync(request, cancellationToken);
        if (changeSet is null || !context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<NewtonsoftUpgradeResult>.Completed(result);
        }

        return CommandOutcome<NewtonsoftUpgradeResult>.Completed(await NewtonsoftUpgrader.ApplyAsync(request, result, changeSet, cancellationToken));
    }

    public void Render(NewtonsoftUpgradeResult result, CommandContext context, HumanOutput output)
    {
        var from = string.Join(", ", result.From.Select(f => $"{f.Key} ({f.Value.Count})"));
        var changed = result.Probes.Count(p => p.Status is ProbeStatus.Differs or ProbeStatus.NowThrows or ProbeStatus.NoLongerThrows);
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"Newtonsoft.Json {from} → {result.To}: {result.Projects.Count} projects{(result.RolledBack ? ", rolled back" : result.Applied ? ", applied" : "")}{(result.Probes.Count > 0 ? $"; {changed} of {result.Probes.Count} probes changed" : "")}."),
            result.RolledBack || changed > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            output.MarkupLine($"  [bold]{Markup.Escape(project.Project)}[/] [dim]{Markup.Escape(project.From)} ({Markup.Escape(project.Via.ToString())}): {Markup.Escape(string.Join(", ", project.Files))}[/]");
        }

        foreach (var probe in result.Probes.Where(p => p.Diff is not null))
        {
            output.MarkupLine($"  [yellow]{Markup.Escape(probe.Status.ToString().ToLowerInvariant())}[/] {Markup.Escape(probe.Probe)}");
            foreach (var line in probe.Diff!.Split('\n').Take(8))
            {
                output.MarkupLine($"      [dim]{Markup.Escape(line)}[/]");
            }
        }

        if (result.Applied && !result.RolledBack)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal ?? "")}");
        }
        else if (!result.Applied && result.Preview is not null)
        {
            output.MarkupLine("[dim]Dry run. Upgrade with[/] --apply [dim](the JSON output has the full preview).[/]");
        }
    }
}
