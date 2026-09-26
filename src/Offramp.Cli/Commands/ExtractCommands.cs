using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Seams;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Refactoring;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Extract;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp extract</c>: code edits that cut a dependency (docs/spec/commands/seams.md#extract-interface).</summary>
public static class ExtractCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var extract = new Command("extract", "Edits that cut a dependency: put an interface between callers and a type that cannot port.");
        extract.Subcommands.Add(ExtractInterfaceCommand.Create(host, globals));
        return extract;
    }
}

public sealed record ExtractInterfaceOptions(string Project, string? Type, string? Name, IReadOnlyList<string> Members, string? FromSeams, string Di);

/// <summary><c>offramp extract interface</c>.</summary>
public sealed class ExtractInterfaceCommand : ICommandHandler<ExtractInterfaceOptions, ExtractInterfaceResult>
{
    public string CommandPath => "extract interface";

    public JsonTypeInfo<ExtractInterfaceResult> ResultType => RefactoringJsonContext.Default.ExtractInterfaceResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string>("--project") { Description = "The project that declares the type (path or name).", HelpName = "PROJECT", Required = true };
        var type = new Option<string?>("--type") { Description = "The class to extract from, fully qualified. Defaults to the seam's boundary type with --from-seams.", HelpName = "NS.Type" };
        var name = new Option<string?>("--name") { Description = "The interface name. Defaults to the seam's proposed name, or I + the type's name.", HelpName = "IName" };
        var members = new Option<string[]>("--members") { Description = "Member names to put on the interface (comma-separated or repeated); default every public instance member.", HelpName = "m1,m2", AllowMultipleArgumentsPerToken = true };
        var fromSeams = new Option<string?>("--from-seams") { Description = "A seam from `offramp seams --out seams.json`, as FILE#ID: its members and callers.", HelpName = "FILE#ID" };
        var di = new Option<string>("--di") { Description = "The registration snippet to print: microsoft, autofac, or none.", HelpName = "CONTAINER", DefaultValueFactory = _ => "microsoft" };
        di.AcceptOnlyFromAmong("microsoft", "autofac", "none");
        var command = new Command("interface", "Generate an interface next to a type with the members its callers use, implement it, and retype the callers' injected dependencies to it; compiled in memory before anything is written.")
        {
            project, type, name, members, fromSeams, di,
        };
        command.Validators.Add(result =>
        {
            if (result.GetValue(type) is null && result.GetValue(fromSeams) is null)
            {
                result.AddError("Pass --type, or --from-seams FILE#ID.");
            }
        });
        command.SetAction((parse, ct) =>
        {
            var list = (parse.GetValue(members) ?? []).SelectMany(m => m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            return CommandRunner.RunAsync(new ExtractInterfaceCommand(),
                new ExtractInterfaceOptions(parse.GetValue(project)!, parse.GetValue(type), parse.GetValue(name), list, parse.GetValue(fromSeams), parse.GetValue(di)!),
                globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp extract interface --project Accounts --type Accounts.Directory.DirectoryLookup",
            "offramp seams --project Accounts --out seams.json && offramp extract interface --project Accounts --from-seams seams.json#seam-1 --apply");
        return command;
    }

    public async Task<CommandOutcome<ExtractInterfaceResult>> ExecuteAsync(ExtractInterfaceOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<ExtractInterfaceResult>.Environment();
        }

        if (ProjectLookup.Resolve(options.Project, model, context) is not { } id)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{options.Project}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", options.Project)]);
            return CommandOutcome<ExtractInterfaceResult>.Usage();
        }

        Seam? seam = null;
        if (options.FromSeams is { } reference)
        {
            seam = await ReadSeamAsync(reference, context, cancellationToken);
            if (seam is null)
            {
                return CommandOutcome<ExtractInterfaceResult>.Usage();
            }
        }

        var project = model.Projects.Single(p => p.Id == id);
        using var loader = new CompilationLoader(root);
        if (loader.LoadForProject(project) is not { } compilation)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(id));
            return CommandOutcome<ExtractInterfaceResult>.Environment();
        }

        var plan = InterfaceExtractor.Plan(new ExtractInterfaceRequest
        {
            RepositoryRoot = root,
            Project = project,
            Type = options.Type ?? seam!.BoundaryType,
            Name = options.Name ?? seam?.ProposedInterface,
            Members = options.Members,
            SeamMembers = options.Members.Count == 0 && seam is not null ? [.. seam.Members.Select(m => m.Signature)] : [],
            Callers = seam?.Callers ?? [],
            Di = options.Di,
            Diagnostics = context.Diagnostics,
        }, compilation);
        if (plan is null)
        {
            return CommandOutcome<ExtractInterfaceResult>.Usage();
        }

        if (!context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<ExtractInterfaceResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "extract interface", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ExtractInterfaceResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    /// <summary>Reads <c>FILE#ID</c>: a <c>seams</c> result (or its <c>--json</c> envelope) and one of its seams.</summary>
    private static async Task<Seam?> ReadSeamAsync(string reference, CommandContext context, CancellationToken cancellationToken)
    {
        var hash = reference.LastIndexOf('#');
        var file = hash < 0 ? reference : reference[..hash];
        var id = hash < 0 ? "seam-1" : reference[(hash + 1)..];
        try
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(Path.GetFullPath(file, context.Host.WorkingDirectory), cancellationToken));
            var result = node?["result"] is JsonObject inner && node["command"]?.GetValue<string>() == "seams" ? inner : node;
            var seams = result?.Deserialize(AnalysisJsonContext.Default.SeamsResult);
            if (seams?.Seams.FirstOrDefault(s => s.Id == id) is { } seam)
            {
                return seam;
            }

            context.Diagnostics.Report(DiagnosticCatalog.OFR4013, $"'{file}' has no seam '{id}'; it has {(seams is null || seams.Seams.Count == 0 ? "none" : string.Join(", ", seams.Seams.Select(s => s.Id)))}.",
                data: [KeyValuePair.Create<string, JsonNode?>("path", file)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR4013, $"'{file}' is not a readable seams document: {ex.Message}",
                data: [KeyValuePair.Create<string, JsonNode?>("path", file)]);
        }

        return null;
    }

    public void Render(ExtractInterfaceResult result, CommandContext context, HumanOutput output)
    {
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{result.Interface} on {result.Type}: {result.Members.Count} member{(result.Members.Count == 1 ? "" : "s")}, {result.Callers.Count} caller{(result.Callers.Count == 1 ? "" : "s")} retyped, {result.DirectInstantiations.Count} still create{(result.DirectInstantiations.Count == 1 ? "s" : "")} it with new."),
            result.DirectInstantiations.Count > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        output.MarkupLine($"  [dim]new file[/] {Markup.Escape(result.File)}");
        foreach (var member in result.Members)
        {
            output.MarkupLine($"    {Markup.Escape(member)}");
        }

        foreach (var caller in result.Callers)
        {
            output.MarkupLine($"  {Markup.Escape(caller.Type)} [dim]({Markup.Escape(caller.File)})[/]");
            foreach (var change in caller.Changes)
            {
                output.MarkupLine($"    [dim]{Markup.Escape(change)}[/]");
            }
        }

        foreach (var instantiation in result.DirectInstantiations)
        {
            output.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"  [yellow]new[/] {Markup.Escape(instantiation.Type)} [dim]{Markup.Escape(instantiation.File)}:{instantiation.Line}[/]"));
        }

        if (result.Registration is { } registration)
        {
            output.MarkupLine($"[dim]Register:[/] {Markup.Escape(registration)}");
        }

        IfdefRendering.Tail(result.Applied, result.Journal, result.Preview, output);
    }
}
