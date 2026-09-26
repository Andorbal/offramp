using System.CommandLine;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Paths;
using Offramp.Refactoring;
using Offramp.Refactoring.Dependencies.Consolidation;
using Offramp.NuGet.Feeds;
using Offramp.Refactoring.ChangeSets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record DepsConsolidateOptions(string? Package, string? Family, bool All, string? Prefer, bool Cpm, string? OptInVia, string Verify);

/// <summary><c>offramp deps consolidate</c>: one version per package, checked by NuGet's own restore.</summary>
public sealed class DepsConsolidateCommand : ICommandHandler<DepsConsolidateOptions, ConsolidateResult>
{
    public string CommandPath => "deps consolidate";

    public JsonTypeInfo<ConsolidateResult> ResultType => RefactoringJsonContext.Default.ConsolidateResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var package = new Option<string?>("--package") { Description = "Consolidate this package (and the rest of its family).", HelpName = "ID" };
        var all = new Option<bool>("--all") { Description = "Consolidate every package referenced directly." };
        var family = new Option<string?>("--family") { Description = "Consolidate the packages whose id starts with this prefix.", HelpName = "PREFIX" };
        var prefer = new Option<string?>("--prefer") { Description = "lowest (default) or newest version that satisfies every constraint (deps.preferNewest).", HelpName = "WHICH" };
        prefer.AcceptOnlyFromAmong("lowest", "newest");
        var cpm = new Option<bool>("--cpm") { Description = "Move versions into a central props file (deps.cpm.file) with central package management." };
        var optIn = new Option<string?>("--opt-in-via") { Description = "With a non-default central file, opt projects in through this shared props file instead of each project.", HelpName = "PATH" };
        var verify = new Option<string>("--verify") { Description = "restore (default): NuGet restores the proposal in a scratch copy first; build: then build it too; none.", DefaultValueFactory = _ => "restore", HelpName = "HOW" };
        verify.AcceptOnlyFromAmong("restore", "build", "none");
        var command = new Command("consolidate", "One version per package across the solution, respecting pins, families, and transitive constraints, verified by NuGet's own restore.")
        {
            package, all, family, prefer, cpm, optIn, verify,
        };
        command.Validators.Add(r =>
        {
            if (new[] { r.GetResult(package) is not null, r.GetResult(all) is not null, r.GetResult(family) is not null }.Count(g => g) != 1)
            {
                r.AddError("Choose one of --package, --all, and --family.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new DepsConsolidateCommand(),
            new DepsConsolidateOptions(parse.GetValue(package), parse.GetValue(family), parse.GetValue(all), parse.GetValue(prefer), parse.GetValue(cpm), parse.GetValue(optIn), parse.GetValue(verify) ?? "restore"),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp deps consolidate --package Newtonsoft.Json",
            "offramp deps consolidate --family Microsoft.Extensions. --apply",
            "offramp deps consolidate --all --cpm --apply");
        return command;
    }

    public async Task<CommandOutcome<ConsolidateResult>> ExecuteAsync(DepsConsolidateOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var (model, _, _) = DepsCommands.Load(context, null);
        if (model is null)
        {
            return CommandOutcome<ConsolidateResult>.Environment();
        }

        var root = context.Repository.Path;
        var config = context.Config.Config;
        using var feeds = NuGetPackageFeeds.ForRepository(root, config.Deps.Feeds);
        var plan = await Consolidator.PlanAsync(new ConsolidateRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            Feeds = feeds,
            Cache = CommandRunner.Cache(context),
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Package = options.Package,
            Family = options.Family,
            Prefer = options.Prefer ?? (config.Deps.PreferNewest ? "newest" : "lowest"),
            Cpm = options.Cpm,
            OptInVia = options.OptInVia is null ? null : RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(options.OptInVia, context.Host.WorkingDirectory)),
        }, cancellationToken);
        if (plan is null)
        {
            return CommandOutcome<ConsolidateResult>.Usage();
        }

        var result = plan.Result;
        if (plan.ChangeSet is null)
        {
            return CommandOutcome<ConsolidateResult>.Completed(result);
        }

        if (options.Verify != "none")
        {
            var verification = await RestoreVerifier.VerifyAsync(new RestoreVerifyRequest
            {
                RepositoryRoot = root,
                Model = model,
                Config = config,
                ChangeSet = plan.ChangeSet,
                Mode = options.Verify,
                Git = context.Host.GitService,
                Processes = context.Host.Processes,
                Diagnostics = context.Diagnostics,
                Progress = context.Progress,
            }, cancellationToken);
            result = result with { Verification = verification };
            if (!verification.Passed)
            {
                return CommandOutcome<ConsolidateResult>.Completed(result);
            }
        }

        if (!context.Settings.Apply || context.Settings.DryRun)
        {
            return CommandOutcome<ConsolidateResult>.Completed(result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "deps consolidate", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ConsolidateResult>.Completed(result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(ConsolidateResult result, CommandContext context, HumanOutput output)
    {
        var changing = result.Packages.Where(p => p.Changes.Count > 0).ToList();
        var blocked = result.Verification is { Passed: false };
        var headline = blocked ? "Not applied: restore of the proposal reported problems."
            : changing.Count == 0 ? $"Nothing to consolidate: {Count(result.Packages.Count, "package")} already on one version each."
            : result.Applied ? $"Consolidated {Count(changing.Count, "package")}."
            : $"Would consolidate {Count(changing.Count, "package")}{(result.Verification is { Passed: true } ? " (restore verified)" : "")}.";
        output.Headline(headline, blocked || result.Unsatisfiable.Count > 0 ? Theme.BlockingStyle : changing.Count == 0 ? Theme.ReadyStyle : Theme.DecisionStyle);

        // One block per package that changes or cannot: versions, the choice, and why.
        foreach (var package in result.Packages.Where(p => p.Current.Count > 1 || p.Changes.Count > 0 || p.Selected is null))
        {
            var current = string.Join(", ", package.Current.Select(c => c.Version));
            var selected = package.Selected is null ? $"[{Theme.BlockingStyle}]no version[/]" : $"[{Theme.ReadyStyle}]{Markup.Escape(package.Selected)}[/]";
            output.MarkupLine($"[bold]{Markup.Escape(package.Id)}[/] {Markup.Escape(current)} → {selected}");
            output.MarkupLine($"  [dim]{Markup.Escape(package.Reason)}[/]");
            foreach (var pinned in package.Pinned)
            {
                output.MarkupLine($"  [dim]Pinned to {Markup.Escape(pinned.Version)}: {Markup.Escape(string.Join(", ", pinned.Projects))}[/]");
            }
        }

        foreach (var warning in result.Verification?.Warnings ?? [])
        {
            output.MarkupLine($"[{Theme.BlockingStyle}]{Markup.Escape(warning.Code)}[/] {Markup.Escape(warning.Project ?? "")}: {Markup.Escape(warning.Message)}");
        }

        if (result.Preview is { Length: > 0 } preview && !blocked)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);
            output.Line();
            output.MarkupLine("[dim]Dry run. Apply with[/] --apply");
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal!)}");
        }
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");
}
