using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Compilations;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding;
using Offramp.Scaffolding.Remote;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Cli.Commands;

public sealed record RemoteOptions(
    string Interface, string? Implementation, string? Project, string HostFramework, string Serializer,
    string? HostDirectory, string? ClientDirectory, string? ContractsDirectory, bool Container, bool AsyncVariant, IReadOnlyList<string> SkipMembers);

/// <summary><c>offramp remote</c> (docs/spec/commands/seams.md#remote).</summary>
public sealed class RemoteCommand : ICommandHandler<RemoteOptions, RemoteResult>
{
    public string CommandPath => "remote";

    public JsonTypeInfo<RemoteResult> ResultType => ScaffoldingJsonContext.Default.RemoteResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var contract = new Option<string>("--interface") { Description = "The interface to put on the network, fully qualified (from extract interface).", HelpName = "NS.IName", Required = true };
        var implementation = new Option<string?>("--implementation") { Description = "The class the host runs; default the one class in the project that implements the interface.", HelpName = "NS.Type" };
        var project = new Option<string?>("--project") { Description = "The project that declares the interface (path or name); default the project whose sources declare it.", HelpName = "PROJECT" };
        var hostFramework = new Option<string>("--host-framework") { Description = "auto (net10.0-windows when the implementation compiles for it, else net48), net10-windows, or net48.", HelpName = "FRAMEWORK", DefaultValueFactory = _ => "auto" };
        hostFramework.AcceptOnlyFromAmong("auto", "net10-windows", "net48");
        var transport = new Option<string>("--transport") { Description = "http-json (POST per member). gRPC is planned (docs/decisions/0023).", HelpName = "TRANSPORT", DefaultValueFactory = _ => "http-json" };
        transport.AcceptOnlyFromAmong("http-json");
        var serializer = new Option<string>("--serializer") { Description = "The client's JSON serializer: stj (System.Text.Json) or newtonsoft.", HelpName = "SERIALIZER", DefaultValueFactory = _ => "stj" };
        serializer.AcceptOnlyFromAmong("stj", "newtonsoft");
        var hostDir = new Option<string?>("--host-dir") { Description = "Where the host project goes (default next to the project: NAME.Windows.Host).", HelpName = "DIR" };
        var clientDir = new Option<string?>("--client-dir") { Description = "Where the client project goes (default NAME.Remote).", HelpName = "DIR" };
        var contractsDir = new Option<string?>("--contracts-dir") { Description = "Where the contracts project goes (default NAME.Remote.Contracts).", HelpName = "DIR" };
        var container = new Option<bool>("--container") { Description = "Also write a Windows Dockerfile and a Kubernetes Deployment and Service for the host." };
        var asyncVariant = new Option<bool>("--async-variant") { Description = "Also generate INameAsync (Task-returning members with cancellation) implemented by the client." };
        var skip = new Option<string[]>("--skip-member") { Description = "Interface members that stay local: the client throws NotSupportedException for them (comma-separated or repeated).", HelpName = "MEMBER", AllowMultipleArgumentsPerToken = true };
        var command = new Command("remote", "Turn a seam interface into an HTTP boundary: a contracts project, a Windows host that runs the implementation, and a client that implements the interface by calling the host.")
        {
            contract, implementation, project, hostFramework, transport, serializer, hostDir, clientDir, contractsDir, container, asyncVariant, skip,
        };
        command.SetAction((parse, ct) =>
        {
            var skipped = (parse.GetValue(skip) ?? []).SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).ToList();
            return CommandRunner.RunAsync(new RemoteCommand(), new RemoteOptions(
                parse.GetValue(contract)!, parse.GetValue(implementation), parse.GetValue(project), parse.GetValue(hostFramework)!, parse.GetValue(serializer)!,
                parse.GetValue(hostDir), parse.GetValue(clientDir), parse.GetValue(contractsDir), parse.GetValue(container), parse.GetValue(asyncVariant), skipped),
                globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp remote --interface Accounts.Directory.IDirectoryLookup --skip-member Watch",
            "offramp remote --interface Accounts.Directory.IDirectoryLookup --host-framework net10-windows --container --async-variant --apply");
        return command;
    }

    public async Task<CommandOutcome<RemoteResult>> ExecuteAsync(RemoteOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<RemoteResult>.Environment();
        }

