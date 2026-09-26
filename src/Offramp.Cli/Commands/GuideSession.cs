using System.Text.Json;
using System.Text.Json.Nodes;
using Offramp.Cli.Infrastructure;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Workspace.Guide;
using Offramp.Workspace.Store;

namespace Offramp.Cli.Commands;

/// <summary>The answers a guide session can give.</summary>
public enum GuideAnswer
{
    Run,
    MarkDone,
    Skip,
    Back,
    Quit,

    /// <summary>Skip every open step of the current stage.</summary>
    SkipStage,
}

/// <summary>A choice among the open steps: <see cref="GuideAnswer.Run"/> with a step, <see cref="GuideAnswer.SkipStage"/>, or <see cref="GuideAnswer.Quit"/>.</summary>
public sealed record GuidePick(GuideAnswer Answer, string? Step = null);

/// <summary>Asks the guide session's questions; the Spectre prompter in production, a script in tests.</summary>
public interface IGuidePrompter
{
    /// <summary>Shows where the migration stands.</summary>
    void Show(GuideStatus status);

    /// <summary>Asks which of the current stage's open steps to take on (only when there are several).</summary>
    GuidePick PickStep(GuideStatus status);

    /// <summary>Asks which open project of a step done per project; null goes back.</summary>
    string? PickProject(GuideStepReport step);

    /// <summary>Explains the step and asks what to do with it.</summary>
    GuideAnswer PickAction(GuideStepReport step, string? project, string command, IReadOnlyList<GuideAnswer> answers);

    /// <summary>Asks whether to apply what the dry run showed.</summary>
    bool ConfirmApply(string command);

    /// <summary>A message between steps (plain text).</summary>
    void Tell(string message);
}

/// <summary>
/// One run of <c>offramp guide</c>: the recorded progress, the steps it runs (in process, through
/// the same command tree as the terminal), and the session loop that asks what to do next.
/// </summary>
internal sealed class GuideSession(CommandContext context, string statePath, GuideState state)
{
    private readonly List<GuideRun> _ran = [];
    private GuideFacts? _facts;

    public CommandContext Context => context;

    public GuideState State { get; private set; } = state;

    public IReadOnlyList<GuideRun> Ran => _ran;

    /// <summary>How the command ends after <see cref="TryResolveProject"/> or <see cref="SoleOpenProject"/> failed.</summary>
    public OutcomeKind ProjectFailure { get; private set; } = OutcomeKind.UsageFailure;

    private int Target => _facts?.Target ?? context.Config.Config.Target;

    public GuideStatus Evaluate()
    {
        _facts = Facts();
        return GuideEvaluator.Evaluate(_facts, State);
    }

    public void Save() => GuideStateStore.Save(statePath, State);

    public void Record(GuideStep step, string? project, GuideRecordStatus status, int? exitCode)
    {
        State = State.With(new GuideRecord { Step = step.Id, Project = project, Status = status, ExitCode = exitCode });
        Save();
    }

    public void Forget(string step, string? project)
    {
        State = State.Without(step, project);
        Save();
    }

    public bool TryResolveProject(string? value, out ProjectInfo? project)
    {
        project = null;
        if (value is null)
        {
            return true;
        }

        if (Model() is not { } model)
        {
            return false;
        }

        var id = ProjectLookup.Resolve(value, model, context);
        if (id is null)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021,
                $"No project '{value}' in the workspace model. Use a repository-relative path or a unique project name.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
            ProjectFailure = OutcomeKind.UsageFailure;
            return false;
        }

