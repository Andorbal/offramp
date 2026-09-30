using System.CommandLine;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.NuGet.Feeds;
using Offramp.Refactoring;
using Offramp.Refactoring.Dependencies.Resolution;
using Offramp.Refactoring.Moves;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record DepsResolveDllsOptions(string? Project, string? Verify = null);

/// <summary><c>offramp deps resolve-dlls</c>: loose DLL references become project or package references.</summary>
public sealed class DepsResolveDllsCommand : ICommandHandler<DepsResolveDllsOptions, ResolveDllsResult>
{
    public string CommandPath => "deps resolve-dlls";

    public JsonTypeInfo<ResolveDllsResult> ResultType => RefactoringJsonContext.Default.ResolveDllsResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string?>("--project") { Description = "Only this project (path or name).", HelpName = "PROJECT" };
        var verify = new Option<string?>("--verify") { Description = "After --apply: end (default) restores and builds the edited projects and their dependents, rolling back on failure; none skips it.", HelpName = "WHEN" };
        verify.AcceptOnlyFromAmong("end", "none");
        var command = new Command("resolve-dlls", "Replace References with a HintPath by the project that builds the DLL or the package that ships it; report the rest.")
        {
            project, verify,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new DepsResolveDllsCommand(), new DepsResolveDllsOptions(parse.GetValue(project), parse.GetValue(verify)), globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp deps resolve-dlls",
            "offramp deps resolve-dlls --project src/App/App.csproj --apply");
        return command;
    }

    public async Task<CommandOutcome<ResolveDllsResult>> ExecuteAsync(DepsResolveDllsOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var (model, project, usageError) = DepsCommands.Load(context, options.Project);
        if (usageError)
        {
            return CommandOutcome<ResolveDllsResult>.Usage();
        }

        if (model is null)
        {
            return CommandOutcome<ResolveDllsResult>.Environment();
        }

        var root = context.Repository.Path;
        var config = context.Config.Config;
        using var feeds = NuGetPackageFeeds.ForWorkspace(root, config.Deps.Feeds, model);
        var plan = await DllResolver.PlanAsync(new ResolveDllsRequest
        {
            RepositoryRoot = root,
            Model = model,
            Feeds = feeds,
            Cache = CommandRunner.Cache(context),
            Diagnostics = context.Diagnostics,
            Project = project,
            IncludePrerelease = config.Deps.IncludePrerelease,
            AssemblyPackages = config.Deps.AssemblyPackages ?? [],
        }, cancellationToken);
        if (!context.Settings.Apply || context.Settings.DryRun || plan.ChangeSet is null)
        {
            return CommandOutcome<ResolveDllsResult>.Completed(plan.Result);
        }

        var outcome = await DllResolveExecutor.ApplyAsync(plan, new MoveExecution
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
        return outcome.Partial ? new CommandOutcome<ResolveDllsResult>(outcome.Result, OutcomeKind.Partial) : CommandOutcome<ResolveDllsResult>.Completed(outcome.Result);
    }

    public void Render(ResolveDllsResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        var total = s.Project + s.Package + s.Unmatched;
        var installed = s.PackagesConfig == 0 ? ""
            : string.Create(CultureInfo.InvariantCulture, $" {s.PackagesConfig} more come from packages.config packages and need nothing.");
        output.Headline(
            (total == 0 ? "No loose DLL references."
            : string.Create(CultureInfo.InvariantCulture, $"{total} loose DLL reference{(total == 1 ? "" : "s")}: {s.Project} to a project, {s.Package} to a package, {s.Unmatched} unmatched{(s.Blockers > 0 ? $" ({s.Blockers} blocking the target)" : "")}.")) + installed,
            s.Blockers > 0 ? Theme.BlockingStyle : s.Unmatched > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            var loose = project.References.Where(r => r.Resolution.Kind != DllResolutionKind.PackagesConfig).ToList();
            if (loose.Count == 0)
            {
                continue;
            }

            output.MarkupLine($"[bold]{Markup.Escape(project.Project)}[/]");
            foreach (var dll in loose)
            {
                var target = dll.Resolution.Kind switch
                {
                    DllResolutionKind.Project => $"[{Theme.ReadyStyle}]project[/] {Markup.Escape(dll.Resolution.Project!)}",
                    DllResolutionKind.Package => $"[{(dll.Resolution.Match == DllMatch.Newer ? Theme.DecisionStyle : Theme.ReadyStyle)}]package[/] {Markup.Escape(dll.Resolution.Package!)} {Markup.Escape(dll.Resolution.Version!)} [dim]({Matched(dll.Resolution.Match)})[/]",
                    _ => dll.Blocker ? $"[{Theme.BlockingStyle}]blocker[/]" : $"[{Theme.DecisionStyle}]unmatched[/]",
                };
                output.MarkupLine($"  {Markup.Escape(dll.HintPath)} [dim]({Markup.Escape(dll.AssemblyVersion ?? "?")}, {Markup.Escape(dll.TargetFramework ?? "?")})[/] → {target}");
            }
        }

        if (result.Preview is { Length: > 0 } preview)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);
            output.Line();
            output.MarkupLine("[dim]Dry run. Apply with[/] --apply");
        }

        if (result.RolledBack)
        {
            output.MarkupLine($"[{Theme.BlockingStyle}]Verification failed, so every project file was restored.[/]");
        }
        else if (result.Applied)
        {
            output.MarkupLine($"[dim]Applied{(result.Verify is { Passed: true } ? " and verified" : "")}. Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal!)}");
        }
    }

    private static string Matched(DllMatch? match) => match switch
    {
        DllMatch.Path => "its path names it",
        DllMatch.Identical => "the same file",
        DllMatch.FileVersion => "same file version",
        DllMatch.InformationalVersion => "same informational version",
        DllMatch.AssemblyVersion => "closest build",
        DllMatch.Newer => "newer: an upgrade",
        _ => "",
    };
}