        using var loader = new CompilationLoader(root);
        (ProjectInfo Project, CSharpCompilation Compilation)? found = null;
        if (options.Project is { } name)
        {
            if (ProjectLookup.Resolve(name, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{name}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", name)]);
                return CommandOutcome<RemoteResult>.Usage();
            }

            var project = model.Projects.Single(p => p.Id == id);
            if (loader.LoadForProject(project) is not CSharpCompilation compilation)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(id));
                return CommandOutcome<RemoteResult>.Environment();
            }

            found = (project, compilation);
        }
        else
        {
            found = Declaring(model.Projects, loader, options.Interface);
            if (found is null)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR4023, $"No project in the workspace model declares an interface '{options.Interface}'; pass --project, or run `offramp scan` after extract interface.");
                return CommandOutcome<RemoteResult>.Usage();
            }
        }

        var plan = await RemoteGenerator.PlanAsync(new RemoteRequest
        {
            RepositoryRoot = root,
            Project = found.Value.Project,
            Interface = options.Interface,
            Implementation = options.Implementation,
            HostFramework = options.HostFramework,
            Serializer = options.Serializer,
            HostDirectory = options.HostDirectory,
            ClientDirectory = options.ClientDirectory,
            ContractsDirectory = options.ContractsDirectory,
            Container = options.Container,
            AsyncVariant = options.AsyncVariant,
            SkipMembers = options.SkipMembers,
            Solution = model.Solution,
            References = new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)),
            Diagnostics = context.Diagnostics,
        }, found.Value.Compilation, cancellationToken);
        if (plan is null)
        {
            return CommandOutcome<RemoteResult>.Usage();
        }

        if (plan.ChangeSet is null || !context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<RemoteResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "remote", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<RemoteResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    /// <summary>The C# project whose recorded sources declare the interface.</summary>
    private static (ProjectInfo, CSharpCompilation)? Declaring(IEnumerable<ProjectInfo> projects, CompilationLoader loader, string name)
    {
        var simple = name[(name.LastIndexOf('.') + 1)..];
        foreach (var project in projects.Where(p => p.Language == "csharp").OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (loader.LoadForProject(project) is CSharpCompilation compilation
                && compilation.GetSymbolsWithName(simple, SymbolFilter.Type).OfType<INamedTypeSymbol>().Any(t => t.TypeKind == TypeKind.Interface && t.ToDisplayString() == name))
            {
                return (project, compilation);
            }
        }

        return null;
    }

    public void Render(RemoteResult result, CommandContext context, HumanOutput output)
    {
        var generated = result.Files.Count > 0;
        output.Headline(generated
            ? string.Create(CultureInfo.InvariantCulture, $"{result.Interface} over HTTP: {result.Members.Count(m => m.Status == "remote")} member(s) remote, host on {result.HostFramework}, {result.Files.Count} files.")
            : $"{result.Interface}: nothing generated.",
            generated ? Theme.ReadyStyle : Theme.BlockingStyle);
        foreach (var member in result.Members)
        {
            var status = member.Status switch
            {
                "remote" => $"[{Theme.ReadyStyle}]{Markup.Escape(member.Route ?? "")}[/]{(member.Sync ? " [yellow]sync[/]" : "")}",
                "skipped" => "[dim]skipped[/]",
                _ => $"[{Theme.BlockingStyle}]blocked[/] [dim]{Markup.Escape(string.Join("; ", member.Problems))}[/]",
            };
            output.MarkupLine($"  {Markup.Escape(member.Signature)} {status}");
        }

        if (result.HostFrameworkReason is { } reason)
        {
            output.MarkupLine($"[dim]Host framework:[/] {Markup.Escape(result.HostFramework ?? "")} [dim]({Markup.Escape(reason)})[/]");
        }

        foreach (var project in result.Projects)
        {
            output.MarkupLine($"  [dim]{Markup.Escape(project.Role)}[/] {Markup.Escape(project.Path)} [dim]({Markup.Escape(string.Join(";", project.TargetFrameworks))})[/]");
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal ?? "")}");
        }
        else if (generated)
        {
            output.MarkupLine("[dim]Dry run. Write the projects with[/] --apply [dim](the JSON output has the full preview).[/]");
        }

        foreach (var step in result.NextSteps)
        {
            output.MarkupLine($"[dim]Next:[/] {Markup.Escape(step)}");
        }
    }
}
