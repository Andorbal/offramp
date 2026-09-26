using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Model;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record MovePlanOptions(string From, string To, IReadOnlyList<string> Files, string? FilesFrom, bool All, string? CoMove, string? NamespaceMismatch);

/// <summary><c>offramp move plan</c>: a reviewable plan for moving files between projects; changes nothing.</summary>
public sealed class MovePlanCommand : ICommandHandler<MovePlanOptions, MovePlanResult>
{
    public string CommandPath => "move plan";

    public JsonTypeInfo<MovePlanResult> ResultType => RefactoringJsonContext.Default.MovePlanResult;

    /// <summary><c>--out</c> receives the plan file, not the envelope.</summary>
    public bool WritesOwnOutput => true;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var from = new Option<string>("--from") { Description = "The project the files are in (path or name).", HelpName = "PROJECT", Required = true };
        var to = new Option<string>("--to") { Description = "The project to move them to (path or name).", HelpName = "PROJECT", Required = true };
        var files = new Option<string[]>("--files")
        {
            Description = "Files to move: paths or globs (relative to the current directory), matched against the source project's files.",
            HelpName = "GLOB",
            AllowMultipleArgumentsPerToken = true,
        };
        var filesFrom = new Option<string?>("--files-from") { Description = "A file listing paths to move, one per line (# starts a comment).", HelpName = "LIST" };
        var all = new Option<bool>("--all") { Description = "Plan every file of the source project." };
        var coMove = new Option<string?>("--co-move") { Description = "closure (default, from move.coMove): bring along what the files need; none: exclude them instead.", HelpName = "MODE" };
        coMove.AcceptOnlyFromAmong("closure", "none");
        var namespaces = new Option<string?>("--namespace-mismatch") { Description = "allow (default, from move.namespaceMismatch), warn, or block files outside the destination's root namespace.", HelpName = "MODE" };
        namespaces.AcceptOnlyFromAmong("allow", "warn", "block");
        var command = new Command("plan", "Plan a move of files between projects: what moves, what comes along, which references to add, and what stays and why. Changes nothing.")
        {
            from, to, files, filesFrom, all, coMove, namespaces,
        };
        command.Validators.Add(r =>
        {
            var given = new[] { r.GetResult(files) is not null, r.GetResult(filesFrom) is not null, r.GetResult(all) is not null }.Count(g => g);
            if (given != 1)
            {
                r.AddError("Choose one of --files, --files-from, and --all.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new MovePlanCommand(),
            new MovePlanOptions(
                parse.GetValue(from)!, parse.GetValue(to)!, parse.GetValue(files) ?? [], parse.GetValue(filesFrom), parse.GetValue(all),
                parse.GetValue(coMove), parse.GetValue(namespaces)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp move plan --from src/Foo/Foo.csproj --to src/Foo.Core/Foo.Core.csproj --files \"src/Foo/Util/**/*.cs\"",
            "offramp move plan --from Foo --to Foo.Core --all --out move-plan.json",
            "offramp move plan --from Foo --to Foo.Core --files-from files.txt --co-move none --json");
        return command;
    }

    public Task<CommandOutcome<MovePlanResult>> ExecuteAsync(MovePlanOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return Task.FromResult(CommandOutcome<MovePlanResult>.Environment());
        }

        var from = MoveCommandSupport.Resolve(options.From, model, context);
        var to = MoveCommandSupport.Resolve(options.To, model, context);
        if (from is null || to is null)
        {
            return Task.FromResult(CommandOutcome<MovePlanResult>.Usage());
        }

        var source = model.Projects.Single(p => p.Id == from);
        List<string> files = [];
        if (!options.All)
        {
            var selected = options.FilesFrom is not null ? FromList(options.FilesFrom, context) : Match(options.Files, source, context);
            if (selected is null)
            {
                return Task.FromResult(CommandOutcome<MovePlanResult>.Usage());
            }

            files = selected;
            if (files.Count == 0)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR2004, $"No file of {source.Id} matches {string.Join(", ", options.Files)}.", new DiagnosticLocation(source.Id));
                return Task.FromResult(CommandOutcome<MovePlanResult>.Usage());
            }
        }

        var result = MovePlanner.Plan(new MovePlanRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            WorkspaceHash = CommandRunner.WorkspaceHash(context.WorkspacePath)!,
            From = from,
            To = to,
            Files = files,
            All = options.All,
            CoMove = options.CoMove ?? config.Move.CoMove,
            NamespaceMismatch = options.NamespaceMismatch ?? config.Move.NamespaceMismatch,
            Diagnostics = context.Diagnostics,
        });
        if (result is null)
        {
            return Task.FromResult(context.Diagnostics.Contains("OFR0004") ? CommandOutcome<MovePlanResult>.Environment() : CommandOutcome<MovePlanResult>.Usage());
        }

