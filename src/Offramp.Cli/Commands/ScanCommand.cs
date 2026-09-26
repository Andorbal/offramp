using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Workspace;
using Offramp.Workspace.Scanning;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record ScanOptions(string? Binlog, string? Complog, bool IfStale, bool NoBuild);

/// <summary><c>offramp scan</c>: builds the workspace model (docs/spec/commands/workspace.md#scan).</summary>
public sealed class ScanCommand : ICommandHandler<ScanOptions, ScanResult>, INextStep<ScanResult>
{
    public string CommandPath => "scan";

    public JsonTypeInfo<ScanResult> ResultType => WorkspaceJsonContext.Default.ScanResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var binlog = new Option<string?>("--binlog")
        {
            Description = "Read an existing MSBuild binary log instead of building (for example one captured on Windows).",
            HelpName = "PATH",
        };
        var complog = new Option<string?>("--complog")
        {
            Description = "Use a compiler log (from `complog create`). Alone it gives a reduced model; with --binlog, the full one.",
            HelpName = "PATH",
        };
        var ifStale = new Option<bool>("--if-stale") { Description = "Rescan only when the model is stale." };
        var noBuild = new Option<bool>("--no-build") { Description = "Reuse the previous scan's binary log instead of building." };
        var command = new Command("scan", "Build the workspace model (.offramp/workspace.json) from a build of the solution or from logs, and record a ledger snapshot.")
        {
            binlog, complog, ifStale, noBuild,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(noBuild) && (result.GetValue(binlog) is not null || result.GetValue(complog) is not null))
            {
                result.AddError("--no-build cannot be combined with --binlog or --complog.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new ScanCommand(),
            new ScanOptions(parse.GetValue(binlog), parse.GetValue(complog), parse.GetValue(ifStale), parse.GetValue(noBuild)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp scan",
            "offramp scan --solution src/Monolith.sln",
            "offramp scan --binlog windows.binlog --complog windows.complog",
            "offramp scan --if-stale --json");
        return command;
    }

    public async Task<CommandOutcome<ScanResult>> ExecuteAsync(ScanOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var cwd = context.Host.WorkingDirectory;
        var outcome = await ScanRunner.RunAsync(new ScanRequest
        {
            RepositoryRoot = context.Repository.Path,
            Config = context.Config.Config,
            WorkspacePath = context.WorkspacePath,
            BinlogPath = options.Binlog is null ? null : Path.GetFullPath(options.Binlog, cwd),
            ComplogPath = options.Complog is null ? null : Path.GetFullPath(options.Complog, cwd),
            IfStale = options.IfStale,
            NoBuild = options.NoBuild,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Time = context.Host.Time,
        }, cancellationToken);

        return outcome.Failure switch
        {
            ScanFailure.Usage => CommandOutcome<ScanResult>.Usage(),
            ScanFailure.Environment => CommandOutcome<ScanResult>.Environment(),
            _ => CommandOutcome<ScanResult>.Completed(outcome.Result!),
        };
    }

    public void Render(ScanResult result, CommandContext context, HumanOutput output)
    {
        if (result.UpToDate)
        {
            output.Headline($"The workspace model is up to date ({result.Projects} projects); nothing was rescanned.", Theme.ReadyStyle);
            return;
        }

        var c = result.ByFrameworkClass;
        var style = result.BuildSucceeded == false ? Theme.DecisionStyle : Theme.ReadyStyle;
        output.Headline(
            $"Scanned {result.Projects} projects ({result.Loc:N0} lines): {c.GetValueOrDefault("framework")} framework, {c.GetValueOrDefault("standard")} standard, {c.GetValueOrDefault("modern")} modern, {c.GetValueOrDefault("dual")} dual.",
            style);
        output.Line();

        var classes = new Table().Border(TableBorder.None).HideHeaders();
        classes.AddColumn("Class");
        classes.AddColumn(new TableColumn("Projects").RightAligned());
        foreach (var (name, color) in new[] { ("framework", Theme.FrameworkOrange), ("standard", Theme.StandardBlue), ("modern", Theme.ModernGreen), ("dual", Theme.DualTeal) })
        {
            classes.AddRow(new Markup($"[{color.ToMarkup()}]■[/] {name}"), new Markup(c.GetValueOrDefault(name).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        output.Write(classes);
        var kinds = string.Join(", ", result.ByKind.Where(k => k.Value > 0).Select(k => $"{k.Value} {k.Key}"));
        output.MarkupLine($"[dim]Kinds:[/] {Markup.Escape(kinds)}");

        if (result.Cycles.Count > 0)
        {
            output.Line();
            output.MarkupLine($"[{Theme.DecisionStyle}]Cycles:[/]");
            foreach (var cycle in result.Cycles)
            {
                output.MarkupLine("  " + Markup.Escape(string.Join(" ↔ ", cycle)));
            }
        }

        if (result.WindowsOnlyBuildSteps.Count > 0)
        {
            output.Line();
            output.MarkupLine($"[{Theme.DecisionStyle}]Windows-only build steps:[/]");
            foreach (var project in result.WindowsOnlyBuildSteps)
            {
                output.MarkupLine($"  {Markup.Escape(project.Project)}: {Markup.Escape(string.Join(", ", project.Steps))}");
            }
        }

        output.Line();
        output.MarkupLine($"[dim]Model:[/] {Markup.Escape(result.Model)}  [dim]Ledger:[/] {Markup.Escape(result.LedgerSnapshot ?? "-")}");
    }

    public string? NextStep(ScanResult result, CommandContext context) =>
        result.WindowsOnlyBuildSteps.Count > 0 ? "offramp doctor --fix" : null;
}
