using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Seams;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Paths;
using Offramp.Reporting.Seams;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record SeamsOptions(string Project, string? UnportableFrom, IReadOnlyList<string> Symbols, int? MaxCut, string Format);

/// <summary><c>offramp seams</c> (docs/spec/commands/seams.md).</summary>
public sealed class SeamsCommand(string format) : ICommandHandler<SeamsOptions, SeamsResult>, IRawOutput<SeamsResult>
{
    /// <summary>The audit rules whose findings make code unportable (OFR3002 too when the target is not -windows).</summary>
    private static readonly HashSet<string> UnportableRules = new(StringComparer.Ordinal) { "OFR3001", "OFR3002", "OFR3004", "OFR3005", "OFR3006", "OFR3007", "OFR3008", "OFR3009" };

    private string? _written;

    public string CommandPath => "seams";

    public JsonTypeInfo<SeamsResult> ResultType => AnalysisJsonContext.Default.SeamsResult;

    /// <summary>DOT, HTML, and JSON documents are the primary output: --out receives them, not the envelope.</summary>
    public bool WritesOwnOutput => format != "table";

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The project to find seams in (path or name).", HelpName = "PROJECT", Required = true };
        var from = new Option<string?>("--unportable-from") { Description = "audit (run audit api on the project; default from seams.unportableSources) or list (only --symbols and seams.unportableSymbols).", HelpName = "SOURCE" };
        from.AcceptOnlyFromAmong("audit", "list");
        var symbols = new Option<string[]>("--symbols") { Description = "Namespaces or types that cannot port (comma-separated or repeated), added to seams.unportableSymbols.", HelpName = "NS.Type", AllowMultipleArgumentsPerToken = true };
        var maxCut = new Option<int?>("--max-cut") { Description = "Report no seam when the smallest boundary crosses more references than this.", HelpName = "N" };
        var format = new Option<string?>("--format") { Description = "table, json, dot, or html. Inferred from --out's extension when omitted.", HelpName = "FORMAT" };
        format.AcceptOnlyFromAmong("table", "json", "dot", "html");
        var command = new Command("seams", "Find the smallest boundary around code that cannot port: tainted types, the minimum cut, and the interfaces to extract.")
        {
            project, from, symbols, maxCut, format,
        };
        command.SetAction((parse, ct) =>
        {
            var settings = globals.Bind(parse);
            var chosen = parse.GetValue(format) ?? (settings.Out is { } o ? Path.GetExtension(o).ToLowerInvariant() switch
            {
                ".json" => "json",
                ".dot" or ".gv" => "dot",
                ".html" or ".htm" => "html",
                _ => "table",
            } : "table");
            var list = (parse.GetValue(symbols) ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            return CommandRunner.RunAsync(new SeamsCommand(chosen), new SeamsOptions(parse.GetValue(project)!, parse.GetValue(from), list, parse.GetValue(maxCut), chosen), settings, host, ct);
        });
        HelpExamples.Add(command,
            "offramp seams --project src/Accounts/Accounts.csproj",
            "offramp seams --project Accounts --unportable-from list --symbols System.DirectoryServices",
            "offramp seams --project Accounts --out seams.html");
        return command;
    }

    public async Task<CommandOutcome<SeamsResult>> ExecuteAsync(SeamsOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<SeamsResult>.Environment();
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return CommandOutcome<SeamsResult>.Usage();
        }

