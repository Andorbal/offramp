using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Compilations;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding;
using Offramp.Scaffolding.Web;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp web</c>: ASP.NET (System.Web) applications (docs/spec/commands/scaffold.md#web-inventory).</summary>
public static class WebCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var web = new Command("web", "ASP.NET (System.Web) applications: what they are made of, and a strangler-fig ASP.NET Core front.");
        web.Subcommands.Add(WebInventoryCommand.Create(host, globals));
        web.Subcommands.Add(WebScaffoldCommand.Create(host, globals));
        return web;
    }
}

public sealed record WebInventoryOptions(string Project);

/// <summary><c>offramp web inventory</c>.</summary>
public sealed class WebInventoryCommand(string format) : ICommandHandler<WebInventoryOptions, WebInventoryResult>, IRawOutput<WebInventoryResult>
{
    public string CommandPath => "web inventory";

    public JsonTypeInfo<WebInventoryResult> ResultType => ScaffoldingJsonContext.Default.WebInventoryResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The ASP.NET application's project (path or name).", HelpName = "PROJECT", Required = true };
        var format = new Option<string>("--format") { Description = "table (the terminal view), json (the result alone), or markdown.", HelpName = "FORMAT", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "json", "markdown");
        var command = new Command("inventory", "What an ASP.NET application is made of: controllers, actions, routes, filters, modules, handlers, Global.asax, Web Forms, session, authentication, and its System.Web API surface.")
        {
            project, format,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new WebInventoryCommand(parse.GetValue(format)!), new WebInventoryOptions(parse.GetValue(project)!), globals.Bind(parse), host, ct));
        HelpExamples.Add(command, "offramp web inventory --project src/Shop.Web/Shop.Web.csproj", "offramp web inventory --project Shop.Web --format markdown > docs/web-inventory.md");
        return command;
    }

    public Task<CommandOutcome<WebInventoryResult>> ExecuteAsync(WebInventoryOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return Task.FromResult(CommandOutcome<WebInventoryResult>.Environment());
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return Task.FromResult(CommandOutcome<WebInventoryResult>.Usage());
        }

        var project = model.Projects.Single(p => p.Id == id);
        using var loader = new CompilationLoader(root);
        if (loader.LoadForProject(project) is not { } compilation)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(id));
            return Task.FromResult(CommandOutcome<WebInventoryResult>.Environment());
        }

        return Task.FromResult(CommandOutcome<WebInventoryResult>.Completed(WebInventory.Analyze(root, project, compilation)));
    }

    public string? RawOutput(WebInventoryResult result, CommandContext context) => format switch
    {
        "markdown" => WebInventoryMarkdown.Write(result),
        "json" => Offramp.Core.Json.OfframpJson.Serialize(result, ScaffoldingJsonContext.Default.WebInventoryResult),
        _ => null,
    };

    public void Render(WebInventoryResult result, CommandContext context, HumanOutput output)
    {
        var actions = result.Controllers.Sum(c => c.Actions.Count);
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Project} ({result.Kind}): {result.Controllers.Count} controllers, {actions} actions, {result.Routes.Count} convention routes, {result.Modules.Count} modules, {result.Handlers.Count} handlers, {result.WebForms.Count} Web Forms files."),
            Theme.ReadyStyle);
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Controller");
        table.AddColumn("Action");
        table.AddColumn("Methods");
        table.AddColumn("Routes");
        foreach (var controller in result.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                table.AddRow(Markup.Escape(controller.Name) + $" [dim]{controller.Kind}{(controller.Area is null ? "" : " " + Markup.Escape(controller.Area))}[/]", Markup.Escape(action.Name),
                    Markup.Escape(action.HttpMethods.Count == 0 ? "any" : string.Join(",", action.HttpMethods)),
                    Markup.Escape(action.Routes.Count == 0 ? "convention" : string.Join(", ", action.Routes)));
            }
        }

        output.Write(table);
        foreach (var route in result.Routes)
        {
            output.MarkupLine($"  [dim]route[/] {Markup.Escape(route.Name)} {Markup.Escape(route.Template)} [dim]({Markup.Escape(route.Kind)})[/]");
        }

        foreach (var component in result.Modules.Concat(result.Handlers))
        {
            output.MarkupLine($"  [dim]{(result.Modules.Contains(component) ? "module" : "handler")}[/] {Markup.Escape(component.Name)} [dim]{Markup.Escape(component.Type)}[/]");
        }

        var settings = result.Settings;
        output.MarkupLine($"  [dim]authentication[/] {Markup.Escape(settings.Authentication ?? "none")} [dim]session[/] {Markup.Escape(settings.SessionState ?? "default")} ({result.Session.Count} uses) [dim]Web Forms[/] {result.WebForms.Count}");
    }
}

public sealed record WebScaffoldOptions(string Project, string New, string Proxy, bool Adapters, string? LegacyUrl);

/// <summary><c>offramp web scaffold</c> (docs/spec/commands/scaffold.md#web-scaffold).</summary>
public sealed class WebScaffoldCommand : ICommandHandler<WebScaffoldOptions, WebScaffoldResult>
{
    public string CommandPath => "web scaffold";

