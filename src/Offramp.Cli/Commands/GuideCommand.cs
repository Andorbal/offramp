using System.CommandLine;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Workspace;
using Offramp.Workspace.Guide;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record GuideOptions(string? Run = null, string? Done = null, string? Skip = null, string? Reset = null, string? Project = null)
{
    public bool HasAction => Run is not null || Done is not null || Skip is not null || Reset is not null;
}

/// <summary><c>offramp guide</c>: a walk through the migration, one step at a time (docs/spec/commands/guide.md).</summary>
public sealed class GuideCommand : ICommandHandler<GuideOptions, GuideResult>, INextStep<GuideResult>, IInteractiveCommand
{
    public const string All = "all";

    public string CommandPath => "guide";

    public JsonTypeInfo<GuideResult> ResultType => WorkspaceJsonContext.Default.GuideResult;

    /// <summary>The guide's first step is doctor, which reports configuration problems.</summary>
    public bool RunsWithInvalidConfig => true;

    /// <summary>Configuration findings belong to doctor's output, not the guide's.</summary>
    public bool IncludesConfigDiagnostics => false;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var run = StepOption("--run", "Run a step's command now: a dry run for steps that change the repository, unless --apply.");
        var done = StepOption("--done", "Record a step as done without running it.");
        var skip = StepOption("--skip", "Record a step as skipped.");
        var reset = StepOption("--reset", "Forget what was recorded for a step, or `all` to start the guide's record over.", All);
        var project = new Option<string?>("--project")
        {
            Description = "The project, for a step done per project (path or name).",
            HelpName = "PROJECT",
        };
        var command = new Command("guide", "Walk through the migration one step at a time: explains each step, runs Offramp's commands for you, remembers progress in .offramp/guide.json, and asks when there is a choice.")
        {
            run, done, skip, reset, project,
        };
        command.Validators.Add(result =>
        {
            var actions = new[] { run, done, skip, reset }.Where(o => result.GetValue(o) is not null).ToList();
            if (actions.Count > 1)
            {
                result.AddError("Use one of --run, --done, --skip, and --reset at a time.");
            }

            if (result.GetValue(project) is null)
            {
                return;
            }

            var step = actions.Count == 1 ? GuideCatalog.Find(result.GetValue(actions[0]) ?? "") : null;
            if (actions.Count == 0 || (step is not null && !step.PerProject))
            {
                result.AddError("--project applies to steps done per project: "
                    + string.Join(", ", GuideCatalog.Steps.Where(s => s.PerProject).Select(s => s.Id)) + ".");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new GuideCommand(),
            new GuideOptions(parse.GetValue(run), parse.GetValue(done), parse.GetValue(skip), parse.GetValue(reset), parse.GetValue(project)),
            globals.Bind(parse),
            host,
            ct));
        HelpExamples.Add(command,
            "offramp guide",
            "offramp guide --apply",
            "offramp guide --json | jq .result.next",
            "offramp guide --run plan",
            "offramp guide --skip audit-native",
            "offramp guide --run move-tests --project src/Billing/Billing.csproj");
        return command;
    }

    private static Option<string?> StepOption(string name, string description, params string[] extra)
    {
        var option = new Option<string?>(name) { Description = description, HelpName = "STEP" };
        option.AcceptOnlyFromAmong([.. GuideCatalog.Ids, .. extra]);
        return option;
    }

    public async Task<CommandOutcome<GuideResult>> ExecuteAsync(GuideOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var statePath = GuideStateStore.PathIn(WorkspaceStore.StateDirectory(root, context.Config.Config));
        var stateFile = RepoPaths.ToRepositoryRelative(root, statePath);

        GuideState? saved;
        try
        {
            saved = options.Reset == All ? null : GuideStateStore.Read(statePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0040,
                $"{stateFile} cannot be read ({ex.Message}); nothing was changed. Fix it, or run `offramp guide --reset all` to start over.",
                new DiagnosticLocation(File: stateFile));
            return CommandOutcome<GuideResult>.Environment();
        }

        var session = new GuideSession(context, statePath, saved ?? new GuideState());
        var started = !File.Exists(statePath);
        var interactive = false;
        OutcomeKind kind;
        if (options.HasAction)
        {
            kind = await ActAsync(session, options, cancellationToken);
        }
        else if (context.Interactive)
        {
            interactive = true;
            var console = ConsoleFactory.Create(context.Host, context.Host.Out, isTerminal: true);
            var prompter = context.Host.GuidePrompter?.Invoke(console) ?? new SpectreGuidePrompter(console);
            await session.RunSessionAsync(prompter, started, cancellationToken);
            kind = OutcomeKind.Completed;
        }
        else
        {
            if (started)
            {
                await session.RunAsync(GuideCatalog.Find("doctor")!, null, apply: false, confirmed: false, cancellationToken);
            }

            kind = OutcomeKind.Completed;
        }

        if (kind != OutcomeKind.Completed)
        {
            return new CommandOutcome<GuideResult>(null, kind);
        }

        session.Save();
        var status = session.Evaluate();
        return CommandOutcome<GuideResult>.Completed(new GuideResult
        {
            StateFile = stateFile,
            Started = started,
            Interactive = interactive,
            Target = context.Config.Config.TargetFramework,
            Ran = session.Ran,
            Stage = status.Stage,
            Next = status.Next,
            Stages = status.Stages,
            Counts = status.Counts,
        });
    }

