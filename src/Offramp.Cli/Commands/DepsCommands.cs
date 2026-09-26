using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.NuGet;
using Offramp.NuGet.Audit;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Gac;
using Offramp.Analysis.Rules;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary>The <c>deps</c> command group (docs/spec/commands/deps.md).</summary>
public static class DepsCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var deps = new Command("deps", "Package and assembly dependencies: what supports the target, and what to do about the rest.");
        deps.Subcommands.Add(DepsAuditCommand.Create(host, globals));
        deps.Subcommands.Add(DepsConsolidateCommand.Create(host, globals));
        deps.Subcommands.Add(DepsGacCommand.Create(host, globals));
        deps.Subcommands.Add(DepsResolveDllsCommand.Create(host, globals));
        return deps;
    }

    /// <summary>Loads the model and resolves an optional <c>--project</c>; null (with a diagnostic) when either fails.</summary>
    internal static (WorkspaceModel? Model, string? Project, bool UsageError) Load(CommandContext context, string? project)
    {
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, context.Repository.Path, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null || project is null)
        {
            return (model, null, false);
        }

        var resolved = ProjectLookup.Resolve(project, model, context);
        if (resolved is null)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", project)]);
            return (model, null, true);
        }

        return (model, resolved, false);
    }
}

public sealed record DepsAuditOptions(string? Package, string? Project, bool IncludePrerelease, string Format);

/// <summary><c>offramp deps audit</c>.</summary>
public sealed class DepsAuditCommand(string format = "table") : ICommandHandler<DepsAuditOptions, DepsAuditResult>, IRawOutput<DepsAuditResult>
{
    public string CommandPath => "deps audit";

    public JsonTypeInfo<DepsAuditResult> ResultType => NuGetJsonContext.Default.DepsAuditResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var package = new Option<string?>("--package") { Description = "Audit only this package.", HelpName = "ID" };
        var project = new Option<string?>("--project") { Description = "Audit only what this project uses (path or name).", HelpName = "PROJECT" };
        var prerelease = new Option<bool>("--include-prerelease") { Description = "Consider prerelease versions (deps.includePrerelease)." };
        var format = new Option<string>("--format") { Description = "table (the terminal view), json (the result alone), or markdown.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "json", "markdown");
        var command = new Command("audit", "For every package in use: which versions support the target, the lowest and newest that do, and what blocks the rest.")
        {
            package, project, prerelease, format,
        };
        command.SetAction((parse, ct) =>
        {
            var chosen = parse.GetValue(format) ?? "table";
            return CommandRunner.RunAsync(
                new DepsAuditCommand(chosen),
                new DepsAuditOptions(parse.GetValue(package), parse.GetValue(project), parse.GetValue(prerelease), chosen),
                globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp deps audit",
            "offramp deps audit --target 8 --project src/Billing/Billing.csproj",
            "offramp deps audit --format markdown > docs/packages.md",
            "offramp deps audit --json | jq '.result.packages[] | select(.status == \"blocked\")'");
        return command;
    }

    public async Task<CommandOutcome<DepsAuditResult>> ExecuteAsync(DepsAuditOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var (model, project, usageError) = DepsCommands.Load(context, options.Project);
        if (usageError)
        {
            return CommandOutcome<DepsAuditResult>.Usage();
        }

        if (model is null)
        {
            return CommandOutcome<DepsAuditResult>.Environment();
        }

        var config = context.Config.Config;
        if (options.IncludePrerelease)
        {
            config = config with { Deps = config.Deps with { IncludePrerelease = true } };
        }

        using var feeds = NuGetPackageFeeds.ForRepository(context.Repository.Path, config.Deps.Feeds);
        var result = await DepsAuditor.RunAsync(new DepsAuditRequest
        {
            Model = model,
            Config = config,
            Feeds = feeds,
            Cache = CommandRunner.Cache(context),
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Package = options.Package,
            Project = project,
        }, cancellationToken);
        return result.Partial ? new CommandOutcome<DepsAuditResult>(result, OutcomeKind.Partial) : CommandOutcome<DepsAuditResult>.Completed(result);
    }

    /// <summary>--format markdown and --format json print the document alone (the envelope needs --json).</summary>
    public string? RawOutput(DepsAuditResult result, CommandContext context) => format switch
    {
        "markdown" => AuditMarkdown.Write(result),
        "json" => OfframpJson.Serialize(result, NuGetJsonContext.Default.DepsAuditResult),
        _ => null,
    };

    public void Render(DepsAuditResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        var style = s.Blocked > 0 ? Theme.BlockingStyle : s.Replace + s.Upgrade + s.Unknown > 0 ? Theme.DecisionStyle : Theme.ReadyStyle;
        output.Headline($"{result.Packages.Count} packages for {result.Target}: {s.Ok} ok, {s.Upgrade} upgrade, {s.Replace} replace, {s.Blocked} blocked" + (s.Unknown > 0 ? $", {s.Unknown} unknown." : "."), style);
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("Package").NoWrap());
        table.AddColumn("In use");
        table.AddColumn(new TableColumn("Lowest").NoWrap());
        table.AddColumn(new TableColumn("Newest").NoWrap());
        table.AddColumn(new TableColumn("Status").NoWrap());
        foreach (var package in AuditMarkdown.Ordered(result.Packages))
        {
            var inUse = string.Join(", ", package.InUse.Select(v =>
                v.Version + (package.SupportsTarget.InUseVersions.GetValueOrDefault(v.Version) == false ? " ✗" : "") + (v.Pinned ? " (pinned)" : "")));
            var (color, word) = package.Status switch
            {
                PackageStatus.Blocked => (Theme.BlockingStyle, "blocked"),
                PackageStatus.Replace => (Theme.BlockingStyle, "replace"),
                PackageStatus.Upgrade => (Theme.DecisionStyle, "upgrade"),
                PackageStatus.Unknown => (Theme.DecisionStyle, "unknown"),
                _ => (Theme.ReadyStyle, "ok"),
            };
            var flags = (package.WindowsOnly ? " [yellow](Windows only)[/]" : "") + (package.Deprecated is not null ? " [yellow](deprecated)[/]" : "");
            table.AddRow(
                new Markup(Markup.Escape(package.Id)),
                new Markup(Markup.Escape(inUse)),
                new Markup(Markup.Escape(package.LowestSupporting ?? "none")),
                new Markup(Markup.Escape(package.NewestSupporting ?? "none")),
                new Markup($"[{color}]{word}[/]{flags}"));
        }

        output.Write(table);
        foreach (var package in AuditMarkdown.Ordered(result.Packages).Where(p => p.Replacement is not null && p.Status is PackageStatus.Replace or PackageStatus.Blocked))
        {
            output.MarkupLine($"  {Markup.Escape(package.Id)} → {Markup.Escape(package.Replacement!.Replacement)}");
        }

        var framework = result.AssemblyReferences.Count(r => r.Kind == AssemblyReferenceKind.Framework);
        if (framework > 0)
        {
            var none = result.AssemblyReferences.Count(r => r.Mapping?.Kind == FrameworkAssemblyKind.None);
            output.MarkupLine($"[dim]{framework.ToString(CultureInfo.InvariantCulture)} framework assembly references, {none.ToString(CultureInfo.InvariantCulture)} with no modern equivalent: see[/] offramp deps gac");
        }
    }
}

public sealed record DepsGacOptions(string? Project);

/// <summary><c>offramp deps gac</c>.</summary>
public sealed class DepsGacCommand : ICommandHandler<DepsGacOptions, GacResult>
{
    public string CommandPath => "deps gac";

