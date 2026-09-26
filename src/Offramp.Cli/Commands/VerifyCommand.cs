using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Workspace;
using Offramp.Workspace.Store;
using Offramp.Workspace.Verification;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record VerifyOptions(
    IReadOnlyList<string>? Projects, IReadOnlyList<string>? AffectedBy, bool All, VerifyMode? Mode, bool Baseline);

/// <summary><c>offramp verify</c>: the user's real build or verification command (docs/spec/commands/workspace.md#verify).</summary>
public sealed class VerifyCommand : ICommandHandler<VerifyOptions, VerifyResult>
{
    public string CommandPath => "verify";

    public JsonTypeInfo<VerifyResult> ResultType => WorkspaceJsonContext.Default.VerifyResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var projects = new Option<string[]>("--projects")
        {
            Description = "Verify these projects (paths or names), comma-separated or repeated.",
            HelpName = "P1,P2",
            AllowMultipleArgumentsPerToken = true,
        };
        var affectedBy = new Option<string[]>("--affected-by")
        {
            Description = "Verify the projects owning these changed files or folders, and their direct dependents.",
            HelpName = "PATHS",
            AllowMultipleArgumentsPerToken = true,
        };
        var all = new Option<bool>("--all") { Description = "Verify every project (the default unless verify.projects.include is set)." };
        var mode = new Option<string?>("--mode") { Description = "build, command, or none (default: verify.mode).", HelpName = "MODE" };
        mode.AcceptOnlyFromAmong("build", "command", "none");
        var baseline = new Option<bool>("--baseline") { Description = "Record the current errors as the baseline; later runs fail only on errors it does not list." };
        var command = new Command("verify", "Build the affected projects with the repository's own toolchain (or run verify.command) and report new errors, grouped by code.")
        {
            projects, affectedBy, all, mode, baseline,
        };
        command.Validators.Add(r =>
        {
            var given = new[] { r.GetResult(projects) is not null, r.GetResult(affectedBy) is not null, r.GetResult(all) is not null }.Count(g => g);
            if (given > 1)
            {
                r.AddError("Choose one of --projects, --affected-by, and --all.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new VerifyCommand(),
            new VerifyOptions(
                parse.GetResult(projects) is null ? null : Split(parse.GetValue(projects)),
                parse.GetResult(affectedBy) is null ? null : Split(parse.GetValue(affectedBy)),
                parse.GetValue(all),
                parse.GetValue(mode) is { } m ? Enum.Parse<VerifyMode>(m, ignoreCase: true) : null,
                parse.GetValue(baseline)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp verify",
            "offramp verify --projects Billing.Core,Billing.Web",
            "offramp verify --affected-by src/Billing/Invoice.cs",
            "offramp verify --baseline",
            "offramp verify --mode command --json");
        return command;
    }

    public async Task<CommandOutcome<VerifyResult>> ExecuteAsync(VerifyOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<VerifyResult>.Environment();
        }

        var mode = options.Mode ?? Enum.Parse<VerifyMode>(config.Verify.Mode, ignoreCase: true);
        if (mode == VerifyMode.Command && string.IsNullOrWhiteSpace(config.Verify.Command))
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0053, "verify.mode is command, but verify.command is not set; set it in offramp.yml or use --mode build.",
                data: [KeyValuePair.Create<string, JsonNode?>("key", "verify.command")]);
            return CommandOutcome<VerifyResult>.Usage();
        }

        List<string>? named = null;
        if (options.Projects is not null)
        {
            named = [];
            foreach (var value in options.Projects)
            {
                if (ProjectLookup.Resolve(value, model, context) is { } id)
                {
                    named.Add(id);
                }
                else
                {
                    context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                        data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                }
            }

            if (named.Count < options.Projects.Count)
            {
                return CommandOutcome<VerifyResult>.Usage();
            }
        }

        var changed = options.AffectedBy?
            .Select(p => RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(p, context.Host.WorkingDirectory)))
            .ToList();
        var selection = VerifySelector.Select(model, named, changed, config.Verify.Projects);
        var result = await VerifyRunner.RunAsync(new VerifyRequest
        {
            RepositoryRoot = root,
            Model = model,
            Config = config.Verify,
            Mode = mode,
            Projects = selection.Projects,
            Everything = selection.Everything,
            Scope = selection.Scope,
            TargetFramework = config.TargetFramework,
            RecordBaseline = options.Baseline,
            Processes = context.Host.Processes,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        }, cancellationToken);
        return result is null ? CommandOutcome<VerifyResult>.Environment() : CommandOutcome<VerifyResult>.Completed(result);
    }

    public void Render(VerifyResult result, CommandContext context, HumanOutput output)
    {
        var (text, style) = result.Status switch
        {
            VerifyStatus.Passed when result.Baseline is { Known: > 0 } b =>
                (string.Create(CultureInfo.InvariantCulture, $"Verification passed: no errors beyond the baseline's {b.Known}."), Theme.ReadyStyle),
            VerifyStatus.Passed => ("Verification passed.", Theme.ReadyStyle),
            VerifyStatus.Skipped => ("Verification skipped (verify.mode: none).", Theme.DimStyle),
            VerifyStatus.TimedOut => ("Verification timed out.", Theme.BlockingStyle),
            _ => (string.Create(CultureInfo.InvariantCulture,
                $"Verification failed: {result.ErrorCount} error{(result.ErrorCount == 1 ? "" : "s")}{(result.Baseline is null ? "" : " not in the baseline")}."), Theme.BlockingStyle),
        };
        output.Headline(text, style);
        output.MarkupLine($"[dim]Scope:[/] {Markup.Escape(result.Scope)}");
        if (result.Errors.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Code");
            table.AddColumn(new TableColumn("Count").RightAligned());
            table.AddColumn("First occurrence");
            foreach (var group in result.Errors)
            {
                var first = group.First;
                var where = first.File is null ? first.Project ?? ""
                    : first.Line is null ? first.File
                    : first.Column is null ? string.Create(CultureInfo.InvariantCulture, $"{first.File}({first.Line})")
                    : string.Create(CultureInfo.InvariantCulture, $"{first.File}({first.Line},{first.Column})");
                table.AddRow(
                    new Markup(Markup.Escape(group.Code.Length == 0 ? "-" : group.Code)),
                    new Markup(group.Count.ToString(CultureInfo.InvariantCulture)),
                    new Markup($"{Markup.Escape(where)}\n[dim]{Markup.Escape(first.Message)}[/]"));
            }

            output.Write(table);
        }

        foreach (var line in result.OutputTail)
        {
            output.MarkupLine($"[dim]{Markup.Escape(line)}[/]");
        }

        if (result.BaselineRecorded is not null)
        {
            output.MarkupLine($"[dim]Baseline recorded:[/] {Markup.Escape(result.BaselineRecorded)}");
        }
        else if (result.Baseline is { } baseline)
        {
            output.MarkupLine(string.Create(CultureInfo.InvariantCulture, $"[dim]Baseline:[/] {baseline.Known} known, {baseline.New} new, {baseline.Fixed} fixed."));
        }

        if (result.Invocations.FirstOrDefault(i => i.Binlog is not null) is { } logged)
        {
            output.MarkupLine($"[dim]Binary log:[/] {Markup.Escape(logged.Binlog!)}");
        }
    }

    private static List<string> Split(string[]? values) =>
        [.. (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
}
