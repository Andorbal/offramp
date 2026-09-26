using Offramp.Core.Model;

namespace Offramp.Workspace.Guide;

/// <summary>
/// Decides each step's status from the repository's facts and the recorded progress, then
/// which stage is current and which steps are the choices (docs/spec/commands/guide.md#status).
/// </summary>
public static class GuideEvaluator
{
    private const string NeedsModel = "Needs the workspace model: run `offramp scan`.";

    public static GuideStatus Evaluate(GuideFacts facts, GuideState state)
    {
        var steps = new Dictionary<string, GuideStepReport>(StringComparer.Ordinal);
        foreach (var step in GuideCatalog.Steps)
        {
            steps[step.Id] = Localize(EvaluateStep(step, facts, state, steps), step, facts.Target);
        }

        var current = GuideCatalog.Stages.FirstOrDefault(stage => stage.Steps.Any(s => steps[s.Id].Status == GuideStepStatus.Open));
        var stages = GuideCatalog.Stages.Select(stage => StageReport(stage, current, steps)).ToList();
        var all = stages.SelectMany(s => s.Steps).ToList();
        return new GuideStatus
        {
            Stages = stages,
            Stage = current?.Id,
            Next = current is null ? [] : [.. current.Steps.Select(s => s.Id).Where(id => steps[id].Status == GuideStepStatus.Open)],
            Counts = new GuideCounts(
                all.Count,
                all.Count(s => s.Status == GuideStepStatus.Done),
                all.Count(s => s.Status == GuideStepStatus.Skipped),
                all.Count(s => s.Status == GuideStepStatus.Open),
                all.Count(s => s.Status == GuideStepStatus.Blocked),
                all.Count(s => s.Status == GuideStepStatus.NotApplicable)),
        };
    }

    /// <summary>A stage's steps; open steps outside the current stage wait for it.</summary>
    private static GuideStageReport StageReport(GuideStage stage, GuideStage? current, Dictionary<string, GuideStepReport> steps)
    {
        var reports = stage.Steps.Select(s => steps[s.Id]).ToList();
        if (stage != current && current is not null)
        {
            reports = [.. reports.Select(r => r.Status == GuideStepStatus.Open
                ? r with { Status = GuideStepStatus.Blocked, Note = r.Note ?? $"Comes after “{current.Title}”." }
                : r)];
        }

        var status = reports.All(r => r.Status is GuideStepStatus.Done or GuideStepStatus.Skipped or GuideStepStatus.NotApplicable)
            ? GuideStageStatus.Done
            : stage == current ? GuideStageStatus.Current : GuideStageStatus.Upcoming;
        return new GuideStageReport { Id = stage.Id, Title = stage.Title, Status = status, Steps = reports };
    }

    private static GuideStepReport EvaluateStep(GuideStep step, GuideFacts facts, GuideState state, Dictionary<string, GuideStepReport> earlier)
    {
        var record = state.Find(step.Id, null);
        var note = step.Note?.Invoke(facts, record);
        if (step.PerProject)
        {
            return EvaluatePerProject(step, facts, state, earlier);
        }

        if (step.Observed?.Invoke(facts) == true)
        {
            return Report(step, GuideStepStatus.Done, note);
        }

        if (!step.ObservedOnly && record?.Status is GuideRecordStatus.Done or GuideRecordStatus.Skipped)
        {
            return Report(step, record.Status == GuideRecordStatus.Done ? GuideStepStatus.Done : GuideStepStatus.Skipped, note);
        }

        if (step.Applies is not null)
        {
            if (facts.Model is null)
            {
                return Report(step, GuideStepStatus.Blocked, NeedsModel);
            }

            if (!step.Applies(facts, facts.Model))
            {
                return Report(step, GuideStepStatus.NotApplicable, null);
            }
        }

        if (Waiting(step, earlier) is { } waiting)
        {
            return Report(step, GuideStepStatus.Blocked, waiting);
        }

        return Report(step, GuideStepStatus.Open, note ?? (step.ObservedOnly ? null : RecordNote(record, GuideCatalog.Display(step.Command))));
    }