    /// <summary>One of <c>--run</c>, <c>--done</c>, <c>--skip</c>, <c>--reset</c>.</summary>
    private static async Task<OutcomeKind> ActAsync(GuideSession session, GuideOptions options, CancellationToken cancellationToken)
    {
        if (options.Reset is { } reset)
        {
            if (reset != All)
            {
                if (!session.TryResolveProject(options.Project, out var resetProject))
                {
                    return session.ProjectFailure;
                }

                session.Forget(reset, resetProject?.Id);
            }

            return OutcomeKind.Completed;
        }

        var step = GuideCatalog.Find(options.Run ?? options.Done ?? options.Skip!)!;
        if (options.Run is null && step.ObservedOnly)
        {
            session.Context.Diagnostics.Report(DiagnosticCatalog.OFR0043,
                $"`{step.Id}` cannot be skipped or marked done: the guide checks it from the repository. Run `offramp guide --run {step.Id}`.",
                data: [KeyValuePair.Create<string, JsonNode?>("step", step.Id)]);
            return OutcomeKind.UsageFailure;
        }

        if (!session.TryResolveProject(options.Project, out var project))
        {
            return session.ProjectFailure;
        }

        if (options.Run is null)
        {
            session.Record(step, project?.Id, options.Done is not null ? GuideRecordStatus.Done : GuideRecordStatus.Skipped, exitCode: null);
            return OutcomeKind.Completed;
        }

        if (step.PerProject && project is null)
        {
            project = session.SoleOpenProject(step);
            if (project is null)
            {
                return session.ProjectFailure;
            }
        }

        await session.RunAsync(step, project, apply: session.Context.Settings.Apply, confirmed: false, cancellationToken);
        return OutcomeKind.Completed;
    }

    public void Render(GuideResult result, CommandContext context, HumanOutput output)
    {
        if (result.Ran.Count > 0 && !result.Interactive)
        {
            output.Line();
            output.Write(new Rule("[dim]offramp guide[/]").LeftJustified().RuleStyle(Theme.DimStyle));
        }

        var (headline, style) = Headline(result);
        output.Headline(headline, style);
        if (result.Started)
        {
            output.MarkupLine($"[dim]Started the guide; progress is kept in {Markup.Escape(result.StateFile)}.[/]");
        }

        if (result.Interactive)
        {
            output.MarkupLine($"[dim]Progress is saved in {Markup.Escape(result.StateFile)}; run `offramp guide` to pick up where you left off.[/]");
            return;
        }

        output.Line();
        output.Write(GuideView.Stages(result.Stages, output.Unicode));
        RenderNext(result, output);
    }

    private static void RenderNext(GuideResult result, HumanOutput output)
    {
        var next = result.Next.Select(id => result.Stages.SelectMany(s => s.Steps).Single(s => s.Id == id)).ToList();
        if (next.Count == 1)
        {
            output.Line();
            output.Write(GuideView.Explain(next[0], null));
            output.MarkupLine(GuideCatalog.Find(next[0].Id)!.ObservedOnly
                ? $"Run it with [bold]{Markup.Escape(RunCommand(next[0]))}[/]."
                : $"Run it with [bold]{Markup.Escape(RunCommand(next[0]))}[/], or record it with --done or --skip.");
        }
        else if (next.Count > 1)
        {
            output.Line();
            output.MarkupLine("Run [bold]offramp guide[/] on a terminal to choose, or pick one with [bold]offramp guide --run STEP[/]:");
            var grid = new Grid();
            grid.AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(2));
            grid.AddColumn(new GridColumn());
            foreach (var step in next)
            {
                grid.AddRow(new Markup($"[bold]{Markup.Escape(step.Id)}[/]"), new Markup(Markup.Escape(step.Title)));
            }

            output.Write(grid);
        }
    }

    private static string RunCommand(GuideStepReport step)
    {
        var open = step.Projects.Where(p => p.Status == GuideStepStatus.Open).ToList();
        return open.Count > 1 ? $"offramp guide --run {step.Id} --project PROJECT" : $"offramp guide --run {step.Id}";
    }

    private static (string Text, string Style) Headline(GuideResult result)
    {
        var steps = result.Stages.SelectMany(s => s.Steps).ToList();
        if (result.Stage is null)
        {
            var waiting = steps.FirstOrDefault(s => s.Status == GuideStepStatus.Blocked);
            return waiting is null
                ? ("Nothing left for the guide: every step is done, skipped, or not needed.", Theme.ReadyStyle)
                : ($"The guide is waiting: {waiting.Note ?? waiting.Title}", Theme.DecisionStyle);
        }

        var stage = result.Stages.Single(s => s.Id == result.Stage);
        return result.Next.Count == 1
            ? ($"Next: {steps.Single(s => s.Id == result.Next[0]).Title}.", Theme.DecisionStyle)
            : ($"Next: pick one of {result.Next.Count} steps in “{stage.Title}”.", Theme.DecisionStyle);
    }

    public string? NextStep(GuideResult result, CommandContext context)
    {
        if (result.Next.Count == 0)
        {
            return null;
        }

        var step = GuideCatalog.Find(result.Next[0])!;
        return result.Next.Count == 1 && !result.Interactive && !step.PerProject
            ? GuideCatalog.Display(step.Command)
            : "offramp guide";
    }
}