        project = model.Projects.Single(p => p.Id == id);
        return true;
    }

    /// <summary>The project a per-project step runs for without <c>--project</c>: its only open one, or its only one.</summary>
    public ProjectInfo? SoleOpenProject(GuideStep step)
    {
        if (Model() is not { } model)
        {
            return null;
        }

        var report = Evaluate().Step(step.Id);
        var open = report.Projects.Where(p => p.Status == GuideStepStatus.Open).Select(p => p.Project).ToList();
        var candidates = open.Count > 0 ? open : [.. report.Projects.Select(p => p.Project)];
        if (candidates.Count == 1)
        {
            return model.Projects.Single(p => p.Id == candidates[0]);
        }

        context.Diagnostics.Report(DiagnosticCatalog.OFR0041,
            candidates.Count == 0
                ? $"`{step.Id}` has no project to run for in this repository."
                : $"`{step.Id}` is done per project and {candidates.Count} are open: {string.Join(", ", candidates)}. Pass --project.",
            data:
            [
                KeyValuePair.Create<string, JsonNode?>("step", step.Id),
                KeyValuePair.Create<string, JsonNode?>("candidates", new JsonArray([.. candidates.Select(c => (JsonNode?)c)])),
            ]);
        ProjectFailure = OutcomeKind.UsageFailure;
        return null;
    }

    /// <summary>
    /// Runs the step's command in process and records the outcome
    /// (docs/spec/commands/guide.md#running-a-step). <paramref name="confirmed"/> adds <c>--yes</c>
    /// because the session already asked.
    /// </summary>
    public async Task<GuideRun> RunAsync(GuideStep step, ProjectInfo? project, bool apply, bool confirmed, CancellationToken cancellationToken)
    {
        var applied = apply && step.Writes == GuideWrites.Repository;
        var arguments = GuideCatalog.Arguments(step, project, Target).ToList();
        if (applied)
        {
            arguments.Add("--apply");
            if (confirmed)
            {
                arguments.Add("--yes");
            }
        }

        var invocation = arguments.Concat(PassedOn(context.Settings)).ToList();
        int exitCode;
        JsonNode? envelope = null;
        if (context.Settings.Json)
        {
            invocation.Add("--json");
            var buffer = new StringWriter { NewLine = "\n" };
            exitCode = await OfframpCli.RunAsync([.. invocation], context.Host with { Out = buffer }, cancellationToken);
            envelope = ParseEnvelope(buffer.ToString());
        }
        else
        {
            exitCode = await OfframpCli.RunAsync([.. invocation], context.Host, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var command = GuideCatalog.Display(arguments);
        var outcome = Outcome(step, exitCode, applied);
        Record(step, project?.Id, outcome, exitCode);
        if (outcome == GuideRecordStatus.Failed)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0042,
                $"`{command}` exited with code {exitCode}; `{step.Id}` stays open.",
                project is null ? null : new DiagnosticLocation(Project: project.Id),
                data:
                [
                    KeyValuePair.Create<string, JsonNode?>("step", step.Id),
                    KeyValuePair.Create<string, JsonNode?>("command", command),
                    KeyValuePair.Create<string, JsonNode?>("exitCode", exitCode),
                ]);
        }

        var run = new GuideRun { Step = step.Id, Project = project?.Id, Command = command, ExitCode = exitCode, Outcome = outcome, Envelope = envelope };
        _ran.Add(run);
        return run;
    }

    /// <summary>
    /// The session: on a first run, doctor; then, until nothing is open or the user stops, show
    /// the status, ask which step (when there are several) and what to do with it, and do it.
    /// </summary>
    public async Task RunSessionAsync(IGuidePrompter prompter, bool started, CancellationToken cancellationToken)
    {
        if (started)
        {
            prompter.Tell(Welcome());
            await RunAsync(GuideCatalog.Find("doctor")!, null, apply: false, confirmed: false, cancellationToken);
        }

        while (true)
        {
            var status = Evaluate();
            prompter.Show(status);
            if (status.Next.Count == 0)
            {
                return;
            }

            var single = status.Next.Count == 1;
            var stepId = status.Next[0];
            if (!single)
            {
                var pick = prompter.PickStep(status);
                if (pick.Answer == GuideAnswer.Quit)
                {
                    return;
                }

                if (pick.Answer == GuideAnswer.SkipStage)
                {
                    SkipAll(status.Next);
                    continue;
                }

                stepId = pick.Step!;
            }

            if (!await TakeStepAsync(prompter, status.Step(stepId), single, cancellationToken))
            {
                return;
            }
        }
    }

    /// <summary>Asks what to do with one step and does it; false when the user stops.</summary>
    private async Task<bool> TakeStepAsync(IGuidePrompter prompter, GuideStepReport report, bool single, CancellationToken cancellationToken)
    {
        var step = GuideCatalog.Find(report.Id)!;
        ProjectInfo? project = null;
        if (step.PerProject)
        {
            var open = report.Projects.Where(p => p.Status == GuideStepStatus.Open).Select(p => p.Project).ToList();
            var chosen = open.Count == 1 ? open[0] : prompter.PickProject(report);
            if (chosen is null)
            {
                return !single;
            }

            project = _facts!.Model!.Projects.Single(p => p.Id == chosen);
        }

        List<GuideAnswer> answers = [GuideAnswer.Run];
        if (!step.ObservedOnly)
        {
            answers.AddRange([GuideAnswer.MarkDone, GuideAnswer.Skip]);
        }

        answers.AddRange(single ? [GuideAnswer.Quit] : [GuideAnswer.Back, GuideAnswer.Quit]);
        var command = GuideCatalog.Display(GuideCatalog.Arguments(step, project, Target));
        switch (prompter.PickAction(report, project?.Id, command, answers))
        {
            case GuideAnswer.Run:
                await RunInSessionAsync(prompter, step, project, command, cancellationToken);
                return true;
            case GuideAnswer.MarkDone:
                Record(step, project?.Id, GuideRecordStatus.Done, exitCode: null);
                return true;
            case GuideAnswer.Skip:
                Record(step, project?.Id, GuideRecordStatus.Skipped, exitCode: null);
                return true;
            case GuideAnswer.Back:
                return true;
            default:
                return false;
        }
    }

    /// <summary>Runs a step; for a writer, the dry run first, then (with --apply, after asking) the change.</summary>
    private async Task RunInSessionAsync(IGuidePrompter prompter, GuideStep step, ProjectInfo? project, string command, CancellationToken cancellationToken)
    {
        var run = await RunAsync(step, project, apply: false, confirmed: false, cancellationToken);
        if (run.Outcome == GuideRecordStatus.Previewed && run.ExitCode != 0)
        {
            prompter.Tell("The dry run reported problems (above). Fix them before applying; the step stays open.");
            return;
        }

        if (run.Outcome == GuideRecordStatus.Previewed)
        {
            var applyCommand = command + " --apply";
            if (!context.Settings.Apply)
            {
                prompter.Tell($"That was a dry run; nothing changed. To make the change, run `{applyCommand}`, or start the guide with `offramp guide --apply` to apply from here.");
                return;
            }

            if (!context.Settings.Yes && !prompter.ConfirmApply(applyCommand))
            {
                prompter.Tell("Nothing was applied; the step stays open.");
                return;
            }

            run = await RunAsync(step, project, apply: true, confirmed: true, cancellationToken);
        }

        if (run.Outcome == GuideRecordStatus.Failed)
        {
            prompter.Tell($"`{run.Command}` exited with code {run.ExitCode}; the step stays open. Fix what it reported and run it again, or mark it done or skip it.");
        }
    }

    private void SkipAll(IEnumerable<string> steps)
    {
        foreach (var step in steps.Select(id => GuideCatalog.Find(id)!).Where(s => !s.ObservedOnly))
        {
            Record(step, null, GuideRecordStatus.Skipped, exitCode: null);
        }
    }

    private string Welcome() =>
        $"Welcome to the Offramp guide. It walks through moving this repository to net{Target}.0 one step at a time: "
        + "it explains why each step matters, runs Offramp's commands for you, and asks whenever there is a choice. "
        + $"Progress is kept in {Offramp.Core.Paths.RepoPaths.ToRepositoryRelative(context.Repository.Path, statePath)}, so you can stop at any point and pick up later. "
        + "Steps that change your code show a dry run unless you start the guide with --apply.\n\n"
        + "First, a quick check of your machine and repository.";

    /// <summary>What a run's exit code means for the step (docs/spec/commands/guide.md#running-a-step).</summary>
    private static GuideRecordStatus Outcome(GuideStep step, int exitCode, bool applied)
    {
        var dryRun = step.Writes == GuideWrites.Repository && !applied;
        return exitCode switch
        {
            0 => dryRun ? GuideRecordStatus.Previewed : GuideRecordStatus.Done,
            1 when dryRun => GuideRecordStatus.Previewed,
            1 when !step.FailsOnFindings && !applied => GuideRecordStatus.Done,
            _ => GuideRecordStatus.Failed,
        };
    }

    /// <summary>The global options a step inherits from the guide's command line.</summary>
    private static IEnumerable<string> PassedOn(GlobalSettings settings)
    {
        if (settings.Target is { } target)
        {
            yield return "--target";
            yield return target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var (name, value) in new[] { ("--solution", settings.Solution), ("--config", settings.Config), ("--workspace", settings.Workspace) })
        {
            if (value is not null)
            {
                yield return name;
                yield return value;
            }
        }

        foreach (var (name, on) in new[] { ("--verbose", settings.Verbose), ("--quiet", settings.Quiet), ("--no-cache", settings.NoCache), ("--llm", settings.Llm == true), ("--no-llm", settings.Llm == false) })
        {
            if (on)
            {
                yield return name;
            }
        }
    }

    private static JsonNode? ParseEnvelope(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private GuideFacts Facts()
    {
        var root = context.Repository.Path;
        var config = ConfigLoader.Load(new ConfigSources
        {
            RepositoryRoot = root,
            ExplicitPath = CommandRunner.ResolveConfigPath(context.Settings, context.Host),
            Environment = context.Host.Environment,
        });
        var target = context.Settings.Target ?? (config.IsValid ? config.Config.Target : context.Config.Config.Target);
        return GuideFacts.Gather(root, target, config.File is not null, context.WorkspacePath, WorkspaceStore.StateDirectory(root, context.Config.Config));
    }

    private WorkspaceModel? Model()
    {
        _facts ??= Facts();
        if (_facts.Model is { } model)
        {
            return model;
        }

        context.Diagnostics.Report(DiagnosticCatalog.OFR0001,
            $"{Offramp.Core.Paths.RepoPaths.ToRepositoryRelative(context.Repository.Path, context.WorkspacePath)} does not exist, so projects cannot be looked up. Run `offramp guide --run scan` first.");
        ProjectFailure = OutcomeKind.EnvironmentFailure;
        return null;
    }
}