        if (context.Settings.Out is { } outPath)
        {
            var path = Path.GetFullPath(outPath, context.Host.WorkingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, OfframpJson.Serialize(result.Plan, RefactoringJsonContext.Default.MovePlanDocument), new System.Text.UTF8Encoding(false));
            result = result with { Output = RepoPaths.ToRepositoryRelative(root, path) };
        }

        return Task.FromResult(CommandOutcome<MovePlanResult>.Completed(result));
    }

    public void Render(MovePlanResult result, CommandContext context, HumanOutput output)
    {
        var plan = result.Plan;
        var coMoves = plan.Moves.Count(m => m.CoMoveOf is not null);
        var what = MoveCommandSupport.Files(plan.Moves.Count) + (coMoves > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({coMoves} brought along)") : "");
        output.Headline(
            plan.Moves.Count == 0 ? $"Nothing can move from {plan.From} to {plan.To}." : $"Would move {what} from {plan.From} to {plan.To}.",
            plan.Moves.Count == 0 ? Theme.BlockingStyle : Theme.ReadyStyle);

        if (plan.Moves.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("File");
            table.AddColumn("To");
            table.AddColumn("Why");
            foreach (var move in plan.Moves)
            {
                table.AddRow(Markup.Escape(move.File), Markup.Escape(move.To), move.CoMoveOf is null ? "[dim]asked[/]" : $"[dim]needed by {Markup.Escape(move.CoMoveOf)}[/]");
            }

            output.Write(table);
        }

        foreach (var edit in plan.ProjectEdits)
        {
            output.MarkupLine($"[dim]Edit:[/] {Markup.Escape(edit.Project)} [dim]{MoveCommandSupport.Describe(edit)}[/]");
        }

        foreach (var excluded in plan.Excluded)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Stays:[/] {Markup.Escape(excluded.File)} [dim]{Markup.Escape(excluded.Code)} {Markup.Escape(excluded.Message)}[/]");
        }

        if (result.Preview.Length > 0)
        {
            output.Line();
            MoveCommandSupport.WriteDiff(result.Preview, output);
        }

        output.Line();
        output.MarkupLine(result.Output is null
            ? "[dim]Nothing written. Save the plan with[/] --out move-plan.json[dim], review it, then[/] offramp move apply --plan move-plan.json"
            : $"[dim]Plan written to {Markup.Escape(result.Output)}. Apply with[/] offramp move apply --plan {Markup.Escape(result.Output)}");
    }

    /// <summary>Source files matching the globs (paths relative to the working directory), in path order.</summary>
    private static List<string> Match(IReadOnlyList<string> patterns, ProjectInfo source, CommandContext context)
    {
        var root = context.Repository.Path;
        var prefix = RepoPaths.ToRepositoryRelative(root, context.Host.WorkingDirectory);
        var globs = new PathGlobs(patterns.Select(p => Anchor(prefix, p)));
        return [.. MoveCommandSupport.SourceFiles(root, source).Where(globs.Matches)];
    }

    /// <summary>Paths listed one per line, relative to the working directory; null (with OFR2004) when the list cannot be read.</summary>
    private static List<string>? FromList(string list, CommandContext context)
    {
        var root = context.Repository.Path;
        var path = Path.GetFullPath(list, context.Host.WorkingDirectory);
        if (!File.Exists(path))
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR2004, $"The list {list} does not exist.", data: [KeyValuePair.Create<string, JsonNode?>("list", list)]);
            return null;
        }

        return [.. File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(l, context.Host.WorkingDirectory)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>A pattern relative to the working directory (<paramref name="prefix"/>, repository-relative) as a repository-relative one.</summary>
    internal static string Anchor(string prefix, string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return prefix is "" or "." ? normalized : prefix + "/" + normalized;
    }
}

