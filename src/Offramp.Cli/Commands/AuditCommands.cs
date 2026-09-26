using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.Audits;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Paths;
using Offramp.Reporting.Audit;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp audit</c>: read-only code audits (docs/spec/commands/audit.md).</summary>
public static class AuditCommands
{
    private static readonly string[] Packs = ["core", "web", "desktop", "data", "serialization", "native"];

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var audit = new Command("audit", "Read-only code audits: what will not compile or will throw on the target, what behaves differently, binary serialization, and native interop.");
        audit.Subcommands.Add(Kind(host, globals, AuditKind.Api, "api",
            "APIs missing on the target (compiling each .NET Framework project against the target's reference assemblies), Windows-only APIs, APIs that throw, and removed technologies; with a porting ledger.",
            "offramp audit api", "offramp audit api --target 8 --format sarif --out audit-api.sarif", "offramp audit api --project Billing.Core --group-by namespace"));
        audit.Subcommands.Add(Kind(host, globals, AuditKind.Behavior, "behavior",
            "Code that compiles on the target but behaves differently: culture, encodings, paths, time zones, the registry, ambient contexts, and more.",
            "offramp audit behavior", "offramp audit behavior --pack core --all-locations", "offramp audit behavior --format markdown > docs/behavior-audit.md"));
        audit.Subcommands.Add(Kind(host, globals, AuditKind.Serialization, "serialization",
            "BinaryFormatter and its relatives: whether the data stays in the process (a deep clone) or outlives it, and which types it carries.",
            "offramp audit serialization", "offramp audit serialization --json | jq '.result.findings[] | select(.rule == \"OFR3203\")'"));
        audit.Subcommands.Add(Kind(host, globals, AuditKind.Native, "native",
            "P/Invoke inventory, marshalling defaults that differ, LibraryImport candidates, and COM.",
            "offramp audit native", "offramp audit native --format sarif --out native.sarif"));
        audit.Subcommands.Add(AuditDeadCodeCommand.Create(host, globals));
        audit.Subcommands.Add(AuditApiCompatCommand.Create(host, globals));
        return audit;
    }

    private static Command Kind(CliHost host, GlobalOptions globals, AuditKind kind, string name, string description, params string[] examples)
    {
        var project = new Option<string[]>("--project") { Description = "Audit only these projects (path or name). Repeatable.", HelpName = "PROJECT", AllowMultipleArgumentsPerToken = false };
        var pack = new Option<string[]>("--pack") { Description = $"Run only these rule packs ({string.Join(", ", Packs)}). Repeatable; overrides rules.packs.disable.", HelpName = "NAME" };
        pack.Validators.Add(r =>
        {
            foreach (var value in r.GetValueOrDefault<string[]>() ?? [])
            {
                if (!Packs.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    r.AddError($"'{value}' is not a rule pack; use one of {string.Join(", ", Packs)}.");
                }
            }
        });
        var format = new Option<string?>("--format") { Description = "table (the terminal view), json (the result alone), sarif, or markdown. Inferred from --out's extension when omitted.", HelpName = "FORMAT" };
        format.AcceptOnlyFromAmong("table", "json", "sarif", "markdown");
        var groupBy = new Option<string>("--group-by") { Description = "Group findings by rule, project, namespace, or file.", DefaultValueFactory = _ => "rule" };
        groupBy.AcceptOnlyFromAmong("rule", "project", "namespace", "file");
        var all = new Option<bool>("--all-locations") { Description = $"List every location (default: the first {AuditCommand.DefaultLocations} per group)." };

        var command = new Command(name, description) { project, pack, format, groupBy, all };
        command.Validators.Add(r =>
        {
            if (r.GetResult(format) is null && r.GetValue(globals.Out) is { } o && FromExtension(o) is null)
            {
                r.AddError($"Cannot tell the format of '{o}' from its extension; pass --format json|sarif|markdown|table.");
            }
        });
        command.SetAction((parse, ct) =>
        {
            var settings = globals.Bind(parse);
            var chosen = parse.GetValue(format) ?? (settings.Out is { } o ? FromExtension(o) : null) ?? "table";
            var options = new AuditOptions(
                kind,
                parse.GetValue(project) ?? [],
                [.. (parse.GetValue(pack) ?? []).Select(p => p.ToLowerInvariant()).Distinct(StringComparer.Ordinal)],
                chosen,
                Enum.Parse<AuditGrouping>(parse.GetValue(groupBy) ?? "rule", ignoreCase: true),
                parse.GetValue(all));
            return CommandRunner.RunAsync(new AuditCommand(kind, chosen), options, settings, host, ct);
        });
        HelpExamples.Add(command, examples);
        return command;
    }

    /// <summary><c>.sarif</c> → sarif, <c>.json</c> → json, <c>.md</c> → markdown; null for anything else.</summary>
    public static string? FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".sarif" => "sarif",
        ".json" => path.EndsWith(".sarif.json", StringComparison.OrdinalIgnoreCase) ? "sarif" : "json",
        ".md" or ".markdown" => "markdown",
        ".txt" => "table",
        _ => null,
    };
}

