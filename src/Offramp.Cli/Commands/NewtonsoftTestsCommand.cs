using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding;
using Offramp.Scaffolding.Newtonsoft;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record NewtonsoftTestsOptions(IReadOnlyList<string> Projects, string? To, string? Samples, bool Record = false, bool Verify = false, bool AllProjects = false);

/// <summary><c>offramp newtonsoft tests</c> (docs/spec/commands/newtonsoft.md#newtonsoft-tests).</summary>
public sealed class NewtonsoftTestsCommand : ICommandHandler<NewtonsoftTestsOptions, NewtonsoftTestsResult>
{
    public string CommandPath => "newtonsoft tests";

    public JsonTypeInfo<NewtonsoftTestsResult> ResultType => ScaffoldingJsonContext.Default.NewtonsoftTestsResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string[]>("--project") { Description = "Only these production projects (path or name). Repeatable.", HelpName = "PROJECT" };
        var to = new Option<string?>("--to") { Description = "The test project for the probes (with a single --project).", HelpName = "PROJECT" };
        var samples = new Option<string?>("--samples") { Description = "A folder of real payloads named <payload type full name>[.<label>].json.", HelpName = "DIR" };
        var record = new Option<bool>("--record") { Description = "Build and run the probes, twice, and write what they observe as baselines (with --apply)." };
        var verify = new Option<bool>("--verify") { Description = "Build and run the probes once and compare what they observe with the baselines." };
        var allProjects = new Option<bool>("--all-projects") { Description = "Include projects outside deployed application scope." };
        var command = new Command("tests", "Generate characterization probes for every serialization site into the test projects that cover it; record and verify their baselines.")
        {
            project, to, samples, record, verify, allProjects,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(record) && result.GetValue(verify))
            {
                result.AddError("--record and --verify cannot be combined.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new NewtonsoftTestsCommand(),
            new NewtonsoftTestsOptions(parse.GetValue(project) ?? [], parse.GetValue(to), parse.GetValue(samples), parse.GetValue(record), parse.GetValue(verify), parse.GetValue(allProjects)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp newtonsoft tests",
            "offramp newtonsoft tests --samples payloads/ --record --apply",
            "offramp newtonsoft tests --verify",
            "offramp newtonsoft tests --project src/Orders/Orders.csproj --to tests/Orders.Tests/Orders.Tests.csproj --apply");
        return command;
    }

    public async Task<CommandOutcome<NewtonsoftTestsResult>> ExecuteAsync(NewtonsoftTestsOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<NewtonsoftTestsResult>.Environment();
        }

        var projects = new List<string>();
        foreach (var value in options.Projects.Append(options.To).OfType<string>())
        {
            if (ProjectLookup.Resolve(value, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                return CommandOutcome<NewtonsoftTestsResult>.Usage();
            }

            projects.Add(id);
        }

        var to = options.To is null ? null : projects[^1];
        if (to is not null)
        {
            projects.RemoveAt(projects.Count - 1);
        }

        var samples = options.Samples ?? config.Newtonsoft.Samples;
        var (result, changeSet) = NewtonsoftTestsGenerator.Generate(new NewtonsoftTestsRequest
        {
            RepositoryRoot = root,
            Model = model,
            Projects = projects,
            To = to,
            TestProjects = config.Newtonsoft.Tests.Projects,
            Samples = samples is null ? null : Path.GetFullPath(samples, options.Samples is null ? root : context.Host.WorkingDirectory),
            Folder = config.Newtonsoft.Tests.Folder,
            SamplesPerType = config.Newtonsoft.Tests.SamplesPerType,
            ObjectDepth = config.Newtonsoft.Tests.ObjectDepth,
            EntryDepth = config.Newtonsoft.Tests.EntryDepth,
            NonHermetic = config.Newtonsoft.Hermetic.NonHermetic,
            AllProjects = options.AllProjects || string.Equals(config.Newtonsoft.Scope, "all", StringComparison.OrdinalIgnoreCase),
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        }, cancellationToken);
        var apply = context.Settings.Apply && !context.Settings.DryRun;
        if (changeSet is not null && apply)
        {
            var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(changeSet, "newtonsoft tests", context.Host.Time.GetUtcNow(), cancellationToken);
            result = result with { Applied = true, Journal = journal, Preview = null };
        }

        if (!options.Record && !options.Verify)
        {
            return CommandOutcome<NewtonsoftTestsResult>.Completed(result);
        }

        if (changeSet is not null && !apply)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR3746,
                "The probes on disk are not what generating them now would write; run `offramp newtonsoft tests --apply` first (or record with --apply, which writes them).");
            return CommandOutcome<NewtonsoftTestsResult>.Usage();
        }

