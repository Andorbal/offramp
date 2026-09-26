using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis.Compilations;
using Offramp.Analyzers.CodeFixes;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Refactoring;
using Offramp.Refactoring.Codemods;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using Spectre.Console;
using Catalog = Offramp.Analyzers.Codemods;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp codemod</c>: bulk, idempotent rewrites (docs/spec/commands/codemod.md).</summary>
public static class CodemodCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var codemod = new Command("codemod", "Bulk, idempotent Roslyn rewrites for recurring migration patterns, with a dry-run diff and a verifying build.");
        codemod.Subcommands.Add(CodemodListCommand.Create(host, globals));
        codemod.Subcommands.Add(CodemodRunCommand.Create(host, globals));
        return codemod;
    }

    public static CodemodInfo Info(CodemodImplementation implementation) => new()
    {
        Id = implementation.Codemod.Id,
        Name = implementation.Codemod.Name,
        Title = implementation.Codemod.Title,
        Description = implementation.Codemod.Description,
        OptIn = implementation.Codemod.OptIn,
        Experimental = implementation.Codemod.Experimental,
        RewritesCode = implementation.Fixer is not null,
        Packages = [.. implementation.Codemod.Packages.Select(p => new CodemodPackageInfo(p.Id, p.Version, p.Targets.ToString().ToLowerInvariant()))],
    };
}

public sealed record CodemodListOptions;

/// <summary><c>offramp codemod list</c>.</summary>
public sealed class CodemodListCommand : ICommandHandler<CodemodListOptions, CodemodListResult>
{
    public string CommandPath => "codemod list";

    public JsonTypeInfo<CodemodListResult> ResultType => RefactoringJsonContext.Default.CodemodListResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("list", "The codemod catalog: IDs, names, what each rewrites, and the packages it adds.");
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new CodemodListCommand(), new CodemodListOptions(), globals.Bind(parse), host, ct));
        HelpExamples.Add(command, "offramp codemod list", "offramp codemod list --json");
        return command;
    }

    public Task<CommandOutcome<CodemodListResult>> ExecuteAsync(CodemodListOptions options, CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandOutcome<CodemodListResult>.Completed(new CodemodListResult { Codemods = [.. CodemodRegistry.All.Select(CodemodCommands.Info)] }));

    public void Render(CodemodListResult result, CommandContext context, HumanOutput output)
    {
        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("ID");
        table.AddColumn("Name");
        table.AddColumn("Rewrite");
        table.AddColumn("Packages");
        foreach (var codemod in result.Codemods)
        {
            var flags = (codemod.OptIn ? " [yellow](opt-in)[/]" : "") + (codemod.Experimental ? " [yellow](experimental)[/]" : "") + (codemod.RewritesCode ? "" : " [dim](package only)[/]");
            table.AddRow(codemod.Id, Markup.Escape(codemod.Name) + flags, Markup.Escape(codemod.Title),
                Markup.Escape(string.Join(", ", codemod.Packages.Select(p => $"{p.Id} {p.Version}{(p.Targets == "all" ? "" : $" ({p.Targets} targets)")}"))));
        }

        output.Write(table);
        output.MarkupLine("[dim]Run one with[/] offramp codemod run --mod NAME [dim](dry run; add --apply to write).[/]");
    }
}

public sealed record CodemodRunOptions(IReadOnlyList<string> Mods, IReadOnlyList<string> Projects, string? Verify, bool Experimental, bool FormatMode);

/// <summary><c>offramp codemod run</c>.</summary>
public sealed class CodemodRunCommand : ICommandHandler<CodemodRunOptions, CodemodRunResult>
{
    public string CommandPath => "codemod run";

