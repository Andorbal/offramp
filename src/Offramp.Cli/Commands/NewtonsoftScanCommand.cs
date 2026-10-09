using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.Newtonsoft;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record NewtonsoftScanOptions(IReadOnlyList<string> Projects, string? Version, string Format);

/// <summary><c>offramp newtonsoft scan</c> (docs/spec/commands/newtonsoft.md#newtonsoft-scan).</summary>
public sealed class NewtonsoftScanCommand(string format) : ICommandHandler<NewtonsoftScanOptions, NewtonsoftScanResult>, IRawOutput<NewtonsoftScanResult>
{
    public string CommandPath => "newtonsoft scan";

    public JsonTypeInfo<NewtonsoftScanResult> ResultType => AnalysisJsonContext.Default.NewtonsoftScanResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string[]>("--project") { Description = "Only these projects (path or name). Repeatable.", HelpName = "PROJECT" };
        var version = new Option<string?>("--version") { Description = "Judge against this release instead of the newest in newtonsoft.target.", HelpName = "VERSION" };
        var format = new Option<string>("--format") { Description = "table (the terminal view), json (the result alone), or markdown.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "json", "markdown");
        var command = new Command("scan", "Where the code serializes, deserializes, and configures Newtonsoft.Json, and what changes between the release in use and the target.")
        {
            project, version, format,
        };
        command.SetAction((parse, ct) =>
        {
            var chosen = parse.GetValue(format) ?? "table";
            var options = new NewtonsoftScanOptions(parse.GetValue(project) ?? [], parse.GetValue(version), chosen);
            return CommandRunner.RunAsync(new NewtonsoftScanCommand(chosen), options, globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp newtonsoft scan",
            "offramp newtonsoft scan --project src/Orders/Orders.csproj --version 13.0.3",
            "offramp newtonsoft scan --format markdown > newtonsoft.md");
        return command;
    }

    public async Task<CommandOutcome<NewtonsoftScanResult>> ExecuteAsync(NewtonsoftScanOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<NewtonsoftScanResult>.Environment();
        }

        var projects = new List<string>();
        foreach (var value in options.Projects)
        {
            if (ProjectLookup.Resolve(value, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                return CommandOutcome<NewtonsoftScanResult>.Usage();
            }

            projects.Add(id);
        }

        if (await NewtonsoftCommands.TargetAsync(context, model, options.Version, cancellationToken) is not { Version: { } target } release)
        {
            return CommandOutcome<NewtonsoftScanResult>.Usage();
        }

        var result = NewtonsoftScanner.Scan(new NewtonsoftScanRequest
        {
            RepositoryRoot = root,
            Model = model,
            Target = target,
            TargetPackage = release.Package,
            Projects = projects,
            Overrides = config.SeverityOverrides(),
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        }, cancellationToken);
        return CommandOutcome<NewtonsoftScanResult>.Completed(result);
    }

    public string? RawOutput(NewtonsoftScanResult result, CommandContext context) => format switch
    {
        "json" => OfframpJson.Serialize(result, AnalysisJsonContext.Default.NewtonsoftScanResult),
        "markdown" => Markdown(result),
        _ => null,
    };

    public void Render(NewtonsoftScanResult result, CommandContext context, HumanOutput output)
    {
        var errors = result.Summary.Findings["error"];
        var warnings = result.Summary.Findings["warning"];
        output.Headline(Invariant($"Newtonsoft.Json in {result.Summary.Projects} projects, {result.Summary.Sites} sites; {result.Findings.Count} changes to {result.Target} ({errors} errors, {warnings} warnings)."),
            errors > 0 ? Theme.BlockingStyle : warnings > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);

        var projects = new Table().Border(TableBorder.Simple);
        projects.AddColumn("Project");
        projects.AddColumn("Release");
        projects.AddColumn("Referenced as");
        projects.AddColumn(new TableColumn("Sites").RightAligned());
        foreach (var project in result.Projects)
        {
            projects.AddRow(
                Markup.Escape(project.Project),
                Markup.Escape(project.Newtonsoft.Version),
                Markup.Escape(Wire(project.Newtonsoft.Via)),
                project.Sites.Count.ToString(CultureInfo.InvariantCulture));
        }

        output.Write(projects);
        if (result.Findings.Count == 0)
        {
            return;
        }

        var findings = new Table().Border(TableBorder.Simple);
        findings.AddColumn(new TableColumn("Rule").NoWrap());
        findings.AddColumn("Where");
        findings.AddColumn("What");
        foreach (var finding in result.Findings)
        {
            findings.AddRow(
                new Markup($"[{Color(finding.Severity)}]{finding.Rule}[/]"),
                new Markup(Markup.Escape(Invariant($"{finding.File}:{finding.Line}"))),
                new Markup(Markup.Escape(finding.Message)));
        }

        output.Write(findings);
    }

    private static string Markdown(NewtonsoftScanResult result)
    {
        var b = new StringBuilder();
        b.Append(Invariant($"# Newtonsoft.Json: {result.Summary.Projects} projects, {result.Summary.Sites} sites, target {result.Target}\n\n"));
        b.Append("| Project | Release | Referenced as | Sites |\n|---|---|---|---:|\n");
        foreach (var project in result.Projects)
        {
            b.Append(Invariant($"| {project.Project} | {project.Newtonsoft.Version} | {Wire(project.Newtonsoft.Via)} | {project.Sites.Count} |\n"));
        }

        if (result.Findings.Count > 0)
        {
            b.Append("\n| Rule | Severity | Where | What |\n|---|---|---|---|\n");
            foreach (var finding in result.Findings)
            {
                b.Append(Invariant($"| {finding.Rule} | {finding.Severity.ToWire()} | {finding.File}:{finding.Line} | {finding.Message.Replace("|", "\\|", StringComparison.Ordinal)} |\n"));
            }
        }

        return b.ToString();
    }

    private static string Wire(NewtonsoftReferenceKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string Color(Severity severity) => severity switch
    {
        Severity.Error => "red",
        Severity.Warning => "yellow",
        _ => "blue",
    };

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
