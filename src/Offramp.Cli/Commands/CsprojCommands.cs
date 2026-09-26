using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis.Compilations;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding;
using Offramp.Scaffolding.Csproj;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

/// <summary><c>offramp csproj</c>: project file conversions (docs/spec/commands/scaffold.md#csproj-modernize).</summary>
public static class CsprojCommands
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var csproj = new Command("csproj", "Project file conversions: legacy projects to SDK style, and modern settings for all.");
        csproj.Subcommands.Add(CsprojModernizeCommand.Create(host, globals));
        return csproj;
    }
}

public sealed record CsprojModernizeOptions(IReadOnlyList<string> Projects, bool All, IReadOnlyList<string> TargetFrameworks, string? Nullable, bool AcceptDiff);

/// <summary><c>offramp csproj modernize</c>.</summary>
public sealed class CsprojModernizeCommand : ICommandHandler<CsprojModernizeOptions, ModernizeResult>
{
    public string CommandPath => "csproj modernize";

    public JsonTypeInfo<ModernizeResult> ResultType => ScaffoldingJsonContext.Default.ModernizeResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var projects = new Option<string[]>("--project") { Description = "Projects to modernize (path or name, repeatable).", HelpName = "PROJECT", AllowMultipleArgumentsPerToken = true };
        var all = new Option<bool>("--all") { Description = "Every C# project in the workspace model." };
        var tfm = new Option<string?>("--tfm") { Description = "Target frameworks for every modernized project, semicolon-separated (net48;net10.0).", HelpName = "TFMS" };
        var nullable = new Option<string?>("--nullable") { Description = "Set Nullable (enable, disable, warnings, annotations); left alone unless given.", HelpName = "VALUE" };
        nullable.AcceptOnlyFromAmong("enable", "disable", "warnings", "annotations");
        var acceptDiff = new Option<bool>("--accept-diff") { Description = "Apply even when the converted build compiles different inputs (OFR4303)." };
        var command = new Command("modernize", "Convert legacy projects to SDK style (packages.config, globs, AssemblyInfo, build events), proved by building both and comparing what the compiler gets.")
        {
            projects, all, tfm, nullable, acceptDiff,
        };
        command.Validators.Add(result =>
        {
            if ((result.GetValue(projects) ?? []).Length == 0 && !result.GetValue(all))
            {
                result.AddError("Pass --project PROJECT (repeatable) or --all.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new CsprojModernizeCommand(),
            new CsprojModernizeOptions(
                [.. (parse.GetValue(projects) ?? []).SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))],
                parse.GetValue(all),
                [.. (parse.GetValue(tfm) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                parse.GetValue(nullable),
                parse.GetValue(acceptDiff)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp csproj modernize --project src/Billing/Billing.csproj",
            "offramp csproj modernize --all --apply",
            "offramp csproj modernize --project Billing --tfm \"net48;net10.0\" --apply");
        return command;
    }

    public async Task<CommandOutcome<ModernizeResult>> ExecuteAsync(CsprojModernizeOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<ModernizeResult>.Environment();
        }

        var projects = new List<ProjectInfo>();
        if (options.All)
        {
            projects.AddRange(model.Projects.Where(p => p.Language == "csharp"));
        }

        foreach (var name in options.Projects)
        {
            if (ProjectLookup.Resolve(name, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{name}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", name)]);
                return CommandOutcome<ModernizeResult>.Usage();
            }

            projects.Add(model.Projects.Single(p => p.Id == id));
        }

        using var loader = new CompilationLoader(root);
        var plan = await ModernizePlanner.PlanAsync(new ModernizeRequest
        {
            RepositoryRoot = root,
            Model = model,
            Projects = [.. projects.DistinctBy(p => p.Id)],
            TargetFrameworks = options.TargetFrameworks,
            Nullable = options.Nullable,
            Loader = loader,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        }, cancellationToken);

        var changed = plan.Result.Projects.Where(p => p.Changed).Select(p => p.Project).ToList();
        var result = plan.Result;
        if (changed.Count > 0)
        {
            var verifications = await ModernizeVerifier.VerifyAsync(new ModernizeVerifyRequest
            {
                RepositoryRoot = root,
                Model = model,
                Config = config,
                ChangeSet = plan.ChangeSet,
                Projects = changed,
                ConvertedPackages = [.. result.Projects.SelectMany(p => p.Packages).Select(p => (p.Id, p.Version)).Distinct()],
                Git = context.Host.GitService,
                Processes = context.Host.Processes,
                Diagnostics = context.Diagnostics,
                Progress = context.Progress,
            }, cancellationToken);
            result = result with
            {
                Projects = [.. result.Projects.Select(p => verifications.TryGetValue(p.Project, out var v) ? p with { Verification = v } : p)],
            };
        }

        var verified = result.Projects.All(p => p.Verification is null || p.Verification.Passed);
        if (!context.Settings.Apply || context.Settings.DryRun || plan.ChangeSet.IsEmpty || (!verified && !options.AcceptDiff))
        {
            return CommandOutcome<ModernizeResult>.Completed(result);
        }

        var journal = await new ChangeSetApplier(root, context.Host.GitService).ApplyAsync(plan.ChangeSet, "csproj modernize", context.Host.Time.GetUtcNow(), cancellationToken);
        return CommandOutcome<ModernizeResult>.Completed(result with { Applied = true, Journal = journal, Preview = null });
    }

    public void Render(ModernizeResult result, CommandContext context, HumanOutput output)
    {
        var converted = result.Projects.Count(p => p.Style == "legacy" && p.Changed);
        var failed = result.Projects.Count(p => p.Verification is { Passed: false });
        output.Headline(string.Create(CultureInfo.InvariantCulture,
            $"{(result.Applied ? "Modernized" : "Would modernize")} {result.Projects.Count(p => p.Changed)} project{(result.Projects.Count(p => p.Changed) == 1 ? "" : "s")} ({converted} converted to SDK style){(failed > 0 ? $"; {failed} compile{(failed == 1 ? "s" : "")} different inputs" : "")}."),
            failed > 0 ? Theme.BlockingStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            var verdict = project.Verification is null ? "" : project.Verification.Passed ? " [green]same compile set[/]" : " [red]different compile set[/]";
            output.MarkupLine($"  [bold]{Markup.Escape(project.Project)}[/] [dim]{Markup.Escape(project.Style)} → {Markup.Escape(string.Join(";", project.TargetFrameworks))}{(project.CompileItems is { } items ? $", {items} compile items" : "")}[/]{verdict}");
            if (project.Skipped is { } skipped)
            {
                output.MarkupLine($"    [yellow]not converted:[/] {Markup.Escape(skipped)}");
            }

            foreach (var package in project.Packages)
            {
                output.MarkupLine($"    [dim]package:[/] {Markup.Escape(package.Id)} {Markup.Escape(package.Version)}");
            }

            foreach (var property in project.Properties)
            {
                output.MarkupLine($"    [dim]property:[/] <{Markup.Escape(property.Name)}>{Markup.Escape(property.Value)}</{Markup.Escape(property.Name)}>");
            }

            foreach (var target in project.Verification?.Targets ?? [])
            {
                if (target.ImplicitReferencesAdded.Count > 0)
                {
                    output.MarkupLine($"    [dim]{Markup.Escape(target.TargetFramework)}: the SDK also references {Markup.Escape(string.Join(", ", target.ImplicitReferencesAdded))}[/]");
                }

                if (target.TransitiveReferencesAdded.Count > 0)
                {
                    output.MarkupLine($"    [dim]{Markup.Escape(target.TargetFramework)}: packages of referenced projects now flow here: {Markup.Escape(string.Join(", ", target.TransitiveReferencesAdded))}[/]");
                }
            }
        }

        IfdefRendering.Tail(result.Applied, result.Journal, result.Preview, output);
    }
}
