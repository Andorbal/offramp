using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.Conditional;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Conditional;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp ifdef</c>: conditional compilation used to bridge targets (docs/spec/commands/audit.md#ifdef).</summary>
public static class IfdefCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var ifdef = new Command("ifdef", "Conditional compilation that bridges targets: how much there is, wrapping audit findings in #if, and stripping regions when a target is dropped.");
        ifdef.Subcommands.Add(IfdefReportCommand.Create(host, globals));
        ifdef.Subcommands.Add(IfdefWrapCommand.Create(host, globals));
        ifdef.Subcommands.Add(IfdefStripCommand.Create(host, globals));
        return ifdef;
    }

    internal static Core.Model.WorkspaceModel? Load(CommandContext context) =>
        WorkspaceStore.LoadForCommand(context.WorkspacePath, context.Repository.Path, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);

    internal static Option<string> SymbolOption(string description, bool required) => new("--symbol")
    {
        Description = description,
        HelpName = "SYMBOL",
        Required = required,
        DefaultValueFactory = required ? null! : _ => "NETFRAMEWORK",
    };
}

public sealed record IfdefReportOptions(string? Symbol);

/// <summary><c>offramp ifdef report</c>.</summary>
public sealed class IfdefReportCommand : ICommandHandler<IfdefReportOptions, IfdefReportResult>
{
    public string CommandPath => "ifdef report";

    public JsonTypeInfo<IfdefReportResult> ResultType => AnalysisJsonContext.Default.IfdefReportResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var symbol = new Option<string?>("--symbol") { Description = "Only regions whose conditions name this symbol (NETFRAMEWORK, NET10_0_OR_GREATER, ...).", HelpName = "SYMBOL" };
        var command = new Command("report", "Count #if regions and the lines inside them, per symbol and project.") { symbol };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new IfdefReportCommand(), new IfdefReportOptions(parse.GetValue(symbol)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command, "offramp ifdef report", "offramp ifdef report --symbol NETFRAMEWORK --json");
        return command;
    }

    public Task<CommandOutcome<IfdefReportResult>> ExecuteAsync(IfdefReportOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        if (IfdefCommands.Load(context) is not { } model)
        {
            return Task.FromResult(CommandOutcome<IfdefReportResult>.Environment());
        }

        return Task.FromResult(CommandOutcome<IfdefReportResult>.Completed(IfdefReporter.Report(context.Repository.Path, model, options.Symbol)));
    }

    public void Render(IfdefReportResult result, CommandContext context, HumanOutput output)
    {
        if (result.Totals.Count == 0)
        {
            output.Headline(result.Symbol is null ? "No conditional regions." : $"No regions name {result.Symbol}.", Theme.ReadyStyle);
            return;
        }

        var lines = result.Totals.Sum(t => t.Lines);
        output.Headline(string.Create(CultureInfo.InvariantCulture, $"{result.Totals.Sum(t => t.Regions)} regions across {result.Totals.Count} symbols guard {lines} lines."), Theme.DecisionStyle);
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Project");
        table.AddColumn("Symbol");
        table.AddColumn(new TableColumn("Regions").RightAligned());
        table.AddColumn(new TableColumn("Lines").RightAligned());
        table.AddColumn(new TableColumn("Files").RightAligned());
        foreach (var project in result.Projects)
        {
            foreach (var symbol in project.Symbols)
            {
                table.AddRow(Markup.Escape(project.Project), Markup.Escape(symbol.Symbol), Number(symbol.Regions), Number(symbol.Lines), Number(symbol.Files));
            }
        }

        foreach (var total in result.Totals)
        {
            table.AddRow("[bold]total[/]", Markup.Escape(total.Symbol), Number(total.Regions), Number(total.Lines), Number(total.Files));
        }

        output.Write(table);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed record IfdefWrapOptions(string Findings, string Condition);

/// <summary><c>offramp ifdef wrap</c>.</summary>
public sealed class IfdefWrapCommand : ICommandHandler<IfdefWrapOptions, IfdefWrapResult>
{
    public string CommandPath => "ifdef wrap";

    public JsonTypeInfo<IfdefWrapResult> ResultType => RefactoringJsonContext.Default.IfdefWrapResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var findings = new Option<string>("--findings") { Description = "An audit result: the output of `offramp audit api --format json` (or its --json envelope).", HelpName = "FILE", Required = true };
        var symbol = IfdefCommands.SymbolOption("The condition to write after #if (NETFRAMEWORK, or !NET10_0_OR_GREATER).", required: false);
        var command = new Command("wrap", "Wrap the statements or members behind audit findings in #if … #endif, inserting whole lines only; members code on every target needs are left for a real port (OFR3601).")
        {
            findings, symbol,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new IfdefWrapCommand(), new IfdefWrapOptions(parse.GetValue(findings)!, parse.GetValue(symbol) ?? "NETFRAMEWORK"), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp audit api --format json --out audit.json && offramp ifdef wrap --findings audit.json",
            "offramp ifdef wrap --findings audit.json --symbol '!NET10_0_OR_GREATER' --apply");
        return command;
    }

    public async Task<CommandOutcome<IfdefWrapResult>> ExecuteAsync(IfdefWrapOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        if (IfdefCommands.Load(context) is not { } model)
        {
            return CommandOutcome<IfdefWrapResult>.Environment();
        }

        var path = Path.GetFullPath(options.Findings, context.Host.WorkingDirectory);
        IReadOnlyList<WrapFinding> findings;
        try
        {
            findings = WrapFinding.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR3604, $"'{options.Findings}' is not a readable audit result: {ex.Message}",
                data: [KeyValuePair.Create<string, JsonNode?>("path", options.Findings)]);
            return CommandOutcome<IfdefWrapResult>.Usage();
        }

        var root = context.Repository.Path;
        var plan = IfdefWrapPlanner.Plan(new IfdefWrapRequest
        {
            RepositoryRoot = root,
            Model = model,
            Findings = findings,
            Condition = options.Condition,
            Diagnostics = context.Diagnostics,
        });
        if (!context.Settings.Apply || context.Settings.DryRun || plan.ChangeSet is null)
        {
            return CommandOutcome<IfdefWrapResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "ifdef wrap", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<IfdefWrapResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(IfdefWrapResult result, CommandContext context, HumanOutput output)
    {
        var style = result.NotWrapped.Count > 0 ? Theme.DecisionStyle : Theme.ReadyStyle;
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Wraps.Count} range{(result.Wraps.Count == 1 ? "" : "s")} wrapped in #if {result.Condition} for {result.Findings} finding{(result.Findings == 1 ? "" : "s")}; {result.NotWrapped.Count} not wrapped, {result.AlreadyGuarded} already guarded."), style);
        foreach (var wrap in result.Wraps)
        {
            output.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"  [{Theme.ReadyStyle}]{wrap.Kind}[/] {Markup.Escape(wrap.File)}:{wrap.StartLine}-{wrap.EndLine} [dim]{Markup.Escape(wrap.Target)} ({Markup.Escape(string.Join(", ", wrap.Rules))})[/]"));
        }

        IfdefRendering.Tail(result.Applied, result.Journal, result.Preview, output);
    }
}

