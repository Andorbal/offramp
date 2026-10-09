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

public sealed record NewtonsoftTestsOptions(IReadOnlyList<string> Projects, string? To, string? Samples);

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
        var command = new Command("tests", "Generate characterization probes for every serialization site into the test projects that cover it.")
        {
            project, to, samples,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new NewtonsoftTestsCommand(),
            new NewtonsoftTestsOptions(parse.GetValue(project) ?? [], parse.GetValue(to), parse.GetValue(samples)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp newtonsoft tests",
            "offramp newtonsoft tests --samples payloads/ --apply",
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
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        }, cancellationToken);
        if (changeSet is null || !context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<NewtonsoftTestsResult>.Completed(result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(changeSet, "newtonsoft tests", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<NewtonsoftTestsResult>.Completed(result with { Applied = true, Journal = journal, Preview = null });
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

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal ?? "")}");
        }
        else if (result.Files.Any(f => f.Action is "create" or "edit" or "delete"))
        {
            output.MarkupLine("[dim]Dry run. Write the probes with[/] --apply [dim](the JSON output has the full preview).[/]");
        }
    }
}
