using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Output;
using Offramp.Workspace;
using Offramp.Workspace.Doctor;
using Offramp.Workspace.Init;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record InitOptions(bool Defaults, bool Force);

/// <summary>Asks the <c>init</c> interview questions; the Spectre prompter in production, a fake in tests.</summary>
public interface IInitPrompter
{
    InitValues Ask(InitDetection detection);

    /// <summary>Asks whether to add the compile-only block to Directory.Build.props (diff shown).</summary>
    bool OfferCompileOnlyBlock(CompileOnlyFix plan);
}

/// <summary><c>offramp init</c>: writes <c>offramp.yml</c> (<c>docs/spec/03-configuration.md#init</c>).</summary>
public sealed class InitCommand : ICommandHandler<InitOptions, InitResult>, INextStep<InitResult>, IInteractiveCommand
{
    public string CommandPath => "init";

    public JsonTypeInfo<InitResult> ResultType => WorkspaceJsonContext.Default.InitResult;

    /// <summary>Lets <c>init --force</c> replace a broken configuration file.</summary>
    public bool RunsWithInvalidConfig => true;

    /// <summary>The existing file's problems matter only when init leaves that file in place.</summary>
    public bool IncludesConfigDiagnostics => false;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var defaults = new Option<bool>("--defaults")
        {
            Description = "Write the detected configuration without asking.",
        };
        var force = new Option<bool>("--force")
        {
            Description = "Replace an existing offramp.yml.",
        };
        var command = new Command("init", "Write offramp.yml with detected values (interviews on a terminal) and add Offramp's state to .gitignore.")
        {
            defaults,
            force,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new InitCommand(),
            new InitOptions(parse.GetValue(defaults), parse.GetValue(force)),
            globals.Bind(parse),
            host,
            ct));
        HelpExamples.Add(command,
            "offramp init",
            "offramp init --defaults",
            "offramp init --defaults --target 9 --solution src/Monolith.sln",
            "offramp init --dry-run");
        return command;
    }

    public Task<CommandOutcome<InitResult>> ExecuteAsync(InitOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var detection = InitPlanner.Detect(root, context.Config.Config, context.Diagnostics);
        var interactive = context.Interactive && !options.Defaults && !context.Settings.Yes;
        var values = detection.Values;
        IInitPrompter? prompter = null;
        if (interactive)
        {
            var console = ConsoleFactory.Create(context.Host, context.Host.Out, isTerminal: true);
            prompter = context.Host.InitPrompter?.Invoke(console) ?? new SpectreInitPrompter(console);
            values = prompter.Ask(detection);
        }

        var result = InitPlanner.Apply(
            root, detection, values, context.Config.Config.Paths.State,
            dryRun: context.Settings.DryRun, force: options.Force, interactive: interactive, context.Diagnostics);
        if (prompter is not null && result.Written && HasWindowsOnlySteps(context.WorkspacePath))
        {
            var plan = DoctorRunner.PlanFix(root);
            if (!plan.AlreadyPresent)
            {
                result = result with
                {
                    CompileOnlyFix = prompter.OfferCompileOnlyBlock(plan) ? DoctorRunner.ApplyFix(root) : plan,
                };
            }
        }
        if (!result.Replaced)
        {
            context.Diagnostics.AddRange(context.Config.Diagnostics.Where(d => d.Code != "OFR0016"));
        }

        return Task.FromResult(CommandOutcome<InitResult>.Completed(result));
    }

    public void Render(InitResult result, CommandContext context, HumanOutput output)
    {
        if (result.DryRun)
        {
            output.Headline($"Dry run: would write {result.ConfigFile}.", Theme.DecisionStyle);
            output.Line();
            DiffRenderer.Render(output, UnifiedDiff.ForNewFile(result.ConfigFile, result.Content));
            RenderGitignore(result, output, "would add");
            return;
        }

        if (!result.Written)
        {
            output.Headline($"{result.ConfigFile} already exists; nothing was written.", Theme.DecisionStyle);
            return;
        }

        output.Headline(result.Replaced ? $"Replaced {result.ConfigFile}." : $"Wrote {result.ConfigFile}.", Theme.ReadyStyle);
        output.Line();
        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.AddRow("target", $"net{result.Values.Target}.0");
        table.AddRow("solution", Markup.Escape(result.Values.Solution ?? "(none)"));
        table.AddRow("verify.mode", Markup.Escape(result.Values.VerifyMode));
        table.AddRow("deps.cpm.file", Markup.Escape(result.Values.CpmFile));
        table.AddRow("deps.pins", result.Values.Pins.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        output.Write(table);
        RenderGitignore(result, output, "added");
        if (result.CompileOnlyFix is { Applied: true } fix)
        {
            output.MarkupLine($"[dim]{Markup.Escape(fix.File)}:[/] added the compile-only block for Windows-only build steps.");
        }
    }

    private static bool HasWindowsOnlySteps(string workspacePath)
    {
        if (!File.Exists(workspacePath))
        {
            return false;
        }

        try
        {
            return WorkspaceStore.Read(workspacePath).Projects.Any(p => p.WindowsOnlyBuildSteps.Count > 0);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public string? NextStep(InitResult result, CommandContext context) =>
        result.Written ? "offramp doctor" : result.DryRun ? "offramp init" : null;

    private static void RenderGitignore(InitResult result, HumanOutput output, string verb)
    {
        if (result.Gitignore.Added.Count == 0)
        {
            return;
        }

        output.Line();
        output.MarkupLine($"[dim]{Markup.Escape(result.Gitignore.File)} {verb}:[/] {Markup.Escape(string.Join(", ", result.Gitignore.Added))}");
    }
}