    private static GuideStepReport EvaluatePerProject(GuideStep step, GuideFacts facts, GuideState state, Dictionary<string, GuideStepReport> earlier)
    {
        var stepRecord = state.Find(step.Id, null);
        if (step.Observed?.Invoke(facts) == true)
        {
            return Report(step, GuideStepStatus.Done, null);
        }

        if (stepRecord?.Status is GuideRecordStatus.Done or GuideRecordStatus.Skipped)
        {
            return Report(step, stepRecord.Status == GuideRecordStatus.Done ? GuideStepStatus.Done : GuideStepStatus.Skipped, null);
        }

        if (facts.Model is not { } model)
        {
            return Report(step, GuideStepStatus.Blocked, NeedsModel);
        }

        var projects = step.Projects!(facts, model)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => ProjectReport(step, p, facts, state.Find(step.Id, p.Id) ?? stepRecord))
            .ToList();
        if (projects.Count == 0)
        {
            return step.BlockedWithoutProjects?.Invoke(facts, model) is { } reason
                ? Report(step, GuideStepStatus.Blocked, reason)
                : Report(step, GuideStepStatus.NotApplicable, null);
        }

        GuideStepStatus status;
        string? note = null;
        if (projects.All(p => p.Status == GuideStepStatus.Skipped))
        {
            status = GuideStepStatus.Skipped;
        }
        else if (projects.All(p => p.Status != GuideStepStatus.Open))
        {
            status = GuideStepStatus.Done;
        }
        else if (Waiting(step, earlier) is { } waiting)
        {
            (status, note) = (GuideStepStatus.Blocked, waiting);
        }
        else
        {
            var open = projects.Count(p => p.Status == GuideStepStatus.Open);
            (status, note) = (GuideStepStatus.Open, $"{open} of {projects.Count} project(s) open.");
        }

        return Report(step, status, note) with { Projects = projects };
    }

    private static GuideProjectReport ProjectReport(GuideStep step, ProjectInfo project, GuideFacts facts, GuideRecord? record)
    {
        var command = GuideCatalog.Display(GuideCatalog.Arguments(step, project, facts.Target));
        var status = record?.Status switch
        {
            GuideRecordStatus.Done => GuideStepStatus.Done,
            GuideRecordStatus.Skipped => GuideStepStatus.Skipped,
            _ => GuideStepStatus.Open,
        };
        return new GuideProjectReport { Project = project.Id, Status = status, Command = command, Note = RecordNote(record, command) };
    }

    /// <summary>Why a step waits for the steps it requires, or null when it does not.</summary>
    private static string? Waiting(GuideStep step, Dictionary<string, GuideStepReport> earlier)
    {
        var pending = step.Requires
            .Select(id => earlier[id])
            .Where(r => r.Status is GuideStepStatus.Open or GuideStepStatus.Blocked)
            .Select(r => r.Id)
            .ToList();
        return pending.Count == 0 ? null : $"Waiting on: {string.Join(", ", pending)}.";
    }

    private static string? RecordNote(GuideRecord? record, string command) => record?.Status switch
    {
        GuideRecordStatus.Previewed => $"Dry run shown; nothing changed yet. Apply with `{command} --apply`, or start the guide with --apply.",
        GuideRecordStatus.Failed => $"The last run exited with code {record.ExitCode}; see its output.",
        _ => null,
    };

    private static GuideStepReport Report(GuideStep step, GuideStepStatus status, string? note) => new()
    {
        Id = step.Id,
        Title = step.Title,
        Why = step.Why,
        Command = GuideCatalog.Display(step.Command),
        Writes = step.Writes,
        Requires = step.Requires,
        Status = status,
        Note = note,
    };

    private static GuideStepReport Localize(GuideStepReport report, GuideStep step, int target) => report with
    {
        Title = GuideCatalog.ForTarget(step.Title, target),
        Why = GuideCatalog.ForTarget(step.Why, target),
    };
}