    public JsonTypeInfo<CodemodRunResult> ResultType => RefactoringJsonContext.Default.CodemodRunResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var mods = new Option<string[]>("--mod") { Description = "Codemods by name or ID (comma-separated or repeated); `all` is every codemod that is neither opt-in nor experimental.", HelpName = "NAME", Required = true, AllowMultipleArgumentsPerToken = true };
        var projects = new Option<string[]>("--project") { Description = "Projects to rewrite (path or name, repeatable); default every C# project with a recorded compilation.", HelpName = "PROJECT", AllowMultipleArgumentsPerToken = true };
        var verify = new Option<string?>("--verify") { Description = "After --apply: end (default) builds the changed projects and their dependents, rolling back on failure; none skips it.", HelpName = "WHEN" };
        verify.AcceptOnlyFromAmong("end", "none");
        var experimental = new Option<bool>("--experimental") { Description = "Allow experimental codemods (right less than ~95% of the time; review every change)." };
        var formatMode = new Option<bool>("--format-mode") { Description = "Delegate to `dotnet format analyzers --diagnostics OFRM###` in projects that reference the Offramp.Analyzers package." };
        var command = new Command("run", "Run codemods over projects: a diff by default, --apply to write through a journal and verify with a build.")
        {
            mods, projects, verify, experimental, formatMode,
        };
        command.SetAction((parse, ct) =>
        {
            static List<string> Split(string[]? values) =>
                [.. (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
            return CommandRunner.RunAsync(new CodemodRunCommand(),
                new CodemodRunOptions(Split(parse.GetValue(mods)), Split(parse.GetValue(projects)), parse.GetValue(verify), parse.GetValue(experimental), parse.GetValue(formatMode)),
                globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp codemod run --mod sqlclient",
            "offramp codemod run --mod process-start-url,timezone-ids --project src/Shop/Shop.csproj --apply",
            "offramp codemod run --mod all --apply --verify none",
            "offramp codemod run --mod sqlclient --format-mode --apply");
        return command;
    }

    public async Task<CommandOutcome<CodemodRunResult>> ExecuteAsync(CodemodRunOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        if (Select(options, context) is not { } codemods)
        {
            return CommandOutcome<CodemodRunResult>.Usage();
        }

        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<CodemodRunResult>.Environment();
        }

        if (Projects(options, model, context) is not { } projects)
        {
            return CommandOutcome<CodemodRunResult>.Usage();
        }

        var apply = context.Settings.Apply && !context.Settings.DryRun;
        if (options.FormatMode)
        {
            var formatted = await CodemodFormatter.RunAsync(new CodemodFormatRequest
            {
                RepositoryRoot = root,
                Projects = projects,
                Codemods = [.. codemods.Select(c => c.Codemod)],
                Apply = apply,
                Processes = context.Host.Processes,
                Diagnostics = context.Diagnostics,
                Progress = context.Progress,
            }, cancellationToken);
            return CommandOutcome<CodemodRunResult>.Completed(formatted);
        }

        using var loader = new CompilationLoader(root);
        var plan = await CodemodRunner.PlanAsync(new CodemodRequest
        {
            RepositoryRoot = root,
            Model = model,
            Projects = projects,
            Codemods = codemods,
            Loader = loader,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        }, cancellationToken);
        if (!apply)
        {
            return CommandOutcome<CodemodRunResult>.Completed(plan.Result);
        }

        var outcome = await CodemodExecutor.ApplyAsync(plan, new MoveExecution
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            VerifyPolicy = options.Verify ?? "end",
            Git = context.Host.GitService,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Now = context.Host.Time.GetUtcNow(),
        }, cancellationToken);
        return outcome.Partial ? new CommandOutcome<CodemodRunResult>(outcome.Result, OutcomeKind.Partial) : CommandOutcome<CodemodRunResult>.Completed(outcome.Result);
    }

    /// <summary>The codemods named, in catalog order; null after reporting an unknown (OFR4502) or experimental (OFR4503) one.</summary>
    private static List<CodemodImplementation>? Select(CodemodRunOptions options, CommandContext context)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var ok = true;
        foreach (var name in options.Mods)
        {
            if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
            {
                chosen.UnionWith(Catalog.All.Where(c => !c.OptIn && (!c.Experimental || options.Experimental)).Select(c => c.Id));
                continue;
            }

            var codemod = Catalog.ByName(name) ?? Catalog.ByName(name.ToUpperInvariant());
            if (codemod is null)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR4502, $"'{name}' is not a codemod; `offramp codemod list` shows the catalog.",
                    data: [KeyValuePair.Create<string, JsonNode?>("mod", name)]);
                ok = false;
                continue;
            }