public sealed record IfdefStripOptions(string Symbol, bool Keep);

/// <summary><c>offramp ifdef strip</c>.</summary>
public sealed class IfdefStripCommand : ICommandHandler<IfdefStripOptions, IfdefStripResult>
{
    public string CommandPath => "ifdef strip";

    public JsonTypeInfo<IfdefStripResult> ResultType => RefactoringJsonContext.Default.IfdefStripResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var symbol = IfdefCommands.SymbolOption("The symbol whose regions go (NETFRAMEWORK when net48 is dropped).", required: true);
        var keep = new Option<string>("--keep") { Description = "Keep the branch for the symbol defined (true) or not defined (false).", HelpName = "true|false", DefaultValueFactory = _ => "false" };
        keep.AcceptOnlyFromAmong("true", "false");
        var command = new Command("strip", "Remove the #if regions that name a symbol, keeping the branch selected by --keep; regions that also depend on other symbols stay (OFR3603).")
        {
            symbol, keep,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new IfdefStripCommand(),
            new IfdefStripOptions(parse.GetValue(symbol)!, parse.GetValue(keep) == "true"), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp ifdef strip --symbol NETFRAMEWORK",
            "offramp ifdef strip --symbol NETFRAMEWORK --keep false --apply");
        return command;
    }

    public async Task<CommandOutcome<IfdefStripResult>> ExecuteAsync(IfdefStripOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        if (IfdefCommands.Load(context) is not { } model)
        {
            return CommandOutcome<IfdefStripResult>.Environment();
        }

        var root = context.Repository.Path;
        var plan = IfdefStripPlanner.Plan(new IfdefStripRequest
        {
            RepositoryRoot = root,
            Model = model,
            Symbol = options.Symbol,
            Keep = options.Keep,
            Diagnostics = context.Diagnostics,
        });
        if (!context.Settings.Apply || context.Settings.DryRun || plan.ChangeSet is null)
        {
            return CommandOutcome<IfdefStripResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "ifdef strip", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<IfdefStripResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(IfdefStripResult result, CommandContext context, HumanOutput output)
    {
        var style = result.NotStripped.Count > 0 ? Theme.DecisionStyle : Theme.ReadyStyle;
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Stripped.Count} region{(result.Stripped.Count == 1 ? "" : "s")} naming {result.Symbol} stripped as if it were {(result.Keep ? "defined" : "undefined")}, in {result.Files.Count} file{(result.Files.Count == 1 ? "" : "s")}; {result.NotStripped.Count} left."), style);
        foreach (var region in result.Stripped)
        {
            output.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"  {Markup.Escape(region.File)}:{region.Line} [dim]#if {Markup.Escape(region.Condition)} → kept {region.Kept}, {region.RemovedLines} lines removed[/]"));
        }

        IfdefRendering.Tail(result.Applied, result.Journal, result.Preview, output);
    }
}

internal static class IfdefRendering
{
    public static void Tail(bool applied, string? journal, string? preview, HumanOutput output)
    {
        if (applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(journal ?? "")}");
            return;
        }

        if (preview is { Length: > 0 })
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);
            output.Line();
            output.MarkupLine("[dim]Dry run. Apply with[/] --apply");
        }
    }
}
