using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Reporting;
using Offramp.Reporting.Graph;
using Offramp.Reporting.Report;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record ReportOptions(ReportFormat? Format, string? Since, string? Title, bool WithGraph);

/// <summary><c>offramp report</c>: the stakeholder progress page and its data (docs/spec/commands/workspace.md#report).</summary>
public sealed class ReportCommand : ICommandHandler<ReportOptions, ReportResult>, IRawOutput<ReportResult>
{
    public string CommandPath => "report";

    public JsonTypeInfo<ReportResult> ResultType => ReportingJsonContext.Default.ReportResult;

    /// <summary>The rendering is the primary output; --out receives it, not the envelope.</summary>
    public bool WritesOwnOutput => true;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var format = new Option<string?>("--format") { Description = "html (a self-contained page), json (the data behind the charts), or markdown. Inferred from --out's extension when omitted.", HelpName = "FORMAT" };
        format.AcceptOnlyFromAmong("html", "json", "markdown");
        var since = new Option<string?>("--since") { Description = "Start the burn-down at this date (2026-09-01) or UTC time.", HelpName = "DATE" };
        since.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string?>() is { } value && NormalizeSince(value) is null)
            {
                r.AddError($"'{value}' is not a date (yyyy-MM-dd) or an ISO 8601 time.");
            }
        });
        var title = new Option<string?>("--title") { Description = "The page title (default: report.title, else the repository folder's name).", HelpName = "TEXT" };
        var withGraph = new Option<bool>("--with-graph") { Description = "Embed the interactive dependency graph (HTML only)." };

        var command = new Command("report", "Summarize migration progress for stakeholders: headline numbers, the burn-down across scans, applications, and what can be ported today.")
        {
            format, since, title, withGraph,
        };
        command.Validators.Add(r =>
        {
            // GetResult, not GetValue: an option whose own validation failed must not throw here.
            var effective = r.GetResult(format) is { } given ? given.Tokens.LastOrDefault()?.Value
                : r.GetValue(globals.Out) is { } o ? ReportRenderer.FromExtension(o)?.ToString().ToLowerInvariant() : null;
            if (r.GetResult(format) is null && r.GetValue(globals.Out) is { } path && ReportRenderer.FromExtension(path) is null)
            {
                r.AddError($"Cannot tell the format of '{path}' from its extension; pass --format html|json|markdown.");
            }
            else if (r.GetValue(withGraph) && effective != "html")
            {
                r.AddError("--with-graph needs the html format (--format html or --out FILE.html).");
            }
        });
        command.SetAction((parse, ct) =>
        {
            var settings = globals.Bind(parse);
            var effective = parse.GetValue(format) is { } f ? Enum.Parse<ReportFormat>(f, ignoreCase: true)
                : settings.Out is { } o ? ReportRenderer.FromExtension(o) : null;
            var options = new ReportOptions(effective, parse.GetValue(since) is { } s ? NormalizeSince(s) : null, parse.GetValue(title), parse.GetValue(withGraph));
            return CommandRunner.RunAsync(new ReportCommand(), options, settings, host, ct);
        });
        HelpExamples.Add(command,
            "offramp report --out migration.html",
            "offramp report --out migration.html --with-graph --title \"Billing migration\"",
            "offramp report --format markdown --since 2026-07-01 > progress.md",
            "offramp report --format json");
        return command;
    }

    public async Task<CommandOutcome<ReportResult>> ExecuteAsync(ReportOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<ReportResult>.Environment();
        }

        var ledger = Ledger.ReadAll(Path.GetFullPath(config.Report.Ledger, root), root);
        foreach (var file in ledger.Unreadable)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0202, $"{file} is not a ledger snapshot; the burn-down leaves it out.",
                new DiagnosticLocation(File: file), [KeyValuePair.Create<string, JsonNode?>("file", file)]);
        }

        var title = options.Title ?? config.Report.Title ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var report = ReportBuilder.Build(model, ledger.Snapshots, title, options.Since);
        string? graphHtml = null;
        if (options.WithGraph)
        {
            var view = new GraphViewOptions { ExcludeKinds = [Core.Model.ProjectKind.Test], Highlight = GraphHighlightMode.Frontier };
            graphHtml = HtmlGraphWriter.Write(GraphView.Build(model, view), title + " dependency graph");
        }

        var summary = options.Format == ReportFormat.Markdown ? await SummaryAsync(report, context, cancellationToken) : null;
        var content = options.Format is { } format ? ReportRenderer.Render(report, format, graphHtml, summary) : null;
        string? output = null;
        if (context.Settings.Out is not null && content is not null)
        {
            var path = Path.GetFullPath(context.Settings.Out, context.Host.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            output = RepoPaths.ToRepositoryRelative(root, path);
            content = null;
        }

        return CommandOutcome<ReportResult>.Completed(new ReportResult
        {
            Format = options.Format,
            Output = output,
            Report = report,
            Content = content,
            Summary = summary,
        });
    }

    /// <summary>The Markdown summary: the template sentence, or with <c>llm.uses: summarizing</c> the model's paragraph from the same numbers.</summary>
    private static async Task<ReportSummary> SummaryAsync(ReportData report, CommandContext context, CancellationToken cancellationToken)
    {
        var template = new ReportSummary { Text = ReportText.Summary(report) };
        if (LlmGate.For(context, LlmGate.Summarizing) is not { } llm)
        {
            return template;
        }

        var h = report.Headline;
        var prompt = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            Write a two to four sentence executive summary of a .NET Framework to modern .NET migration, for managers. Plain text, no headings, no lists, no numbers that are not given here.

            Codebase: {report.Title}. Projects: {h.Projects}. Lines of code: {h.Loc}. Portable (standard, modern, or dual-targeted): {h.PortablePercent:0.#}%.
            Framework-only: {h.FrameworkProjects} projects, {h.FrameworkLoc} lines{ReportText.Change(report)}.
            Applications: {h.ApplicationsDone} of {h.Applications} done. Projects ready to port today: {h.Ready}.
            Largest areas: {string.Join(", ", report.Areas.OrderByDescending(a => a.Loc).Take(5).Select(a => a.Area))}.
            """);
        var text = await llm.AskTextAsync(new Offramp.Llm.LlmRequest { Use = LlmGate.Summarizing, Prompt = prompt, MaxTokens = 400 }, "the report summary", cancellationToken);
        return text is { Length: > 0 and <= 2000 } && !text.Contains("\n#", StringComparison.Ordinal) ? new ReportSummary { Text = text, Source = LlmGate.Source } : template;
    }

    public string? RawOutput(ReportResult result, CommandContext context) => result.Content;

    public void Render(ReportResult result, CommandContext context, HumanOutput output)
    {
        var report = result.Report;
        var h = report.Headline;
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{h.PortablePercent:0.#}% of {h.Loc:N0} lines portable; {h.FrameworkLoc:N0} framework-only lines in {h.FrameworkProjects} project{(h.FrameworkProjects == 1 ? "" : "s")}{ReportText.Change(report)}.");
        if (result.Output is not null)
        {
            output.Headline($"Wrote {result.Output}: {summary}", Theme.ReadyStyle);
            return;
        }

        output.Headline(summary, h.FrameworkProjects == 0 ? Theme.ReadyStyle : Theme.DecisionStyle);
        if (report.Applications.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Application");
            table.AddColumn("Status");
            table.AddColumn(new TableColumn("Projects left").RightAligned());
            table.AddColumn(new TableColumn("Lines left").RightAligned());
            table.AddColumn("Port next");
            foreach (var app in report.Applications)
            {
                table.AddRow(
                    new Markup(Markup.Escape(app.Name)),
                    new Markup(Status(app.Status)),
                    new Markup(string.Create(CultureInfo.InvariantCulture, $"{app.Remaining} of {app.Closure}")),
                    new Markup(app.RemainingLoc.ToString("N0", CultureInfo.InvariantCulture)),
                    new Markup(Markup.Escape(string.Join(", ", app.Next))));
            }

            output.Write(table);
        }

        if (report.Frontier.Count > 0)
        {
            output.MarkupLine($"[dim]Ready to port today ({report.Frontier.Count}):[/]");
            foreach (var project in report.Frontier.Take(10))
            {
                output.MarkupLine($"  {Markup.Escape(project.Project)} [dim]({project.Loc:N0} lines, {project.Dependents} dependents)[/]");
            }
        }

        output.MarkupLine(report.Series.Count == 1
            ? "[dim]One scan in the ledger so far; commit .offramp/ledger so the burn-down grows with each scan.[/]"
            : $"[dim]Burn-down over {report.Series.Count} scans since {Markup.Escape(ReportText.Moment(report.Series[0].CreatedAt))}.[/]");
        output.MarkupLine("[dim]Page:[/] offramp report --out report.html");
    }

    /// <summary>A date stays a date (compared as a prefix); a time becomes UTC <c>yyyy-MM-ddTHH:mm:ssZ</c> like snapshot times; else null.</summary>
    internal static string? NormalizeSince(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value.Contains('T', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;
    }

    private static string Status(ProjectReadiness status) => status switch
    {
        ProjectReadiness.Done => "[dim]done[/]",
        ProjectReadiness.Ready => $"[{Theme.ReadyStyle}]ready[/]",
        _ => $"[{Theme.BlockingStyle}]blocked[/]",
    };
}