            if (codemod.Experimental && !options.Experimental)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR4503, $"{codemod.Name} ({codemod.Id}) is experimental; pass --experimental and review every change.",
                    data: [KeyValuePair.Create<string, JsonNode?>("mod", codemod.Name)]);
                ok = false;
                continue;
            }

            chosen.Add(codemod.Id);
        }

        return ok ? [.. CodemodRegistry.All.Where(i => chosen.Contains(i.Codemod.Id))] : null;
    }

    /// <summary>The named projects, or every C# project with a recorded compilation; null after reporting an unknown one.</summary>
    private static List<ProjectInfo>? Projects(CodemodRunOptions options, WorkspaceModel model, CommandContext context)
    {
        if (options.Projects.Count == 0)
        {
            return [.. model.Projects.Where(p => p.Language == "csharp" && p.CompilerCalls.Count > 0).OrderBy(p => p.Id, StringComparer.Ordinal)];
        }

        var projects = new List<ProjectInfo>();
        foreach (var name in options.Projects)
        {
            if (ProjectLookup.Resolve(name, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{name}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", name)]);
                return null;
            }

            projects.Add(model.Projects.Single(p => p.Id == id));
        }

        return [.. projects.DistinctBy(p => p.Id).OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    public void Render(CodemodRunResult result, CommandContext context, HumanOutput output)
    {
        var summary = result.Summary;
        var verb = result.RolledBack ? "Rolled back" : result.Applied ? "Rewrote" : "Would rewrite";
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{verb} {summary.SitesRewritten} site{(summary.SitesRewritten == 1 ? "" : "s")} in {summary.FilesChanged} file{(summary.FilesChanged == 1 ? "" : "s")} across {summary.Projects} project{(summary.Projects == 1 ? "" : "s")}; {summary.SitesSkipped} skipped ({string.Join(", ", result.Codemods)})."),
            result.RolledBack ? Theme.BlockingStyle : summary.SitesRewritten == 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            output.MarkupLine($"  [bold]{Markup.Escape(project.Project)}[/]{(project.TargetFramework is { } tfm ? $" [dim]({Markup.Escape(tfm)})[/]" : "")}");
            foreach (var site in project.Sites)
            {
                var style = site.Outcome == CodemodSiteOutcome.Skipped ? "yellow" : "green";
                output.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                    $"    [{style}]{Markup.Escape(Wire(site.Outcome))}[/] {Markup.Escape(site.Codemod)} [dim]{Markup.Escape(site.File)}:{site.Line}[/]{(site.Reason is { } reason ? $" [dim]{Markup.Escape(reason)}[/]" : "")}"));
            }

            foreach (var package in project.Packages)
            {
                output.MarkupLine($"    [dim]package:[/] {Markup.Escape(package.Id)} {Markup.Escape(package.Version)}{(package.Condition is { } condition ? $" [dim]when {Markup.Escape(condition)}[/]" : "")}");
            }

            foreach (var property in project.Properties)
            {
                output.MarkupLine($"    [dim]property:[/] <{Markup.Escape(property.Name)}>{Markup.Escape(property.Value)}</{Markup.Escape(property.Name)}>");
            }
        }

        if (result.Mode == CodemodMode.Format)
        {
            output.MarkupLine(result.Applied ? "[dim]dotnet format wrote the files.[/]" : "[dim]Dry run (dotnet format --verify-no-changes). Apply with[/] --apply");
            return;
        }

        IfdefRendering.Tail(result.Applied, result.Journal, result.Preview, output);
    }

    private static string Wire(CodemodSiteOutcome outcome) => outcome switch
    {
        CodemodSiteOutcome.Rewritten => "rewritten",
        CodemodSiteOutcome.Skipped => "skipped",
        _ => "referenced",
    };
}
