using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Configuration;
using Offramp.Workspace;
using Offramp.Workspace.Scanning;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record ScanOptions(string? Binlog, string? Complog, bool IfStale, bool NoBuild, bool Msbuild, string? MsbuildPath);

/// <summary><c>offramp scan</c>: builds the workspace model (docs/spec/commands/workspace.md#scan).</summary>
public sealed class ScanCommand : ICommandHandler<ScanOptions, ScanResult>, INextStep<ScanResult>
{
    public string CommandPath => "scan";

    public JsonTypeInfo<ScanResult> ResultType => WorkspaceJsonContext.Default.ScanResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var binlog = new Option<string?>("--binlog")
        {
            Description = "Read an existing MSBuild binary log instead of building (for example one captured on Windows).",
            HelpName = "PATH",
        };
        var complog = new Option<string?>("--complog")
        {
            Description = "Use a compiler log (from `complog create`). Alone it gives a reduced model; with --binlog, the full one.",
            HelpName = "PATH",
        };
        var ifStale = new Option<bool>("--if-stale") { Description = "Rescan only when the model is stale." };
        var noBuild = new Option<bool>("--no-build") { Description = "Reuse the previous scan's binary log instead of building." };
        var msbuild = new Option<bool>("--msbuild")
        {
            Description = "Build with MSBuild.exe from Visual Studio or Build Tools instead of `dotnet build`, for projects only it builds (sgen, COM references). Default: scan.builder.",
        };
        var msbuildPath = new Option<string?>("--msbuild-path")
        {
            Description = "MSBuild.exe, or the Visual Studio or Build Tools folder holding it; implies --msbuild. Default: scan.msbuildPath, else the Developer Command Prompt's installation, else vswhere.",
            HelpName = "PATH",
        };
        var command = new Command("scan", "Build the workspace model (.offramp/workspace.json) from a build of the solution or from logs, and record a ledger snapshot.")
        {
            binlog, complog, ifStale, noBuild, msbuild, msbuildPath,
        };
        command.Validators.Add(result =>
        {
            var logs = result.GetValue(binlog) is not null || result.GetValue(complog) is not null;
            if (result.GetValue(noBuild) && logs)
            {
                result.AddError("--no-build cannot be combined with --binlog or --complog.");
            }

            if ((result.GetValue(msbuild) || result.GetValue(msbuildPath) is not null) && (logs || result.GetValue(noBuild)))
            {
                result.AddError("--msbuild and --msbuild-path choose how scan builds, so they cannot be combined with --binlog, --complog, or --no-build.");
            }
        });
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new ScanCommand(),
            new ScanOptions(parse.GetValue(binlog), parse.GetValue(complog), parse.GetValue(ifStale), parse.GetValue(noBuild),
                parse.GetValue(msbuild), parse.GetValue(msbuildPath)),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp scan",
            "offramp scan --solution src/Monolith.sln",
            "offramp scan --msbuild",
            "offramp scan --msbuild-path \"C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\"",
            "offramp scan --binlog windows.binlog --complog windows.complog",
            "offramp scan --if-stale --json");
        return command;
    }

    public async Task<CommandOutcome<ScanResult>> ExecuteAsync(ScanOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var cwd = context.Host.WorkingDirectory;
        var outcome = await ScanRunner.RunAsync(new ScanRequest
        {
            RepositoryRoot = context.Repository.Path,
            Config = WithBuilder(context.Config.Config, options, cwd),
            WorkspacePath = context.WorkspacePath,
            BinlogPath = options.Binlog is null ? null : Path.GetFullPath(options.Binlog, cwd),
            ComplogPath = options.Complog is null ? null : Path.GetFullPath(options.Complog, cwd),
            IfStale = options.IfStale,
            NoBuild = options.NoBuild,
            Processes = context.Host.Processes,
            Environment = context.Host.Environment,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
            Time = context.Host.Time,
        }, cancellationToken);

        return outcome.Failure switch
        {
            ScanFailure.Usage => CommandOutcome<ScanResult>.Usage(),
            ScanFailure.Environment => CommandOutcome<ScanResult>.Environment(),
            _ => CommandOutcome<ScanResult>.Completed(outcome.Result!),
        };
    }

    /// <summary><c>--msbuild</c> and <c>--msbuild-path</c> over <c>scan:</c>; a path on the command line is relative to the working directory.</summary>
    private static OfframpConfig WithBuilder(OfframpConfig config, ScanOptions options, string cwd)
    {
        if (!options.Msbuild && options.MsbuildPath is null)
        {
            return config;
        }

        return config with
        {
            Scan = config.Scan with
            {
                Builder = ScanConfig.Msbuild,
                MsbuildPath = options.MsbuildPath is null ? config.Scan.MsbuildPath : Path.GetFullPath(options.MsbuildPath, cwd),
            },
        };
    }

    public void Render(ScanResult result, CommandContext context, HumanOutput output)
    {
        if (result.UpToDate)
        {
            output.Headline($"The workspace model is up to date ({result.Projects} projects); nothing was rescanned.", Theme.ReadyStyle);
            return;
        }

        var c = result.ByFrameworkClass;
        var style = result.BuildSucceeded == false ? Theme.DecisionStyle : Theme.ReadyStyle;
        output.Headline(
            $"Scanned {result.Projects} projects ({result.Loc:N0} lines): {c.GetValueOrDefault("framework")} framework, {c.GetValueOrDefault("standard")} standard, {c.GetValueOrDefault("modern")} modern, {c.GetValueOrDefault("dual")} dual.",
            style);
        output.Line();

        var classes = new Table().Border(TableBorder.None).HideHeaders();
        classes.AddColumn("Class");
        classes.AddColumn(new TableColumn("Projects").RightAligned());
        foreach (var (name, color) in new[] { ("framework", Theme.FrameworkOrange), ("standard", Theme.StandardBlue), ("modern", Theme.ModernGreen), ("dual", Theme.DualTeal) })
        {
            classes.AddRow(new Markup($"[{color.ToMarkup()}]■[/] {name}"), new Markup(c.GetValueOrDefault(name).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        output.Write(classes);
        var kinds = string.Join(", ", result.ByKind.Where(k => k.Value > 0).Select(k => $"{k.Value} {k.Key}"));
        output.MarkupLine($"[dim]Kinds:[/] {Markup.Escape(kinds)}");

        if (result.Cycles.Count > 0)
        {
            output.Line();
            output.MarkupLine($"[{Theme.DecisionStyle}]Cycles:[/]");
            foreach (var cycle in result.Cycles)
            {
                output.MarkupLine("  " + Markup.Escape(string.Join(" ↔ ", cycle)));
            }
        }

        if (result.WindowsOnlyBuildSteps.Count > 0)
        {
            output.Line();
            output.MarkupLine($"[{Theme.DecisionStyle}]Windows-only build steps:[/]");
            foreach (var project in result.WindowsOnlyBuildSteps)
            {
                output.MarkupLine($"  {Markup.Escape(project.Project)}: {Markup.Escape(string.Join(", ", project.Steps))}");
            }
        }

        output.Line();
        output.MarkupLine($"[dim]Model:[/] {Markup.Escape(result.Model)}  [dim]Ledger:[/] {Markup.Escape(result.LedgerSnapshot ?? "-")}");
    }

    public string? NextStep(ScanResult result, CommandContext context) =>
        result.WindowsOnlyBuildSteps.Count > 0 ? "offramp doctor --fix" : null;
}