        var project = model.Projects.Single(p => p.Id == id);
        using var loader = new CompilationLoader(root);
        if (loader.LoadForProject(project) is not { } compilation)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(id));
            return CommandOutcome<SeamsResult>.Environment();
        }

        var source = options.UnportableFrom ?? (config.Seams.UnportableSources.Contains("audit", StringComparer.Ordinal) ? "audit" : "list");
        var uses = new List<UnportableUse>();
        if (source == "audit")
        {
            var audit = await AuditRunner.RunAsync(new AuditRequest
            {
                RepositoryRoot = root,
                Model = model,
                Audit = AuditKind.Api,
                TargetMajor = config.Target,
                Projects = [id],
                Overrides = config.Rules.ToSeverityOverrides(),
                Diagnostics = new DiagnosticBag(),
                Progress = context.Progress,
                References = new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)),
            }, cancellationToken);
            uses.AddRange(audit.Findings
                .Where(f => UnportableRules.Contains(f.Rule) && (f.Rule == "OFR3002" || f.Severity == Severity.Error))
                .Select(f => new UnportableUse(f.File, f.Line, f.Symbol)));
        }

        var result = SeamsAnalyzer.Analyze(new SeamsRequest
        {
            RepositoryRoot = root,
            Project = project,
            UnportableFrom = source,
            Symbols = [.. config.Seams.UnportableSymbols.Concat(options.Symbols).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Uses = uses,
            MaxCut = options.MaxCut,
            Diagnostics = context.Diagnostics,
        }, compilation)!;
        if (LlmGate.For(context, LlmGate.Naming) is { } llm)
        {
            result = await LlmNaming.NameSeamsAsync(llm, result, cancellationToken);
        }

        if (context.Settings.Out is not null && format != "table")
        {
            var path = Path.GetFullPath(context.Settings.Out, context.Host.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Document(result) ?? "", new System.Text.UTF8Encoding(false));
            _written = RepoPaths.ToRepositoryRelative(root, path);
        }

        return CommandOutcome<SeamsResult>.Completed(result);
    }

    public string? RawOutput(SeamsResult result, CommandContext context) => context.Settings.Out is null ? Document(result) : null;

    private string? Document(SeamsResult result) => format switch
    {
        "json" => OfframpJson.Serialize(result, AnalysisJsonContext.Default.SeamsResult),
        "dot" => SeamsRenderer.Dot(result),
        "html" => SeamsRenderer.Html(result),
        _ => null,
    };

    public void Render(SeamsResult result, CommandContext context, HumanOutput output)
    {
        var loc = result.Extraction?.EstimatedLoc ?? 0;
        if (_written is not null)
        {
            output.Headline($"Wrote {_written}: {result.Seams.Count} seam{(result.Seams.Count == 1 ? "" : "s")}.", Theme.ReadyStyle);
            return;
        }

        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Project}: {result.Tainted.Count} type{(result.Tainted.Count == 1 ? "" : "s")} cannot port ({loc} lines); {result.Seams.Count} seam{(result.Seams.Count == 1 ? "" : "s")}."),
            result.Seams.Count > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var tainted in result.Tainted)
        {
            output.MarkupLine($"  [{Theme.BlockingStyle}]✗[/] {Markup.Escape(tainted.Type)} [dim]({Markup.Escape(string.Join("; ", tainted.Reason))})[/]");
        }

        foreach (var seam in result.Seams)
        {
            output.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                $"[bold]{Markup.Escape(seam.Id)}[/] {Markup.Escape(seam.ProposedInterface)} on {Markup.Escape(seam.BoundaryType)} [dim](score {seam.Score:0.00}{(seam.ArticulationPoint ? ", articulation point" : "")}; callers {Markup.Escape(string.Join(", ", seam.Callers))})[/]"));
            foreach (var member in seam.Members)
            {
                var flags = (member.WireFriendly ? "" : " [yellow]not wire-friendly[/]") + (member.Static ? " [yellow]static[/]" : "");
                output.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"    {Markup.Escape(member.Signature)} [dim]×{member.CallSites}[/]{flags}"));
            }
        }

        if (result.Extraction is { } extraction)
        {
            output.MarkupLine($"[dim]Extract to[/] {Markup.Escape(extraction.MoveToProject)}[dim]:[/] {Markup.Escape(string.Join(", ", extraction.Types))}");
        }
    }
}
