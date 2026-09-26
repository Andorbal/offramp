using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis.Compilations;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding;
using Offramp.Scaffolding.Config;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp config</c>: configuration file conversions (docs/spec/commands/scaffold.md#config-convert).</summary>
public static class ConfigCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var config = new Command("config", "Configuration conversions: App.config and Web.config to appsettings.json.");
        config.Subcommands.Add(ConfigConvertCommand.Create(host, globals));
        return config;
    }
}

public sealed record ConfigConvertOptions(string Project, string? Out, IReadOnlyList<string> Sections, bool Shim);

/// <summary><c>offramp config convert</c>.</summary>
public sealed class ConfigConvertCommand : ICommandHandler<ConfigConvertOptions, ConfigConvertResult>
{
    public string CommandPath => "config convert";

    public JsonTypeInfo<ConfigConvertResult> ResultType => ScaffoldingJsonContext.Default.ConfigConvertResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The project whose App.config or Web.config to convert (path or name).", HelpName = "PROJECT", Required = true };
        var output = new Option<string?>("--out") { Description = "Where to write appsettings.json and the classes (default: next to the configuration file).", HelpName = "DIR" };
        var sections = new Option<string[]>("--sections") { Description = "appSettings, connectionStrings, custom (every custom section), or section names; default all.", HelpName = "LIST", AllowMultipleArgumentsPerToken = true };
        var shim = new Option<bool>("--shim") { Description = "Also write ConfigurationManagerShim, ConfigurationManager's API over IConfiguration, for code that cannot take IConfiguration yet." };
        var command = new Command("convert", "Convert App.config or Web.config to appsettings.json, options classes, and appsettings.{Environment}.json from transforms.")
        {
            project, output, sections, shim,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new ConfigConvertCommand(),
            new ConfigConvertOptions(parse.GetValue(project)!, parse.GetValue(output),
                [.. (parse.GetValue(sections) ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))],
                parse.GetValue(shim)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp config convert --project src/Billing.Tool/Billing.Tool.csproj",
            "offramp config convert --project Billing.Tool --sections appSettings,connectionStrings --apply",
            "offramp config convert --project Billing.Tool --shim --apply");
        return command;
    }

    public Task<CommandOutcome<ConfigConvertResult>> ExecuteAsync(ConfigConvertOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return Task.FromResult(CommandOutcome<ConfigConvertResult>.Environment());
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return Task.FromResult(CommandOutcome<ConfigConvertResult>.Usage());
        }

        var project = model.Projects.Single(p => p.Id == id);
        using var loader = new CompilationLoader(root);
        var plan = ConfigConverter.Plan(new ConfigConvertRequest
        {
            RepositoryRoot = root,
            Project = project,
            Compilation = loader.LoadForProject(project),
            OutputDirectory = options.Out,
            Sections = options.Sections,
            Shim = options.Shim,
            Diagnostics = context.Diagnostics,
        });
        if (plan is null)
        {
            return Task.FromResult(CommandOutcome<ConfigConvertResult>.Usage());
        }

        return ApplyAsync(plan, context, cancellationToken);
    }

    private static async Task<CommandOutcome<ConfigConvertResult>> ApplyAsync(ConfigConvertPlan plan, CommandContext context, CancellationToken cancellationToken)
    {
        if (!context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<ConfigConvertResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(context.Repository.Path, context.Host.GitService).ApplyAsync(plan.ChangeSet, "config convert", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ConfigConvertResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(ConfigConvertResult result, CommandContext context, HumanOutput output)
    {
        var converted = result.Sections.Where(s => s.Kind is "appSettings" or "connectionStrings" or "custom").ToList();
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Source}: {converted.Sum(s => s.Values)} value{(converted.Sum(s => s.Values) == 1 ? "" : "s")} in {converted.Count} section{(converted.Count == 1 ? "" : "s")} {(result.Applied ? "written to" : "for")} {string.Join(", ", result.Files.Where(f => f.EndsWith(".json", StringComparison.Ordinal)))}."),
            Theme.ReadyStyle);
        foreach (var section in result.Sections)
        {
            var style = section.Kind is "unsupported" ? "yellow" : section.Kind is "dropped" ? "dim" : "green";
            output.MarkupLine($"  [{style}]{Markup.Escape(section.Kind)}[/] {Markup.Escape(section.Name)}{(section.JsonKey is { Length: > 0 } key ? $" → \"{Markup.Escape(key)}\"" : "")}{(section.Options is { } options ? $" [dim]({Markup.Escape(options)})[/]" : "")}");
            foreach (var note in section.Notes)
            {
                output.MarkupLine($"    [dim]{Markup.Escape(note)}[/]");
            }
        }

        foreach (var transform in result.Transforms)
        {
            output.MarkupLine($"  [dim]transform[/] {Markup.Escape(transform.File)} → {Markup.Escape(transform.Output ?? "nothing")} [dim]({transform.Overrides} override{(transform.Overrides == 1 ? "" : "s")}, {transform.NotConverted.Count} not converted)[/]");
        }

        foreach (var step in result.NextSteps)
        {
            output.MarkupLine($"[dim]Next:[/] {Markup.Escape(step)}");
        }

        IfdefRendering.Tail(result.Applied, result.Journal, result.Preview, output);
    }
}
