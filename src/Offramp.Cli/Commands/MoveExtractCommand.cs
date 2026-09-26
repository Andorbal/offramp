using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Json;
using Offramp.Core.Paths;
using Offramp.Refactoring;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record MoveExtractOptions(string From, IReadOnlyList<string> Types, IReadOnlyList<string> Files, string New, IReadOnlyList<string> TargetFrameworks, string? Directory, string? Verify);

/// <summary><c>offramp move extract</c>: move types into a new project (docs/spec/commands/move.md#move-extract).</summary>
public sealed class MoveExtractCommand : ICommandHandler<MoveExtractOptions, MoveExtractResult>
{
    public string CommandPath => "move extract";

    public JsonTypeInfo<MoveExtractResult> ResultType => RefactoringJsonContext.Default.MoveExtractResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var from = new Option<string>("--from") { Description = "The project the types are in (path or name).", HelpName = "PROJECT", Required = true };
        var types = new Option<string[]>("--types") { Description = "Types to move, fully qualified or by a unique name; comma-separated or repeated.", HelpName = "TYPES", AllowMultipleArgumentsPerToken = true };
        var files = new Option<string[]>("--files") { Description = "Files to move: paths or globs (relative to the current directory), matched against the source project's files.", HelpName = "GLOB", AllowMultipleArgumentsPerToken = true };
        var newProject = new Option<string>("--new") { Description = "The new project's name; its file is NAME.csproj.", HelpName = "NAME", Required = true };
        var tfm = new Option<string?>("--tfm") { Description = "Its target frameworks, semicolon-separated (default: the source's).", HelpName = "TFMS" };
        var directory = new Option<string?>("--dir") { Description = "Its folder (default: NAME beside the source project's folder).", HelpName = "PATH" };
        var verify = new Option<string?>("--verify") { Description = "With --apply: none, end (default, from move.verify), per-project, or batch:N.", HelpName = "POLICY" };
        var command = new Command("extract", "Move types into a new project: create it from a template (the source's settings, analyzers, and framework references), then plan and apply the move.")
        {
            from, types, files, newProject, tfm, directory, verify,
        };
        command.Validators.Add(r =>
        {
            if (r.GetResult(types) is null && r.GetResult(files) is null)
            {
                r.AddError("Name what to move with --types or --files.");
            }

            if (r.GetValue(verify) is { } policy && !MoveApplier.IsPolicy(policy))
            {
                r.AddError($"--verify must be none, end, per-project, or batch:N, not '{policy}'.");
            }

            if (r.GetValue(newProject) is { } name && (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0 || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            {
                r.AddError("--new is a project name, not a path; choose the folder with --dir.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(new MoveExtractCommand(),
            new MoveExtractOptions(
                parse.GetValue(from)!,
                [.. (parse.GetValue(types) ?? []).SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))],
                parse.GetValue(files) ?? [],
                parse.GetValue(newProject)!,
                [.. (parse.GetValue(tfm) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                parse.GetValue(directory),
                parse.GetValue(verify)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp move extract --from src/Billing/Billing.csproj --types Billing.Money,Billing.Invoice --new Billing.Domain --tfm \"net48;netstandard2.0\"",
            "offramp move extract --from Billing --files \"src/Billing/Model/**/*.cs\" --new Billing.Model --apply");
        return command;
    }

    public async Task<CommandOutcome<MoveExtractResult>> ExecuteAsync(MoveExtractOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<MoveExtractResult>.Environment();
        }

        if (MoveCommandSupport.Resolve(options.From, model, context) is not { } from)
        {
            return CommandOutcome<MoveExtractResult>.Usage();
        }

        var prefix = RepoPaths.ToRepositoryRelative(root, context.Host.WorkingDirectory);
        var workspaceHash = CommandRunner.WorkspaceHash(context.WorkspacePath)!;
        var plan = await MoveExtractor.PlanAsync(new MoveExtractRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            WorkspaceHash = workspaceHash,
            From = from,
            Types = options.Types,
            Files = [.. options.Files.Select(f => MovePlanCommand.Anchor(prefix, f))],
            NewName = options.New,
            TargetFrameworks = options.TargetFrameworks,
            Directory = options.Directory is null ? null : RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(options.Directory, context.Host.WorkingDirectory)),
            References = new TargetReferenceResolver(root, context.Host.Processes, CommandRunner.Cache(context)),
            Diagnostics = context.Diagnostics,
        }, cancellationToken);
        if (plan is null)
        {
            return context.Diagnostics.Contains("OFR0004") ? CommandOutcome<MoveExtractResult>.Environment() : CommandOutcome<MoveExtractResult>.Usage();
        }

        var result = plan.Result;
        if (!context.Settings.Apply || context.Settings.DryRun || result.Plan.Moves.Count == 0)
        {
            return CommandOutcome<MoveExtractResult>.Completed(result);
        }

        // The journal names its plan (for --resume); the plan lives with Offramp's state.
        var planPath = $".offramp/plans/move-extract-{options.New}.json";
        var absolute = RepoPaths.ToAbsolute(root, planPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, OfframpJson.Serialize(result.Plan, RefactoringJsonContext.Default.MovePlanDocument), new System.Text.UTF8Encoding(false), cancellationToken);
        var outcome = await MoveApplier.ApplyAsync(new MoveApplyRequest
        {
            RepositoryRoot = root,
            Model = plan.Model,
            Config = config,
            Plan = result.Plan,
            PlanPath = planPath,
            WorkspaceHash = workspaceHash,
            VerifyPolicy = options.Verify ?? result.Plan.Verify,
            OnFailure = config.Verify.OnFailure,
            Git = context.Host.GitService,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Now = context.Host.Time.GetUtcNow(),
            Created = plan.Created,
        }, cancellationToken);
        result = result with { Apply = outcome.Result, Preview = null };
        return outcome.Status switch
        {
            MoveApplyStatus.Partial => new CommandOutcome<MoveExtractResult>(result, OutcomeKind.Partial),
            _ => CommandOutcome<MoveExtractResult>.Completed(result),
        };
    }

    public void Render(MoveExtractResult result, CommandContext context, HumanOutput output)
    {
        var plan = result.Plan;
        var apply = result.Apply;
        var what = MoveCommandSupport.Files(plan.Moves.Count);
        var failed = apply?.Verifications.FirstOrDefault(v => !v.Passed);
        var headline = plan.Moves.Count == 0 ? $"Nothing can move from {result.From} to a new {result.NewProject}."
            : apply is null ? $"Would create {result.NewProject} ({string.Join(";", result.TargetFrameworks)}) and move {what} into it from {result.From}."
            : apply.RolledBack ? $"Rolled back: verification failed{(failed is null ? "" : " (" + failed.Scope + ")")}; nothing was created or moved."
            : $"Created {result.NewProject} and moved {MoveCommandSupport.Files(apply.Moved.Count)} into it{(apply.Verifications.Count > 0 && failed is null ? ", verified" : "")}.";
        output.Headline(headline, plan.Moves.Count == 0 || apply?.RolledBack == true || failed is not null ? Theme.BlockingStyle : Theme.ReadyStyle);
        foreach (var type in result.Types)
        {
            output.MarkupLine($"  [dim]type[/] {Markup.Escape(type.Type)} [dim]{Markup.Escape(string.Join(", ", type.Files))}[/]");
        }

        foreach (var move in plan.Moves)
        {
            output.MarkupLine($"  {Markup.Escape(move.File)} → {Markup.Escape(move.To)}{(move.CoMoveOf is null ? "" : $" [dim](needed by {Markup.Escape(move.CoMoveOf)})[/]")}");
        }

        foreach (var edit in plan.ProjectEdits)
        {
            output.MarkupLine($"[dim]Edit:[/] {Markup.Escape(edit.Project)} [dim]{MoveCommandSupport.Describe(edit)}[/]");
        }

        foreach (var excluded in plan.Excluded)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Stays:[/] {Markup.Escape(excluded.File)} [dim]{Markup.Escape(excluded.Code)} {Markup.Escape(excluded.Message)}[/]");
        }

        if (result.Preview is { Length: > 0 } preview)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(preview, output);
            output.MarkupLine("[dim]Dry run. Create the project and move the files with[/] --apply");
        }

        if (apply is { Applied: true, RolledBack: false })
        {
            output.MarkupLine($"[dim]Staged:[/] {apply.Moved.Count} rename{(apply.Moved.Count == 1 ? "" : "s")} (git mv). [dim]Unstaged:[/] {Markup.Escape(string.Join(", ", [result.NewProject, .. apply.Edited]))}.");
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(apply.Journal!)}");
        }
    }
}