        var run = new ProbeRunRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            Targets = result.Targets,
            Probes = result.Probes,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        };
        if (options.Verify)
        {
            var verified = await ProbeRecorder.VerifyAsync(run, cancellationToken);
            return CommandOutcome<NewtonsoftTestsResult>.Completed(result with { Mode = "verify", Runs = verified.Runs, Outcomes = verified.Outcomes });
        }

        var recorded = await ProbeRecorder.RecordAsync(run, cancellationToken);
        result = result with { Mode = "record", Runs = recorded.Runs, Outcomes = recorded.Outcomes };
        if (recorded.Baselines is null)
        {
            return CommandOutcome<NewtonsoftTestsResult>.Completed(result);
        }

        if (!apply)
        {
            return CommandOutcome<NewtonsoftTestsResult>.Completed(result with { Preview = recorded.Baselines.Preview() });
        }

        var baselines = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(recorded.Baselines, "newtonsoft tests record", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<NewtonsoftTestsResult>.Completed(result with { BaselineJournal = baselines });
    }

    public void Render(NewtonsoftTestsResult result, CommandContext context, HumanOutput output)
    {
        var c = result.Coverage;
        var covered = c.Sites - c.Uncovered.Values.Sum();
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Probes.Count} probes for {covered} of {c.Sites} sites ({c.WithEntryProbe} through an entry point, {c.WithTypeProbe} through Newtonsoft.Json) in {result.Targets.Count} test projects."),
            result.Uncovered.Count == 0 ? Theme.ReadyStyle : Theme.DecisionStyle);
        foreach (var target in result.Targets)
        {
            output.MarkupLine($"  [bold]{Markup.Escape(target.Production)}[/] → {Markup.Escape(target.TestProject)} [dim]({Markup.Escape(target.Framework.ToString().ToLowerInvariant())}, {Markup.Escape(target.Folder)})[/]");
        }

        foreach (var site in result.Uncovered)
        {
            output.MarkupLine($"  [dim]no probe:[/] {Markup.Escape(site.Site)} [dim]({Markup.Escape(site.Reason)}: {Markup.Escape(site.Detail)})[/]");
        }

        foreach (var file in result.Files.Where(f => f.Action is not "unchanged"))
        {
            output.MarkupLine($"  [dim]{Markup.Escape(file.Action)}[/] {Markup.Escape(file.Path)}");
        }

        var outcomes = result.Outcomes.GroupBy(o => o.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}").ToList();
        if (outcomes.Count > 0)
        {
            output.MarkupLine($"  [bold]{Markup.Escape(result.Mode)}:[/] {Markup.Escape(string.Join(", ", outcomes))}");
        }

        foreach (var outcome in result.Outcomes.Where(o => o.Diff is not null))
        {
            output.MarkupLine($"  [yellow]{Markup.Escape(outcome.Status.ToString().ToLowerInvariant())}[/] {Markup.Escape(outcome.Probe)}");
            foreach (var line in outcome.Diff!.Split('\n').Take(8))
            {
                output.MarkupLine($"      [dim]{Markup.Escape(line)}[/]");
            }
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal ?? "")}");
        }

        if (result.BaselineJournal is not null)
        {
            output.MarkupLine($"[dim]Baselines written; undo with[/] offramp move rollback --journal {Markup.Escape(result.BaselineJournal)}");
        }
        else if (result.Files.Any(f => f.Action is "create" or "edit" or "delete"))
        {
            output.MarkupLine("[dim]Dry run. Write the probes with[/] --apply [dim](the JSON output has the full preview).[/]");
        }
    }
}