    public JsonTypeInfo<WebScaffoldResult> ResultType => ScaffoldingJsonContext.Default.WebScaffoldResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The ASP.NET application's project (path or name).", HelpName = "PROJECT", Required = true };
        var newProject = new Option<string>("--new") { Description = "The new ASP.NET Core project's folder; its name is the folder's name.", HelpName = "DIR", Required = true };
        var proxy = new Option<string>("--proxy") { Description = "yarp (the new application proxies the rest to the legacy one) or none (an ingress does; the path list is written).", HelpName = "PROXY", DefaultValueFactory = _ => "yarp" };
        proxy.AcceptOnlyFromAmong("yarp", "none");
        var adapters = new Option<bool>("--adapters") { Description = "Share session and authentication with the legacy application through the System.Web adapters (remote app client)." };
        var legacyUrl = new Option<string?>("--legacy-url") { Description = "The legacy application's URL (default: the project's IIS URL).", HelpName = "URL" };
        var command = new Command("scaffold", "Strangler fig: a new ASP.NET Core application that serves the controller actions that port and sends everything else to the legacy application.")
        {
            project, newProject, proxy, adapters, legacyUrl,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new WebScaffoldCommand(),
            new WebScaffoldOptions(parse.GetValue(project)!, parse.GetValue(newProject)!, parse.GetValue(proxy)!, parse.GetValue(adapters), parse.GetValue(legacyUrl)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp web scaffold --project Shop.Web --new src/Shop.Web.Core",
            "offramp web scaffold --project Shop.Web --new src/Shop.Web.Core --adapters --apply",
            "offramp web scaffold --project Shop.Web --new src/Shop.Web.Core --proxy none --legacy-url http://shop-legacy.internal/ --apply");
        return command;
    }

    public async Task<CommandOutcome<WebScaffoldResult>> ExecuteAsync(WebScaffoldOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<WebScaffoldResult>.Environment();
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return CommandOutcome<WebScaffoldResult>.Usage();
        }

        var project = model.Projects.Single(p => p.Id == id);
        using var loader = new CompilationLoader(root);
        if (loader.LoadForProject(project) is not CSharpCompilation compilation)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(id));
            return CommandOutcome<WebScaffoldResult>.Environment();
        }

        var plan = await WebScaffolder.PlanAsync(new WebScaffoldRequest
        {
            RepositoryRoot = root,
            Project = project,
            NewDirectory = options.New,
            TargetMajor = config.Target,
            Proxy = options.Proxy,
            Adapters = options.Adapters,
            LegacyUrl = options.LegacyUrl,
            References = new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)),
            Diagnostics = context.Diagnostics,
        }, compilation, cancellationToken);
        if (plan is null)
        {
            return CommandOutcome<WebScaffoldResult>.Usage();
        }

        if (!context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<WebScaffoldResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "web scaffold", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<WebScaffoldResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(WebScaffoldResult result, CommandContext context, HumanOutput output)
    {
        var ported = result.Controllers.Sum(c => c.Ported.Count);
        var unported = result.Controllers.Sum(c => c.Unported.Count);
        var generated = result.Files.Count > 0;
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Project} → {result.NewProject} ({result.TargetFramework}, proxy {result.Proxy}{(result.Adapters ? ", adapters" : "")}): {ported} action{(ported == 1 ? "" : "s")} ported, {unported} left to the legacy application."),
            generated ? Theme.ReadyStyle : Theme.BlockingStyle);
        foreach (var controller in result.Controllers)
        {
            output.MarkupLine($"  [bold]{Markup.Escape(controller.Type)}[/]{(controller.File is { } file ? $" [dim]→ {Markup.Escape(file)}[/]" : "")}");
            if (controller.Ported.Count > 0)
            {
                output.MarkupLine($"    [green]ported[/] {Markup.Escape(string.Join(", ", controller.Ported))}");
            }

            foreach (var action in controller.Unported)
            {
                output.MarkupLine($"    [yellow]legacy[/] {Markup.Escape(action.Action)} [dim]{Markup.Escape(action.Reason)}[/]");
            }
        }

        foreach (var route in result.Routes)
        {
            output.MarkupLine($"  [dim]route[/] {Markup.Escape(route)}");
        }

        foreach (var stub in result.Middleware.Concat(result.Endpoints))
        {
            output.MarkupLine($"  [dim]stub[/] {Markup.Escape(stub)}");
        }

        if (result.WebForms.Count > 0)
        {
            output.MarkupLine($"  [dim]Web Forms, left to the legacy application:[/] {Markup.Escape(string.Join(", ", result.WebForms))}");
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal ?? "")}");
        }
        else if (generated)
        {
            output.MarkupLine("[dim]Dry run. Write the project with[/] --apply [dim](the JSON output has the full preview).[/]");
        }

        foreach (var step in result.NextSteps)
        {
            output.MarkupLine($"[dim]Next:[/] {Markup.Escape(step)}");
        }
    }
}