public sealed record AuditOptions(AuditKind Kind, IReadOnlyList<string> Projects, IReadOnlyList<string> Packs, string Format, AuditGrouping GroupBy, bool AllLocations);

/// <summary><c>offramp audit api|behavior|serialization|native</c>.</summary>
public sealed class AuditCommand(AuditKind kind, string format) : ICommandHandler<AuditOptions, AuditResult>, IRawOutput<AuditResult>, IRendersOwnDiagnostics
{
    public const int DefaultLocations = 5;

    private AuditOptions? _options;

    public string CommandPath => "audit " + AuditRunner.Wire(kind);

    public JsonTypeInfo<AuditResult> ResultType => AnalysisJsonContext.Default.AuditResult;

    /// <summary>A SARIF, Markdown, or JSON document is the primary output: --out receives it, not the envelope.</summary>
    public bool WritesOwnOutput => format != "table";

    public async Task<CommandOutcome<AuditResult>> ExecuteAsync(AuditOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        _options = options;
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<AuditResult>.Environment();
        }

        var projects = new List<string>();
        foreach (var value in options.Projects)
        {
            if (ProjectLookup.Resolve(value, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                return CommandOutcome<AuditResult>.Usage();
            }

            projects.Add(id);
        }

        var result = await AuditRunner.RunAsync(new AuditRequest
        {
            RepositoryRoot = root,
            Model = model,
            Audit = options.Kind,
            TargetMajor = config.Target,
            Projects = projects,
            Packs = options.Packs,
            DisabledPacks = config.Rules.Packs.Disable,
            Overrides = config.Rules.ToSeverityOverrides(),
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            References = options.Kind == AuditKind.Api ? new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)) : null,
        }, cancellationToken);

        if (context.Settings.Out is not null && format != "table")
        {
            var path = Path.GetFullPath(context.Settings.Out, context.Host.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Document(result, options), new System.Text.UTF8Encoding(false));
            result = result with { Output = RepoPaths.ToRepositoryRelative(root, path) };
        }

        return CommandOutcome<AuditResult>.Completed(result);
    }

    /// <summary>--format json|sarif|markdown without --out prints the document alone (the envelope needs --json).</summary>
    public string? RawOutput(AuditResult result, CommandContext context) =>
        format == "table" || result.Output is not null || _options is null ? null : Document(result, _options);

    private string Document(AuditResult result, AuditOptions options) => format switch
    {
        "sarif" => AuditSarifWriter.Write(result, OfframpVersion.Current),
        "markdown" => AuditMarkdownWriter.Write(result, options.GroupBy, options.AllLocations ? null : DefaultLocations),
        _ => OfframpJson.Serialize(result, AnalysisJsonContext.Default.AuditResult),
    };

    public void Render(AuditResult result, CommandContext context, HumanOutput output)
    {
        var errors = result.Findings.Count(f => f.Severity == Severity.Error);
        var warnings = result.Findings.Count(f => f.Severity == Severity.Warning);
        var info = result.Findings.Count(f => f.Severity == Severity.Info);
        var style = errors > 0 ? Theme.BlockingStyle : warnings > 0 ? Theme.DecisionStyle : Theme.ReadyStyle;
        if (result.Output is not null)
        {
            output.Headline($"Wrote {result.Output}: {Counts(result.Findings.Count, errors, warnings, info)}.", style);
            return;
        }

        output.Headline($"audit {AuditRunner.Wire(result.Audit)} for {result.Target}: {Counts(result.Findings.Count, errors, warnings, info)} in {Plural(result.Projects.Count, "project")}.", style);
        if (result.Summary.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn(new TableColumn("Rule").NoWrap());
            table.AddColumn(new TableColumn("Severity").NoWrap());
            table.AddColumn("Title");
            table.AddColumn(new TableColumn("Findings").RightAligned());
            table.AddColumn(new TableColumn("Projects").RightAligned());
            foreach (var rule in result.Summary)
            {
                table.AddRow(
                    new Markup(Markup.Escape(rule.Rule)),
                    new Markup($"[{Color(rule.Severity)}]{rule.Severity.ToWire()}[/]"),
                    new Markup(Markup.Escape(rule.Title)),
                    new Markup(rule.Findings.ToString(CultureInfo.InvariantCulture)),
                    new Markup(rule.Projects.ToString(CultureInfo.InvariantCulture)));
            }

            output.Write(table);
        }

        if (result.Ledger.Count > 0)
        {
            var ledger = new Table().Border(TableBorder.Simple);
            ledger.AddColumn("Project");
            ledger.AddColumn(new TableColumn("Files").RightAligned());
            ledger.AddColumn(new TableColumn("Portable").RightAligned());
            ledger.AddColumn(new TableColumn("Portability").RightAligned());
            foreach (var project in result.Ledger)
            {
                ledger.AddRow(
                    Markup.Escape(project.Project),
                    project.Files.ToString(CultureInfo.InvariantCulture),
                    project.PortableFiles.ToString(CultureInfo.InvariantCulture),
                    project.Portability.ToString("P0", CultureInfo.InvariantCulture));
            }

            output.Write(ledger);
        }

        var grouping = _options?.GroupBy ?? AuditGrouping.Rule;
        var limit = _options?.AllLocations == true ? int.MaxValue : DefaultLocations;
        foreach (var group in AuditMarkdownWriter.Group(result.Findings, grouping))
        {
            output.MarkupLine($"[bold]{Markup.Escape(group.Key)}[/] [dim]({group.Count().ToString(CultureInfo.InvariantCulture)})[/]");
            foreach (var finding in group.Take(limit))
            {
                output.MarkupLine($"  [{Color(finding.Severity)}]{finding.Rule}[/] {Markup.Escape($"{finding.File}:{finding.Line}")} [dim]{Markup.Escape(finding.Message)}[/]");
            }

            if (group.Count() > limit)
            {
                output.MarkupLine(Invariant($"  [dim]… {group.Count() - limit} more (--all-locations)[/]"));
            }
        }

        foreach (var skipped in result.Skipped)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Not audited:[/] {Markup.Escape(skipped)}");
        }

        // Findings are already listed; other diagnostics (OFR3010, OFR3011, loading) are not.
        output.Diagnostics([.. context.Diagnostics.ToSortedList().Where(d => !result.Rules.Contains(d.Code))]);
    }

    private static string Color(Severity severity) => severity switch
    {
        Severity.Error => Theme.BlockingStyle,
        Severity.Warning => Theme.DecisionStyle,
        _ => Theme.DimStyle,
    };

    /// <summary>"3 findings (1 error, 2 warnings, 0 info)".</summary>
    private static string Counts(int findings, int errors, int warnings, int info) =>
        $"{Plural(findings, "finding")} ({Plural(errors, "error")}, {Plural(warnings, "warning")}, {info.ToString(CultureInfo.InvariantCulture)} info)";

    private static string Plural(int count, string noun) => count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? "" : "s");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
