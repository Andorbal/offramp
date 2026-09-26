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
using Offramp.Scaffolding.Service;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record ServiceOptions(string Project, string Host, string? Out, bool Dockerfile, bool Kubernetes, bool Health, string Logging);

/// <summary><c>offramp service</c> (docs/spec/commands/scaffold.md#service).</summary>
public sealed class ServiceCommand : ICommandHandler<ServiceOptions, ServiceResult>
{
    public string CommandPath => "service";

    public JsonTypeInfo<ServiceResult> ResultType => ScaffoldingJsonContext.Default.ServiceResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The Windows service's project (path or name).", HelpName = "PROJECT", Required = true };
        var hostOption = new Option<string>("--host") { Description = "linux (a container), windows (a Windows service), or both.", HelpName = "HOST", DefaultValueFactory = _ => "linux" };
        hostOption.AcceptOnlyFromAmong("linux", "windows", "both");
        var output = new Option<string?>("--out") { Description = "The worker project's directory (default NAME.Worker next to the project).", HelpName = "DIR" };
        var dockerfile = new Option<bool>("--dockerfile") { Description = "Also write a Linux Dockerfile (multi-stage, non-root)." };
        var kubernetes = new Option<bool>("--k8s") { Description = "Also write a Kubernetes Deployment and ConfigMap." };
        var health = new Option<bool>("--health") { Description = "Serve /health with each worker's last heartbeat (the project becomes Microsoft.NET.Sdk.Web)." };
        var logging = new Option<string>("--logging") { Description = "json-console or simple.", HelpName = "FORMAT", DefaultValueFactory = _ => "json-console" };
        logging.AcceptOnlyFromAmong("json-console", "simple");
        var command = new Command("service", "Turn a Windows service (ServiceBase or Topshelf) into a worker for the generic host: a Linux container, a Windows service, or both.")
        {
            project, hostOption, output, dockerfile, kubernetes, health, logging,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new ServiceCommand(),
            new ServiceOptions(parse.GetValue(project)!, parse.GetValue(hostOption)!, parse.GetValue(output), parse.GetValue(dockerfile), parse.GetValue(kubernetes), parse.GetValue(health), parse.GetValue(logging)!),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp service --project src/Heartbeat/Heartbeat.csproj",
            "offramp service --project Heartbeat --health --dockerfile --k8s --apply",
            "offramp service --project Heartbeat --host both --apply");
        return command;
    }

    public async Task<CommandOutcome<ServiceResult>> ExecuteAsync(ServiceOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<ServiceResult>.Environment();
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return CommandOutcome<ServiceResult>.Usage();
        }

        var project = model.Projects.Single(p => p.Id == id);
        using var loader = new CompilationLoader(root);
        if (loader.LoadForProject(project) is not CSharpCompilation compilation)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(id));
            return CommandOutcome<ServiceResult>.Environment();
        }

        var plan = await ServiceGenerator.PlanAsync(new ServiceRequest
        {
            RepositoryRoot = root,
            Project = project,
            TargetMajor = config.Target,
            Host = options.Host,
            OutputDirectory = options.Out,
            Dockerfile = options.Dockerfile,
            Kubernetes = options.Kubernetes,
            Health = options.Health,
            Logging = options.Logging,
            References = new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)),
            Diagnostics = context.Diagnostics,
        }, compilation, cancellationToken);
        if (plan is null)
        {
            return CommandOutcome<ServiceResult>.Usage();
        }

        if (plan.ChangeSet is null || !context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<ServiceResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "service", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ServiceResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(ServiceResult result, CommandContext context, HumanOutput output)
    {
        var generated = result.Worker is not null;
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Project}: {result.Services.Count} service{(result.Services.Count == 1 ? "" : "s")}{(generated ? $" → {result.Worker!.Path} ({result.Worker.TargetFramework}, {result.Host})" : ", nothing generated")}."),
            generated ? Theme.ReadyStyle : Theme.BlockingStyle);
        foreach (var service in result.Services)
        {
            output.MarkupLine($"  [bold]{Markup.Escape(service.ServiceName)}[/] [dim]{Markup.Escape(service.Kind)} {Markup.Escape(service.Type)} → {Markup.Escape(service.Worker)}[/]");
            if (service.Lifecycle.Count > 0)
            {
                output.MarkupLine($"    [dim]lifecycle:[/] {Markup.Escape(string.Join(", ", service.Lifecycle))}");
            }

            foreach (var timer in service.Timers)
            {
                output.MarkupLine($"    [dim]timer:[/] {Markup.Escape(timer.Name)} {Markup.Escape(timer.Kind)}{(timer.Converted ? $" → PeriodicTimer every {Markup.Escape(timer.Interval ?? "")} ms" : " (kept)")}");
            }
        }

        foreach (var removal in result.Removals)
        {
            output.MarkupLine($"  [dim]remove later:[/] {Markup.Escape(removal.Path)} [dim]({Markup.Escape(removal.Reason)})[/]");
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal ?? "")}");
        }
        else if (generated)
        {
            output.MarkupLine("[dim]Dry run. Write the worker project with[/] --apply [dim](the JSON output has the full preview).[/]");
        }

        foreach (var step in result.NextSteps)
        {
            output.MarkupLine($"[dim]Next:[/] {Markup.Escape(step)}");
        }
    }
}