    public JsonTypeInfo<GacResult> ResultType => NuGetJsonContext.Default.GacResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string?>("--project") { Description = "Only this project (path or name).", HelpName = "PROJECT" };
        var command = new Command("gac", ".NET Framework assembly references and their modern equivalents, with how much each is used.") { project };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new DepsGacCommand(), new DepsGacOptions(parse.GetValue(project)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp deps gac",
            "offramp deps gac --project src/Billing/Billing.csproj --json");
        return command;
    }

    public Task<CommandOutcome<GacResult>> ExecuteAsync(DepsGacOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var (model, project, usageError) = DepsCommands.Load(context, options.Project);
        if (usageError)
        {
            return Task.FromResult(CommandOutcome<GacResult>.Usage());
        }

        if (model is null)
        {
            return Task.FromResult(CommandOutcome<GacResult>.Environment());
        }

        var result = GacAnalyzer.Run(model, context.Config.Config, context.Repository.Path, project, context.Progress, context.Diagnostics, cancellationToken);
        return Task.FromResult(CommandOutcome<GacResult>.Completed(result));
    }

    public void Render(GacResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        output.Headline(
            $"{result.Projects.Sum(p => p.References.Count)} framework references in {result.Projects.Count} projects: {s.Builtin} built in, {s.Package} package, {s.CompatPack} compatibility pack, {s.None} none, {s.Unknown} unknown; {s.Unused} unused.",
            s.None > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            output.Line();
            output.MarkupLine($"[bold]{Markup.Escape(project.Project)}[/] [dim]({Markup.Escape(string.Join(", ", project.TargetFrameworks))})[/]");
            var table = new Table().Border(TableBorder.None).HideHeaders();
            table.AddColumn("Reference");
            table.AddColumn("Equivalent");
            table.AddColumn(new TableColumn("Uses").RightAligned());
            foreach (var reference in project.References)
            {
                var mapping = reference.Mapping;
                var equivalent = mapping.Kind switch
                {
                    FrameworkAssemblyKind.Builtin => "[green]built in[/]",
                    FrameworkAssemblyKind.Package => $"[green]package[/] {Markup.Escape(mapping.Package ?? "")}",
                    FrameworkAssemblyKind.CompatPack => "[yellow]Microsoft.Windows.Compatibility[/]",
                    FrameworkAssemblyKind.None => "[red]none[/]",
                    _ => "[yellow]unknown[/]",
                } + (mapping.WindowsOnly ? " [yellow](Windows only)[/]" : "");
                var uses = reference.Usages switch
                {
                    null => "[dim]?[/]",
                    0 => "[dim]unused[/]",
                    var n => n.Value.ToString(CultureInfo.InvariantCulture),
                };
                table.AddRow(new Markup("  " + Markup.Escape(reference.Name)), new Markup(equivalent), new Markup(uses));
            }

            output.Write(table);
        }
    }
}
