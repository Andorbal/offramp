using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Workspace;
using Offramp.Workspace.Doctor;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record DoctorOptions;

/// <summary><c>offramp doctor</c>: environment and repository checks (<c>docs/spec/commands/workspace.md#doctor</c>).</summary>
public sealed class DoctorCommand : ICommandHandler<DoctorOptions, DoctorReport>, INextStep<DoctorReport>, IRendersOwnDiagnostics
{
    public string CommandPath => "doctor";

    public JsonTypeInfo<DoctorReport> ResultType => WorkspaceJsonContext.Default.DoctorReport;

    public bool RunsWithInvalidConfig => true;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("doctor", "Check SDKs, reference assemblies, git, and offramp.yml, and explain how to fix what is wrong. Read-only.");
        command.SetAction((parse, ct) =>
            CommandRunner.RunAsync(new DoctorCommand(), new DoctorOptions(), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp doctor",
            "offramp doctor --json > doctor.json",
            "offramp doctor --target 9");
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
        }, cancellationToken);
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
    }

    public string? NextStep(DoctorReport result, CommandContext context)
    {
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