public sealed record MoveApplyOptions(string Plan, string? Verify, string? OnFailure, bool Resume, bool Force);

/// <summary><c>offramp move apply --plan PATH</c>: perform a reviewed plan through a journal, then verify.</summary>
public sealed class MoveApplyCommand : ICommandHandler<MoveApplyOptions, MoveApplyResult>
{
    public string CommandPath => "move apply";

    public JsonTypeInfo<MoveApplyResult> ResultType => RefactoringJsonContext.Default.MoveApplyResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var plan = new Option<string>("--plan") { Description = "The plan written by move plan --out.", HelpName = "PATH", Required = true };
        var verify = new Option<string?>("--verify") { Description = "When to verify: end (default, from the plan), none, per-project, or batch:N (every N files).", HelpName = "WHEN" };
        verify.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string?>() is { } value && !MoveApplier.IsPolicy(value))
            {
                r.AddError($"--verify must be none, end, per-project, or batch:N, not '{value}'.");
            }
        });
        var onFailure = new Option<string?>("--on-failure") { Description = "rollback (default, from verify.onFailure) undoes the whole run; keep leaves it for inspection.", HelpName = "WHAT" };
        onFailure.AcceptOnlyFromAmong("rollback", "keep");
        var resume = new Option<bool>("--resume") { Description = "Finish this plan's interrupted run from its journal." };
        var force = new Option<bool>("--force") { Description = "Apply a plan made from a different workspace model." };
        var command = new Command("apply", "Apply a move plan: project edits, then pure renames (git mv), each journaled; verify, and roll back on failure.")
        {
            plan, verify, onFailure, resume, force,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new MoveApplyCommand(),
            new MoveApplyOptions(parse.GetValue(plan)!, parse.GetValue(verify), parse.GetValue(onFailure), parse.GetValue(resume), parse.GetValue(force)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp move apply --plan move-plan.json",
            "offramp move apply --plan move-plan.json --verify batch:50 --on-failure keep",
            "offramp move apply --plan move-plan.json --resume");
        return command;
    }

    public async Task<CommandOutcome<MoveApplyResult>> ExecuteAsync(MoveApplyOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var planPath = RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(options.Plan, context.Host.WorkingDirectory));
        var plan = ReadPlan(root, planPath, context);
        if (plan is null)
        {
            return CommandOutcome<MoveApplyResult>.Usage();
        }

        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<MoveApplyResult>.Environment();
        }

        var outcome = await MoveApplier.ApplyAsync(new MoveApplyRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config,
            Plan = plan,
            PlanPath = planPath,
            WorkspaceHash = CommandRunner.WorkspaceHash(context.WorkspacePath)!,
            VerifyPolicy = options.Verify ?? plan.Verify,
            OnFailure = options.OnFailure ?? config.Verify.OnFailure,
            Resume = options.Resume,
            Force = options.Force,
            Git = context.Host.GitService,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Now = context.Host.Time.GetUtcNow(),
        }, cancellationToken);
        return outcome.Status switch
        {
            MoveApplyStatus.Stale => new CommandOutcome<MoveApplyResult>(outcome.Result, OutcomeKind.EnvironmentFailure),
            MoveApplyStatus.NothingToResume => new CommandOutcome<MoveApplyResult>(outcome.Result, OutcomeKind.UsageFailure),
            MoveApplyStatus.Partial => new CommandOutcome<MoveApplyResult>(outcome.Result, OutcomeKind.Partial),
            _ => CommandOutcome<MoveApplyResult>.Completed(outcome.Result),
        };
    }

    public void Render(MoveApplyResult result, CommandContext context, HumanOutput output)
    {
        var what = MoveCommandSupport.Files(result.Moved.Count);
        var failed = result.Verifications.FirstOrDefault(v => !v.Passed);
        var headline = result.RolledBack ? $"Rolled back: verification failed{(failed is null ? "" : " (" + failed.Scope + ")")}; nothing moved."
            : !result.Applied ? "Nothing applied."
            : failed is not null ? $"Moved {what}, but verification failed ({failed.Scope}); the move was kept."
            : $"{(result.Resumed ? "Finished the interrupted move: " : "Moved ")}{what}{(result.Verifications.Count > 0 ? ", verified" : "")}.";
        output.Headline(headline, result.RolledBack || failed is not null || !result.Applied ? Theme.BlockingStyle : Theme.ReadyStyle);

        foreach (var skipped in result.Skipped)
        {
            output.MarkupLine($"[{Theme.DecisionStyle}]Skipped:[/] {Markup.Escape(skipped)} [dim]OFR2150[/]");
        }

        if (result.Applied)
        {
            output.MarkupLine($"[dim]Staged:[/] {result.Moved.Count} rename{(result.Moved.Count == 1 ? "" : "s")} (git mv). [dim]Unstaged:[/] {Markup.Escape(result.Edited.Count == 0 ? "no project files" : string.Join(", ", result.Edited))}.");
            output.MarkupLine("[dim]Suggested commits: the project files first, then the renames (a pure-move commit).[/]");
            output.MarkupLine($"[dim]Undo with[/] offramp move rollback --journal {Markup.Escape(result.Journal!)}");
        }
    }

    private static MovePlanDocument? ReadPlan(string root, string planPath, CommandContext context)
    {
        var path = RepoPaths.ToAbsolute(root, planPath);
        string? problem = null;
        MovePlanDocument? plan = null;
        if (!File.Exists(path))
        {
            problem = "does not exist";
        }
        else
        {
            try
            {
                plan = JsonSerializer.Deserialize(File.ReadAllText(path), RefactoringJsonContext.Default.MovePlanDocument);
                problem = plan is null || plan.Schema != MovePlanDocument.SchemaUri ? "is not a move plan" : null;
            }
            catch (JsonException exception)
            {
                problem = "is not a valid move plan: " + exception.Message;
            }
        }

        if (problem is null)
        {
            return plan;
        }

        context.Diagnostics.Report(DiagnosticCatalog.OFR2005, $"{planPath} {problem}.", data: [KeyValuePair.Create<string, JsonNode?>("plan", planPath)]);
        return null;
    }
}

