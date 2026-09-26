using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.ApiCompat;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record AuditApiCompatOptions(string Project, string? Left, string? Right, string? Baseline);

/// <summary><c>offramp audit api-compat</c> (docs/spec/commands/audit.md#audit-api-compat-ofr3500-3599).</summary>
public sealed class AuditApiCompatCommand : ICommandHandler<AuditApiCompatOptions, ApiCompatResult>
{
    public string CommandPath => "audit api-compat";

    public JsonTypeInfo<ApiCompatResult> ResultType => AnalysisJsonContext.Default.ApiCompatResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The project to compare (path or name).", HelpName = "PROJECT", Required = true };
        var left = new Option<string?>("--left") { Description = "Left target framework (default: the first .NET Framework target).", HelpName = "TFM" };
        var right = new Option<string?>("--right") { Description = "Right target framework (default: the newest modern target).", HelpName = "TFM" };
        var baseline = new Option<string?>("--baseline") { Description = "Compare the working tree with the build at this git revision instead.", HelpName = "GIT_REF" };
        var command = new Command("api-compat", "Compare the public API of a project's two targets, or of the working tree and a git revision, with Microsoft's ApiCompat.")
        {
            project, left, right, baseline,
        };
        command.Validators.Add(r =>
        {
            if (r.GetResult(baseline) is not null && (r.GetResult(left) is not null || r.GetResult(right) is not null))
            {
                r.AddError("--baseline compares the working tree with a revision; it does not take --left or --right.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new AuditApiCompatCommand(),
            new AuditApiCompatOptions(parse.GetValue(project)!, parse.GetValue(left), parse.GetValue(right), parse.GetValue(baseline)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp audit api-compat --project src/Shared/Shared.csproj",
            "offramp audit api-compat --project Shared --left net48 --right net10.0",
            "offramp audit api-compat --project Billing.Core --baseline origin/main");
        return command;
    }

    public async Task<CommandOutcome<ApiCompatResult>> ExecuteAsync(AuditApiCompatOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<ApiCompatResult>.Environment();
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return CommandOutcome<ApiCompatResult>.Usage();
        }

        var project = model.Projects.Single(p => p.Id == id);
        foreach (var tfm in new[] { options.Left, options.Right }.OfType<string>())
        {
            if (!project.TargetFrameworks.Contains(tfm, StringComparer.OrdinalIgnoreCase))
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR3503, $"{id} does not target {tfm}; it targets {string.Join(";", project.TargetFrameworks)}.", new Core.Diagnostics.DiagnosticLocation(id));
                return CommandOutcome<ApiCompatResult>.Usage();
            }
        }

        var result = await ApiCompatRunner.RunAsync(new ApiCompatRequest
        {
            RepositoryRoot = root,
            Model = model,
            Project = project,
            Left = options.Left,
            Right = options.Right,
            Baseline = options.Baseline,
            Processes = context.Host.Processes,
            Git = context.Host.GitService,
            Diagnostics = context.Diagnostics,
        }, cancellationToken);
        if (result is null)
        {
            return context.Diagnostics.Contains("OFR3503") ? CommandOutcome<ApiCompatResult>.Usage() : CommandOutcome<ApiCompatResult>.Environment();
        }

        return CommandOutcome<ApiCompatResult>.Completed(result);
    }

    public void Render(ApiCompatResult result, CommandContext context, HumanOutput output)
    {
        var sides = $"{result.Left?.Label} and {result.Right?.Label}";
        if (result.Differences.Count == 0)
        {
            output.Headline($"{result.Project}: the public API of {sides} is the same.", Theme.ReadyStyle);
            return;
        }

        output.Headline(string.Create(CultureInfo.InvariantCulture, $"{result.Project}: {result.Differences.Count} public API difference{(result.Differences.Count == 1 ? "" : "s")} between {sides}."), Theme.DecisionStyle);
        foreach (var difference in result.Differences)
        {
            output.MarkupLine($"  [dim]{difference.Code}[/] {Markup.Escape(difference.Member)} [dim]only on {Markup.Escape(difference.OnlyOn ?? "?")}[/]");
        }
    }
}
