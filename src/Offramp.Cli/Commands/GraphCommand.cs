using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Reporting;
using Offramp.Reporting.Graph;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record GraphOptions(GraphFormat? Format, GraphViewOptions View, string? Focus);

/// <summary><c>offramp graph</c>: the project graph as data, DOT, Mermaid, or an interactive page (docs/spec/commands/graph.md).</summary>
public sealed class GraphCommand : ICommandHandler<GraphOptions, GraphResult>, IRawOutput<GraphResult>
{
    private static readonly string[] Kinds = ["test", "web", "winforms", "wpf", "service", "console", "library", "unknown"];

    public string CommandPath => "graph";

    public JsonTypeInfo<GraphResult> ResultType => ReportingJsonContext.Default.GraphResult;

    /// <summary>The rendering is the primary output; --out receives it, not the envelope.</summary>
    public bool WritesOwnOutput => true;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var format = new Option<string?>("--format") { Description = "json (the graph data), dot, mermaid, or html. Inferred from --out's extension when omitted.", HelpName = "FORMAT" };
        format.AcceptOnlyFromAmong("json", "dot", "mermaid", "html");
        var exclude = KindsOption("--exclude-kind", "Leave out projects of these kinds, for example test.");
        var include = KindsOption("--include-kind", "Show only projects of these kinds.");
        var focus = new Option<string?>("--focus") { Description = "Show the subgraph around this project (path or name).", HelpName = "PROJECT" };
        var depth = new Option<int?>("--depth") { Description = "Levels around --focus (default: all).", HelpName = "N" };
        depth.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 0)
            {
                r.AddError("--depth must be zero or more.");
            }
        });
        var direction = new Option<string>("--direction") { Description = "Around --focus: both, dependencies, or dependents.", DefaultValueFactory = _ => "both" };
        direction.AcceptOnlyFromAmong("both", "dependencies", "dependents");
        var cluster = new Option<string>("--cluster") { Description = "Group projects: none, directory, or kind.", DefaultValueFactory = _ => "none" };
        cluster.AcceptOnlyFromAmong("none", "directory", "kind");
        var highlight = new Option<string>("--highlight") { Description = "Mark cycles, the frontier (ready to port today), the top blockers, or none.", DefaultValueFactory = _ => "cycles" };
        highlight.AcceptOnlyFromAmong("cycles", "frontier", "blockers", "none");
        var edges = new Option<string>("--edges") { Description = "all, or project to hide HintPath references to other projects' outputs.", DefaultValueFactory = _ => "all" };
        edges.AcceptOnlyFromAmong("all", "project");

        var command = new Command("graph", "Show the project dependency graph with framework class and kind, as data, DOT, Mermaid, or a self-contained interactive HTML page.")
        {
            format, exclude, include, focus, depth, direction, cluster, highlight, edges,
        };
        command.Validators.Add(r =>
        {
            // GetResult, not GetValue: an option whose own validation failed must not throw here.
            if (r.GetResult(depth) is not null && r.GetResult(focus) is null)
            {
                r.AddError("--depth needs --focus.");
            }

            if (r.GetResult(format) is null && r.GetValue(globals.Out) is { } o && GraphRenderer.FromExtension(o) is null)
            {
                r.AddError($"Cannot tell the format of '{o}' from its extension; pass --format json|dot|mermaid|html.");
            }
        });
        command.SetAction((parse, ct) =>
        {
            var settings = globals.Bind(parse);
            var effective = parse.GetValue(format) is { } f ? Parse<GraphFormat>(f)
                : settings.Out is { } o ? GraphRenderer.FromExtension(o) : null;
            var view = new GraphViewOptions
            {
                ExcludeKinds = ParseKinds(parse.GetValue(exclude)),
                IncludeKinds = ParseKinds(parse.GetValue(include)),
                Depth = parse.GetValue(depth),
                Direction = Parse<GraphDirection>(parse.GetValue(direction)!),
                Cluster = Parse<GraphClusterMode>(parse.GetValue(cluster)!),
                Highlight = Parse<GraphHighlightMode>(parse.GetValue(highlight)!),
                Edges = Parse<GraphEdgeFilter>(parse.GetValue(edges)!),
            };
            return CommandRunner.RunAsync(new GraphCommand(), new GraphOptions(effective, view, parse.GetValue(focus)), settings, host, ct);
        });
        HelpExamples.Add(command,
            "offramp graph --format html --out graph.html --exclude-kind test",
            "offramp graph --format dot | dot -Tsvg > graph.svg",
            "offramp graph --focus Billing.Core --depth 2 --format mermaid",
            "offramp graph --highlight blockers --cluster directory --out graph.html");
        return command;
    }

    public Task<CommandOutcome<GraphResult>> ExecuteAsync(GraphOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return Task.FromResult(CommandOutcome<GraphResult>.Environment());
        }

        var view = options.View;
        if (options.Focus is not null)
        {
            var focus = ProjectLookup.Resolve(options.Focus, model, context);
            if (focus is null)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Focus}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", options.Focus)]);
                return Task.FromResult(CommandOutcome<GraphResult>.Usage());
            }

            view = view with { Focus = focus };
        }

        var graph = GraphView.Build(model, view);
        if (options.Format == GraphFormat.Mermaid && graph.Nodes.Count > MermaidWriter.MaxNodes)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0201,
                string.Create(CultureInfo.InvariantCulture, $"The Mermaid graph has {graph.Nodes.Count} projects; narrow it with --focus or --exclude-kind, or use --format html."));
        }

        string? content = options.Format is { } format ? GraphRenderer.Render(graph, format, Title(model)) : null;
        string? output = null;
        if (context.Settings.Out is not null && content is not null)
        {
            var path = Path.GetFullPath(context.Settings.Out, context.Host.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            output = RepoPaths.ToRepositoryRelative(root, path);
            content = null;
        }

        return Task.FromResult(CommandOutcome<GraphResult>.Completed(new GraphResult
        {
            Format = options.Format,
            Output = output,
            Graph = graph,
            Content = content,
        }));
    }

    public string? RawOutput(GraphResult result, CommandContext context) => result.Content;

    public void Render(GraphResult result, CommandContext context, HumanOutput output)
    {
        var graph = result.Graph;
        var counts = $"{graph.Nodes.Count} projects, {graph.Edges.Count} references, {graph.Cycles.Count} cycle{(graph.Cycles.Count == 1 ? "" : "s")}";
        if (result.Output is not null)
        {
            output.Headline($"Wrote {result.Output}: {counts}.", Theme.ReadyStyle);
            return;
        }

        output.Headline($"Graph: {counts}.", graph.Cycles.Count > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Class");
        table.AddColumn(new TableColumn("Projects").RightAligned());
        table.AddColumn(new TableColumn("Lines").RightAligned());
        table.AddColumn(new TableColumn("Ready").RightAligned());
        table.AddColumn(new TableColumn("Blocked").RightAligned());
        foreach (var (name, frameworkClass, color) in new[]
        {
            ("framework", FrameworkClass.Framework, Theme.FrameworkOrange), ("standard", FrameworkClass.Standard, Theme.StandardBlue),
            ("modern", FrameworkClass.Modern, Theme.ModernGreen), ("dual", FrameworkClass.Dual, Theme.DualTeal),
        })
        {
            var members = graph.Nodes.Where(n => n.FrameworkClass == frameworkClass).ToList();
            table.AddRow(
                new Markup($"[{color.ToMarkup()}]■[/] {name}"),
                new Markup(members.Count.ToString(CultureInfo.InvariantCulture)),
                new Markup(members.Sum(n => n.Loc).ToString("N0", CultureInfo.InvariantCulture)),
                new Markup(members.Count(n => n.Readiness == ProjectReadiness.Ready).ToString(CultureInfo.InvariantCulture)),
                new Markup(members.Count(n => n.Readiness == ProjectReadiness.Blocked).ToString(CultureInfo.InvariantCulture)));
        }

        output.Write(table);
        var blockers = graph.Nodes
            .Where(n => n.FrameworkClass == FrameworkClass.Framework && n.Dependents > 0)
            .OrderByDescending(n => n.Dependents).ThenBy(n => n.Id, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        if (blockers.Count > 0)
        {
            output.MarkupLine("[dim]Framework-only projects with the most dependents:[/]");
            foreach (var node in blockers)
            {
                output.MarkupLine($"  {Markup.Escape(node.Id)} [dim]({node.Dependents} dependents)[/]");
            }
        }

        foreach (var cycle in graph.Cycles)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Cycle:[/] {Markup.Escape(string.Join(" ↔ ", cycle))}");
        }

        output.MarkupLine("[dim]Interactive view:[/] offramp graph --format html --out graph.html");
    }

    private static string Title(WorkspaceModel model) =>
        model.Solution is null ? "Project graph" : Path.GetFileNameWithoutExtension(model.Solution) + " project graph";

    private static Option<string[]> KindsOption(string name, string description)
    {
        var option = new Option<string[]>(name) { Description = description + " Comma-separated or repeated.", HelpName = "KIND[,KIND...]", AllowMultipleArgumentsPerToken = true };
        option.Validators.Add(r =>
        {
            foreach (var kind in Split(r.GetValueOrDefault<string[]>()))
            {
                if (!Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    r.AddError($"'{kind}' is not a project kind; use one of {string.Join(", ", Kinds)}.");
                }
            }
        });
        return option;
    }

    private static IReadOnlyList<ProjectKind> ParseKinds(string[]? values) =>
        [.. Split(values).Select(Parse<ProjectKind>).Distinct()];

    private static IEnumerable<string> Split(string[]? values) =>
        (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static T Parse<T>(string value)
        where T : struct, Enum => Enum.Parse<T>(value, ignoreCase: true);
}