/// <summary>Helpers the move commands share.</summary>
internal static class MoveCommandSupport
{
    public static string? Resolve(string value, WorkspaceModel model, CommandContext context)
    {
        var id = ProjectLookup.Resolve(value, model, context);
        if (id is null)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
        }

        return id;
    }

    /// <summary>A project's compiled files and the .resx files in its folder, repository-relative and sorted.</summary>
    public static IEnumerable<string> SourceFiles(string root, ProjectInfo source)
    {
        var folder = source.Id.Contains('/', StringComparison.Ordinal) ? source.Id[..source.Id.LastIndexOf('/')] : "";
        var directory = RepoPaths.ToAbsolute(root, folder.Length == 0 ? "." : folder);
        var resources = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.resx", SearchOption.AllDirectories)
                .Select(f => RepoPaths.ToRepositoryRelative(root, f))
                .Where(f => !f[folder.Length..].Contains("/bin/", StringComparison.OrdinalIgnoreCase) && !f[folder.Length..].Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            : [];
        return source.Compile.Concat(resources).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    public static string Files(int count) => string.Create(CultureInfo.InvariantCulture, $"{count} file{(count == 1 ? "" : "s")}");

    public static string Describe(ProjectEdit edit) => edit.Kind switch
    {
        ProjectEditKind.AddProjectReference => "add ProjectReference " + edit.Value,
        ProjectEditKind.AddPackageReference => $"add PackageReference {edit.Value} {edit.Version}".TrimEnd(),
        ProjectEditKind.RemovePackageReference => "remove PackageReference " + edit.Value,
        ProjectEditKind.AddInternalsVisibleTo => "add InternalsVisibleTo " + edit.Value,
        ProjectEditKind.KeepResourceName => $"keep the resource name {edit.Version} for {edit.Value}",
        ProjectEditKind.CreateProject => "create",
        ProjectEditKind.AddToSolution => "add project " + edit.Value,
        _ => edit.Kind + " " + edit.Value,
    };

    public static void WriteDiff(string preview, HumanOutput output)
    {
        foreach (var line in preview.TrimEnd('\n').Split('\n'))
        {
            var style = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ? "bold"
                : line.StartsWith('+') ? Theme.ReadyStyle : line.StartsWith('-') ? Theme.BlockingStyle : line.StartsWith("@@", StringComparison.Ordinal) ? "cyan" : "dim";
            output.MarkupLine($"[{style}]{Markup.Escape(line)}[/]");
        }
    }
}
