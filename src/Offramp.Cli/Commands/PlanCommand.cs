using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Workspace;
using Offramp.Workspace.Model;
using Offramp.Workspace.Planning;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record PlanOptions(string? For, bool Frontier, bool Waves, IReadOnlyList<ProjectKind> ExcludeKinds);

/// <summary><c>offramp plan</c>: a leaf-first migration order in waves (docs/spec/commands/workspace.md#plan).</summary>
public sealed class PlanCommand(bool waves = false) : ICommandHandler<PlanOptions, PlanResult>
{
    public string CommandPath => "plan";

    public JsonTypeInfo<PlanResult> ResultType => WorkspaceJsonContext.Default.PlanResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var frontier = new Option<bool>("--frontier") { Description = "Only projects that can be ported today: every dependency is already portable." };
        var forOption = new Option<string?>("--for") { Description = "Only what must be ported for this project to run on the target (path or name).", HelpName = "PROJECT" };
        var wavesOption = new Option<bool>("--waves") { Description = "Group the order into waves; each wave depends only on earlier ones." };
        var exclude = GraphCommand.KindsOption("--exclude-kind", "Leave out projects of these kinds, for example test.");
        var command = new Command("plan", "Order the migration leaf-first: what can be ported today, what each project waits on, and how far each change reaches.")
        {
            frontier, forOption, wavesOption, exclude,
        };
        command.SetAction((parse, ct) =>
        {
            var grouped = parse.GetValue(wavesOption);
            return CommandRunner.RunAsync(
                new PlanCommand(grouped),
                new PlanOptions(parse.GetValue(forOption), parse.GetValue(frontier), grouped, GraphCommand.ParseKinds(parse.GetValue(exclude))),
                globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp plan --exclude-kind test",
            "offramp plan --frontier",
            "offramp plan --for Billing.Web --waves",
            "offramp plan --json > plan.json");
        return command;
    }

    public Task<CommandOutcome<PlanResult>> ExecuteAsync(PlanOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, context.Repository.Path, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return Task.FromResult(CommandOutcome<PlanResult>.Environment());
        }

        string? target = null;
        if (options.For is not null)
        {
            target = ProjectLookup.Resolve(options.For, model, context);
            if (target is null)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.For}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", options.For)]);
                return Task.FromResult(CommandOutcome<PlanResult>.Usage());
            }
        }

        return Task.FromResult(CommandOutcome<PlanResult>.Completed(MigrationPlanner.Plan(model, target, options.Frontier, options.ExcludeKinds)));
    }

    public void Render(PlanResult result, CommandContext context, HumanOutput output)
    {
        output.Headline(Headline(result), result.Counts.Ready > 0 || result.Counts.Projects == result.Counts.Done ? Theme.ReadyStyle : Theme.DecisionStyle);
        if (result.Order.Count == 0)
        {
            return;
        }

        if (waves)
        {
            foreach (var wave in result.Order.GroupBy(e => e.Wave))
            {
                output.MarkupLine(wave.Key == 0 ? "[dim]Already portable[/]" : $"[bold]Wave {wave.Key}[/]");
                output.Write(Table(wave));
            }
        }
        else
        {
            output.Write(Table(result.Order));
        }

        foreach (var cycle in result.Cycles)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Cycle to break first:[/] {Markup.Escape(string.Join(" ↔ ", cycle))}");
        }
    }

    private static string Headline(PlanResult result)
    {
        var c = result.Counts;
        var toPort = c.Ready + c.Blocked;
        if (result.For is not null && toPort == 0)
        {
            return $"{result.For} and everything it depends on are already portable.";
        }

        if (result.Frontier)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{c.Ready} project{(c.Ready == 1 ? "" : "s")} can be ported today.");
        }

        var scope = result.For is null ? "" : $" for {result.For}";
        return toPort == 0
            ? "Every project is already portable."
            : string.Create(CultureInfo.InvariantCulture,
                $"{toPort} project{(toPort == 1 ? "" : "s")} to port{scope} in {c.Waves} wave{(c.Waves == 1 ? "" : "s")}; {c.Ready} ready today{(result.Cycles.Count > 0 ? $", {result.Cycles.Count} cycle{(result.Cycles.Count == 1 ? "" : "s")} to break first" : "")}.");
    }

    private static Table Table(IEnumerable<PlanEntry> entries)
    {
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("Wave").RightAligned());
        table.AddColumn("Project");
        table.AddColumn("Class");
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Dependents").RightAligned());
        table.AddColumn("Waits on");
        foreach (var entry in entries)
        {
            table.AddRow(
                new Markup(entry.Wave.ToString(CultureInfo.InvariantCulture)),
                new Markup(Markup.Escape(entry.Project) + (entry.InCycle ? $" [{Theme.DecisionStyle}](cycle)[/]" : "")),
                new Markup($"[{ClassColor(entry.FrameworkClass).ToMarkup()}]■[/] {Wire(entry.FrameworkClass)}"),
                new Markup(Status(entry.Readiness)),
                new Markup(entry.BlastRadius.ToString(CultureInfo.InvariantCulture)),
                new Markup(Markup.Escape(Blockers(entry.Blockers))));
        }

        return table;
    }

    private static string Blockers(IReadOnlyList<string> blockers) => blockers.Count switch
    {
        0 => "",
        <= 2 => string.Join(", ", blockers.Select(b => Path.GetFileNameWithoutExtension(b))),
        _ => string.Join(", ", blockers.Take(2).Select(b => Path.GetFileNameWithoutExtension(b))) + string.Create(CultureInfo.InvariantCulture, $" +{blockers.Count - 2}"),
    };

    private static string Status(ProjectReadiness readiness) => readiness switch
    {
        ProjectReadiness.Done => "[dim]done[/]",
        ProjectReadiness.Ready => $"[{Theme.ReadyStyle}]ready[/]",
        _ => $"[{Theme.BlockingStyle}]blocked[/]",
    };

    private static Color ClassColor(FrameworkClass value) => value switch
    {
        FrameworkClass.Framework => Theme.FrameworkOrange,
        FrameworkClass.Standard => Theme.StandardBlue,
        FrameworkClass.Modern => Theme.ModernGreen,
        _ => Theme.DualTeal,
    };

    private static string Wire(FrameworkClass value) => value.ToString().ToLowerInvariant();
}
