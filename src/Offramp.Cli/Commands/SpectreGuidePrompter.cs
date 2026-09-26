using Offramp.Cli.Rendering;
using Offramp.Workspace.Guide;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Offramp.Cli.Commands;

/// <summary>How the guide's stages and steps look on a terminal; shared by the report view and the session.</summary>
public static class GuideView
{
    /// <summary>
    /// Every stage with its steps, one line each: the whole map of the migration. The current
    /// stage's steps also show their command and note.
    /// </summary>
    public static IRenderable Stages(IReadOnlyList<GuideStageReport> stages, bool unicode)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(1));
        grid.AddColumn(new GridColumn());
        grid.AddColumn(new GridColumn().NoWrap().PadLeft(2));
        foreach (var stage in stages)
        {
            var current = stage.Status == GuideStageStatus.Current;
            var mark = stage.Status switch
            {
                GuideStageStatus.Done => $" [{Theme.ReadyStyle}]{(unicode ? "✓" : "done")}[/]",
                GuideStageStatus.Current => $" [{Theme.DecisionStyle}](current)[/]",
                _ => "",
            };
            grid.AddRow(new Markup(""), new Markup($"[bold]{Markup.Escape(stage.Title)}[/]{mark}"), new Markup(""));
            foreach (var step in stage.Steps)
            {
                var detail = Markup.Escape(step.Title) + step.Status switch
                {
                    GuideStepStatus.NotApplicable => " [dim](not needed here)[/]",
                    GuideStepStatus.Skipped => " [dim](skipped)[/]",
                    _ => "",
                };
                if (current && step.Note is not null)
                {
                    detail += $"\n[dim]{Markup.Escape(step.Note)}[/]";
                }

                var command = current && step.Status is GuideStepStatus.Open or GuideStepStatus.Blocked ? $"[dim]{Markup.Escape(step.Command)}[/]" : "";
                grid.AddRow(new Markup(Icon(step.Status, unicode)), new Markup(detail), new Markup(command));
            }
        }

        return grid;
    }

    /// <summary>The step's title, why it matters, the command, and its note.</summary>
    public static IRenderable Explain(GuideStepReport step, string? command)
    {
        var lines = new List<string>
        {
            $"[bold]{Markup.Escape(step.Title)}[/]",
            "",
            Markup.Escape(step.Why),
            "",
            $"Command: [bold]{Markup.Escape(command ?? step.Command)}[/]",
        };
        if (step.Writes == GuideWrites.Repository)
        {
            lines.Add("[dim]This step changes project files or code: it shows a dry run first.[/]");
        }

        if (step.Note is not null)
        {
            lines.Add($"[{Theme.DecisionStyle}]{Markup.Escape(step.Note)}[/]");
        }

        if (command is null && step.Projects.Count > 0)
        {
            lines.Add("Projects: " + string.Join(", ", step.Projects.Select(p => Markup.Escape(p.Project) + (p.Status == GuideStepStatus.Open ? "" : $" [dim]({p.Status.ToString().ToLowerInvariant()})[/]"))));
        }

        return new Padder(new Markup(string.Join('\n', lines)), new Padding(2, 0, 0, 1));
    }

    private static string Icon(GuideStepStatus status, bool unicode) => status switch
    {
        GuideStepStatus.Done => $"[{Theme.ReadyStyle}]{(unicode ? "✓" : "ok")}[/]",
        GuideStepStatus.Open => $"[{Theme.DecisionStyle}]{(unicode ? "→" : ">")}[/]",
        GuideStepStatus.Skipped or GuideStepStatus.NotApplicable => "[dim]-[/]",
        _ => $"[dim]{(unicode ? "·" : ".")}[/]",
    };
}

/// <summary>The guide's session questions as Spectre prompts.</summary>
public sealed class SpectreGuidePrompter(IAnsiConsole console) : IGuidePrompter
{
    // Keys that cannot be step ids or project paths.
    private const string SkipStageKey = "\0skip-stage";
    private const string QuitKey = "\0quit";
    private const string BackKey = "\0back";
    private const string QuitLabel = "Stop for now (progress is saved)";

    public void Show(GuideStatus status)
    {
        console.WriteLine();
        console.Write(new Rule("[bold]Offramp guide[/]").LeftJustified());
        console.Write(GuideView.Stages(status.Stages, console.Profile.Capabilities.Unicode));
        console.WriteLine();
    }

    public GuidePick PickStep(GuideStatus status)
    {
        var steps = status.Next.Select(status.Step).ToDictionary(s => s.Id);
        var picked = Choose("Several steps could come next. Which one?", [.. steps.Keys, SkipStageKey, QuitKey], id => id switch
        {
            SkipStageKey => "Skip the rest of this stage",
            QuitKey => QuitLabel,
            _ => $"{Markup.Escape(steps[id].Title)} [dim]({Markup.Escape(id)})[/]",
        });
        return picked switch
        {
            SkipStageKey => new GuidePick(GuideAnswer.SkipStage),
            QuitKey => new GuidePick(GuideAnswer.Quit),
            _ => new GuidePick(GuideAnswer.Run, picked),
        };
    }

    public string? PickProject(GuideStepReport step)
    {
        List<string> open = [.. step.Projects.Where(p => p.Status == GuideStepStatus.Open).Select(p => p.Project), BackKey];
        var picked = Choose($"{Markup.Escape(step.Title)}: which project?", open, p => p == BackKey ? "Back" : Markup.Escape(p));
        return picked == BackKey ? null : picked;
    }

    public GuideAnswer PickAction(GuideStepReport step, string? project, string command, IReadOnlyList<GuideAnswer> answers)
    {
        console.Write(GuideView.Explain(step, command));
        return Choose(project is null ? "What now?" : $"What now for {Markup.Escape(project)}?", answers, a => Label(a, command));
    }

    public bool ConfirmApply(string command) => console.Confirm($"Apply it now ({Markup.Escape(command)})?", defaultValue: false);

    public void Tell(string message)
    {
        console.WriteLine();
        console.MarkupLine(Markup.Escape(message));
    }

    /// <summary>
    /// A selection list, or a numbered list and a number where the terminal cannot move the
    /// cursor (NO_COLOR and TERM=dumb turn ANSI off, and Spectre's selection prompt needs it).
    /// </summary>
    private T Choose<T>(string title, IReadOnlyList<T> choices, Func<T, string> label)
        where T : notnull
    {
        if (console.Profile.Capabilities.Ansi)
        {
            return console.Prompt(new SelectionPrompt<T>().Title(title).PageSize(15).AddChoices(choices).UseConverter(label));
        }

        console.MarkupLine(title);
        for (var i = 0; i < choices.Count; i++)
        {
            console.MarkupLine($"  {i + 1}) {label(choices[i])}");
        }

        var number = console.Prompt(new TextPrompt<int>("Number?")
            .Validate(n => n >= 1 && n <= choices.Count ? ValidationResult.Success() : ValidationResult.Error($"Pick 1 to {choices.Count}.")));
        return choices[number - 1];
    }

    private static string Label(GuideAnswer answer, string command) => answer switch
    {
        GuideAnswer.Run => $"Run it: {Markup.Escape(command)}",
        GuideAnswer.MarkDone => "Mark it done (I did it myself)",
        GuideAnswer.Skip => "Skip it",
        GuideAnswer.Back => "Back to the list",
        _ => QuitLabel,
    };
}
