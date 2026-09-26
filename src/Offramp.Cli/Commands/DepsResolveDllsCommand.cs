using System.CommandLine;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.NuGet.Feeds;
using Offramp.Refactoring;
using Offramp.Refactoring.Dependencies.Resolution;
using Offramp.Refactoring.ChangeSets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record DepsResolveDllsOptions(string? Project);

/// <summary><c>offramp deps resolve-dlls</c>: loose DLL references become project or package references.</summary>
public sealed class DepsResolveDllsCommand : ICommandHandler<DepsResolveDllsOptions, ResolveDllsResult>
{
    public string CommandPath => "deps resolve-dlls";

    public JsonTypeInfo<ResolveDllsResult> ResultType => RefactoringJsonContext.Default.ResolveDllsResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var project = new Option<string?>("--project") { Description = "Only this project (path or name).", HelpName = "PROJECT" };
        var command = new Command("resolve-dlls", "Replace References with a HintPath by the project that builds the DLL or the package that ships it; report the rest.")
        {
            project,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new DepsResolveDllsCommand(), new DepsResolveDllsOptions(parse.GetValue(project)), globals.Bind(parse), host, ct));
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
        using var feeds = NuGetPackageFeeds.ForRepository(root, config.Deps.Feeds);
        var plan = await DllResolver.PlanAsync(new ResolveDllsRequest
        {
            RepositoryRoot = root,
            Model = model,
            Feeds = feeds,
            Cache = CommandRunner.Cache(context),
            Diagnostics = context.Diagnostics,
            Project = project,
            IncludePrerelease = config.Deps.IncludePrerelease,
        }, cancellationToken);
        if (!context.Settings.Apply || context.Settings.DryRun || plan.ChangeSet is null)
        {
            return CommandOutcome<ResolveDllsResult>.Completed(plan.Result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "deps resolve-dlls", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ResolveDllsResult>.Completed(plan.Result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(ResolveDllsResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        var total = s.Project + s.Package + s.Unmatched;
        output.Headline(
            total == 0 ? "No loose DLL references."
            : string.Create(CultureInfo.InvariantCulture, $"{total} loose DLL reference{(total == 1 ? "" : "s")}: {s.Project} to a project, {s.Package} to a package, {s.Unmatched} unmatched{(s.Blockers > 0 ? $" ({s.Blockers} blocking the target)" : "")}."),
            s.Blockers > 0 ? Theme.BlockingStyle : s.Unmatched > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            output.MarkupLine($"[bold]{Markup.Escape(project.Project)}[/]");
            foreach (var dll in project.References)
            {
                var target = dll.Resolution.Kind switch
                {
                    DllResolutionKind.Project => $"[{Theme.ReadyStyle}]project[/] {Markup.Escape(dll.Resolution.Project!)}",
                    DllResolutionKind.Package => $"[{Theme.ReadyStyle}]package[/] {Markup.Escape(dll.Resolution.Package!)} {Markup.Escape(dll.Resolution.Version!)}",
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
    }
}
