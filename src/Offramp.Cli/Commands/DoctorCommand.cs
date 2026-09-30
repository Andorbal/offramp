using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Workspace;
using Offramp.Workspace.Doctor;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record DoctorOptions(bool Fix = false);

/// <summary><c>offramp doctor</c>: environment and repository checks (<c>docs/spec/commands/workspace.md#doctor</c>).</summary>
public sealed class DoctorCommand : ICommandHandler<DoctorOptions, DoctorReport>, INextStep<DoctorReport>, IRendersOwnDiagnostics, IInteractiveCommand
{
    public string CommandPath => "doctor";

    public JsonTypeInfo<DoctorReport> ResultType => WorkspaceJsonContext.Default.DoctorReport;

    public bool RunsWithInvalidConfig => true;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var fix = new Option<bool>("--fix")
        {
            Description = "Show the compile-only block for Windows-only build steps as a diff to Directory.Build.props, and the conditions for the Windows-only settings project files set themselves; with --apply, write them.",
        };
        var command = new Command("doctor", "Check SDKs, reference assemblies, git, offramp.yml, the workspace model, Windows-only build steps, and central package management, and explain how to fix what is wrong.")
        {
            fix,
        };
        command.SetAction((parse, ct) =>
            CommandRunner.RunAsync(new DoctorCommand(), new DoctorOptions(parse.GetValue(fix)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp doctor",
            "offramp doctor --json > doctor.json",
            "offramp doctor --fix",
            "offramp doctor --fix --apply --yes");
        return command;
    }

    public async Task<CommandOutcome<DoctorReport>> ExecuteAsync(
        DoctorOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var report = await DoctorRunner.RunAsync(new DoctorContext
        {
            Repository = context.Repository,
            Config = context.Config,
            WorkspacePath = context.WorkspacePath,
            Processes = context.Host.Processes,
            Git = context.Host.GitService,
            ReferenceAssemblies = context.Host.ReferenceAssemblies,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Os = context.Host.Os,
            Fix = options.Fix,
        }, cancellationToken);

        if (report.Fix is { HasChanges: true } plan && context.Settings.Apply && Confirm(plan, context))
        {
            report = report with { Fix = DoctorRunner.ApplyFix(context.Repository.Path, [.. plan.ProjectFiles.Select(f => f.File)], plan.PackagesConfig) };
        }

        return CommandOutcome<DoctorReport>.Completed(report);
    }

    public void Render(DoctorReport result, CommandContext context, HumanOutput output)
    {
        var summary = result.Summary;
        if (summary.Fail > 0)
        {
            output.Headline($"Doctor found {Plural(summary.Fail, "failing check")}{(summary.Warn > 0 ? $" and {Plural(summary.Warn, "warning")}" : "")}.", Theme.BlockingStyle);
        }
        else if (summary.Warn > 0)
        {
            output.Headline($"Doctor passed with {Plural(summary.Warn, "warning")}.", Theme.DecisionStyle);
        }
        else
        {
            output.Headline("Everything Offramp needs is in place.", Theme.ReadyStyle);
        }

        output.Line();
        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn("").NoWrap());
        table.AddColumn(new TableColumn("Check").NoWrap());
        table.AddColumn(new TableColumn("Result"));
        foreach (var check in result.Checks)
        {
            var detail = Markup.Escape(check.Message);
            if (check.Remedy is not null && check.Status is CheckStatus.Fail or CheckStatus.Warn)
            {
                detail += $"\n[dim]{Markup.Escape("→ " + check.Remedy)}[/]";
            }

            var configDiagnostics = check.Id == "config"
                ? context.Config.Diagnostics.Where(d => d.Severity > Offramp.Core.Diagnostics.Severity.Info)
                : [];
            foreach (var d in configDiagnostics)
            {
                var where = d.Line is null ? "" : $"{d.File}:{d.Line}: ";
                detail += $"\n[{HumanOutput.Style(d.Severity)}]{Markup.Escape($"{d.Code} {where}{d.Message}")}[/]";
            }

            table.AddRow(new Markup(StatusIcon(check.Status, output.Unicode)), new Markup(Markup.Escape(check.Title)), new Markup(detail));
        }

        output.Write(table);
        RenderFix(result.Fix, output);
    }

