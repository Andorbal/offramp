using System.CommandLine;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Slicing;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record SliceOptions(IReadOnlyList<string> For, bool IncludeDependents, bool IncludeTests, string Format);

/// <summary><c>offramp slice</c>: a solution filter for a project closure (docs/spec/commands/workspace.md#slice).</summary>
public sealed class SliceCommand : ICommandHandler<SliceOptions, SliceResult>
{
    public string CommandPath => "slice";

    public JsonTypeInfo<SliceResult> ResultType => WorkspaceJsonContext.Default.SliceResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var forOption = new Option<string[]>("--for")
        {
            Description = "Projects to slice around: repository-relative paths or project names, comma-separated or repeated.",
            HelpName = "PROJECT[,PROJECT...]",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var dependents = new Option<bool>("--include-dependents") { Description = "Also include every project that depends on them." };
        var tests = new Option<bool>("--include-tests") { Description = "Also include test projects that reference anything in the slice." };
        var format = new Option<string>("--format")
        {
            Description = "slnf: a solution filter; slngen: a SlnGen command line.",
            DefaultValueFactory = _ => "slnf",
        };
        format.AcceptOnlyFromAmong("slnf", "slngen");
        var command = new Command("slice", "Write a solution filter (.slnf) for a project closure so a slice of a large repository builds quickly.")
        {
            forOption, dependents, tests, format,
        };
        command.SetAction((parse, ct) => CommandRunner.RunAsync(
            new SliceCommand(),
            new SliceOptions(
                [.. (parse.GetValue(forOption) ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))],
                parse.GetValue(dependents), parse.GetValue(tests), parse.GetValue(format) ?? "slnf"),
            globals.Bind(parse), host, ct));
        HelpExamples.Add(command,
            "offramp slice --for src/Foo/Foo.csproj --out foo.slnf",
            "offramp slice --for Foo,Bar --include-tests --out slices/foo.slnf",
            "offramp slice --for Foo --include-dependents --format slngen");
        return command;
    }

    public async Task<CommandOutcome<SliceResult>> ExecuteAsync(SliceOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, context.Config.Config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<SliceResult>.Environment();
        }

        var requested = new List<string>();
        foreach (var value in options.For)
        {
            var project = Resolve(value, model, context);
            if (project is null)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021,
                    $"'{value}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                continue;
            }

            requested.Add(project);
        }

        if (requested.Count != options.For.Count)
        {
            return CommandOutcome<SliceResult>.Usage();
        }

        var solution = await UnderlyingSolutionAsync(model, root, cancellationToken);
        if (solution is null)
        {
            context.Diagnostics.Report(DiagnosticCatalog.OFR0022,
                "The workspace model records no solution to filter; rescan with --solution.");
            return CommandOutcome<SliceResult>.Environment();
        }

        var output = context.Settings.Out is null
            ? null
            : RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(context.Settings.Out, context.Host.WorkingDirectory));
        var result = SliceBuilder.Build(model, [.. requested.Distinct(StringComparer.Ordinal)], options.IncludeDependents, options.IncludeTests, options.Format, solution, output);
        if (output is not null)
        {
            var path = RepoPaths.ToAbsolute(root, output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, result.Content, new System.Text.UTF8Encoding(false), cancellationToken);
        }

        return CommandOutcome<SliceResult>.Completed(result);
    }

    public void Render(SliceResult result, CommandContext context, HumanOutput output)
    {
        var counts = result.Counts;
        var headline = result.Output is null
            ? $"Slice of {counts.Total} projects ({counts.Requested} requested, {counts.Dependencies} dependencies, {counts.Dependents} dependents, {counts.Tests} tests)."
            : $"Wrote {result.Output}: {counts.Total} projects ({counts.Requested} requested, {counts.Dependencies} dependencies, {counts.Dependents} dependents, {counts.Tests} tests).";
        output.Headline(headline, Theme.ReadyStyle);
        output.Line();
        if (result.Output is null)
        {
            output.Console.Write(new Text(result.Content));
            return;
        }

        foreach (var project in result.Projects)
        {
            output.MarkupLine("  " + Markup.Escape(project));
        }
    }

    /// <summary>
    /// The primary output with --out is the slice file itself, which ExecuteAsync writes;
    /// the envelope is written only with --json.
    /// </summary>
    public bool WritesOwnOutput => true;

    private static string? Resolve(string value, WorkspaceModel model, CommandContext context)
    {
        var normalized = RepoPaths.Normalize(value);
        var byId = model.Projects.FirstOrDefault(p => string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
        {
            return byId.Id;
        }

        var absolute = Path.GetFullPath(value, context.Host.WorkingDirectory);
        var relative = RepoPaths.ToRepositoryRelative(context.Repository.Path, absolute);
        var byPath = model.Projects.FirstOrDefault(p => string.Equals(p.Id, relative, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            return byPath.Id;
        }

        var byName = model.Projects.Where(p => string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    private static async Task<string?> UnderlyingSolutionAsync(WorkspaceModel model, string root, CancellationToken cancellationToken)
    {
        if (model.Solution is null)
        {
            return null;
        }

        if (!model.Solution.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return model.Solution;
        }

        var filter = await SolutionReader.ReadAsync(RepoPaths.ToAbsolute(root, model.Solution), cancellationToken);
        return RepoPaths.ToRepositoryRelative(root, filter.SolutionFile);
    }
}