    private static bool Confirm(CompileOnlyFix plan, CommandContext context)
    {
        if (context.Settings.Yes || !context.Interactive)
        {
            return true;
        }

        var console = ConsoleFactory.Create(context.Host, context.Host.Out, isTerminal: true);
        var output = new HumanOutput(console);
        foreach (var diff in Diffs(plan))
        {
            DiffRenderer.Render(output, diff);
        }

        return console.Confirm($"Write {Changes(plan)}?", defaultValue: false);
    }

    /// <summary>The block's diff first, then each project file's, as the fix writes them.</summary>
    private static IEnumerable<string> Diffs(CompileOnlyFix fix) =>
        new[] { fix.AlreadyPresent ? null : fix.Diff }
            .Concat(fix.PackagesConfigFiles.Where(f => !f.Applied).Select(f => f.Diff))
            .Concat(fix.ProjectFiles.Where(f => !f.Applied).Select(f => f.Diff))
            .OfType<string>();

    private static string Changes(CompileOnlyFix fix)
    {
        var guards = fix.ProjectFiles.Where(f => !f.Applied).ToList();
        var parts = new List<string>();
        if (!fix.AlreadyPresent)
        {
            parts.Add($"the compile-only block to {fix.File}");
        }

        var restore = fix.PackagesConfigFiles.Where(f => !f.Applied).Select(f => f.File).ToList();
        if (restore.Count > 0)
        {
            parts.Add($"the packages.config restore ({string.Join(", ", restore)})");
        }

        if (guards.Count > 0)
        {
            parts.Add($"{Plural(guards.Sum(f => f.Guards.Count), "Windows condition")} to {Plural(guards.Count, "project file")}");
        }

        return parts.Count <= 2 ? string.Join(" and ", parts) : string.Join(", ", parts[..^1]) + ", and " + parts[^1];
    }

    private static void RenderFix(CompileOnlyFix? fix, HumanOutput output)
    {
        if (fix is null)
        {
            return;
        }

        output.Line();
        var written = fix.ProjectFiles.Where(f => f.Applied).ToList();
        var restore = fix.PackagesConfigFiles.Where(f => f.Applied).ToList();
        if (fix.Applied || written.Count > 0 || restore.Count > 0)
        {
            var parts = new List<string>();
            if (fix.Applied)
            {
                parts.Add($"the compile-only block to {fix.File}");
            }

            if (restore.Count > 0)
            {
                parts.Add($"the packages.config restore ({string.Join(", ", restore.Select(f => f.File))})");
            }

            if (written.Count > 0)
            {
                parts.Add($"{Plural(written.Sum(f => f.Guards.Count), "Windows condition")} to {string.Join(", ", written.Select(f => f.File))}");
            }

            var added = parts.Count <= 2 ? string.Join(" and ", parts) : string.Join(", ", parts[..^1]) + ", and " + parts[^1];
            output.MarkupLine($"[{Theme.ReadyStyle}]Added {Markup.Escape(added)}.[/] Commit them with your next change.");
            return;
        }

        if (!fix.HasChanges)
        {
            output.MarkupLine($"[{Theme.ReadyStyle}]{Markup.Escape(fix.File)} already has the compile-only block, and every Windows-only setting in the project files has a condition.[/]");
            return;
        }

        output.MarkupLine($"[{Theme.DecisionStyle}]Dry run:[/] this would add {Markup.Escape(Changes(fix))} (re-run with --apply to write it):");
        foreach (var diff in Diffs(fix))
        {
            DiffRenderer.Render(output, diff);
        }
    }

    public string? NextStep(DoctorReport result, CommandContext context)
    {
        if (result.Fix is { HasChanges: true } fix && !fix.Applied && fix.ProjectFiles.All(f => !f.Applied) && fix.PackagesConfigFiles.All(f => !f.Applied))
        {
            return "offramp doctor --fix --apply";
        }

        if (result.Summary.Fail > 0)
        {
            return null;
        }

        if (context.Config.File is null)
        {
            return "offramp init";
        }

        return result.Checks.Any(c => c.Id == "workspace" && c.Status == CheckStatus.Warn) ? "offramp scan" : null;
    }

    private static string StatusIcon(CheckStatus status, bool unicode) => status switch
    {
        CheckStatus.Pass => $"[{Theme.ReadyStyle}]{(unicode ? "✓" : "ok")}[/]",
        CheckStatus.Warn => $"[{Theme.DecisionStyle}]![/]",
        CheckStatus.Fail => $"[{Theme.BlockingStyle}]{(unicode ? "✗" : "x")}[/]",
        _ => "[dim]-[/]",
    };

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
