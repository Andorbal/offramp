using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Offramp.Analysis;
using Offramp.Analysis.DeadCode;
using Offramp.Cli.Infrastructure;
using Offramp.Cli.Rendering;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Workspace.Store;
using Spectre.Console;

namespace Offramp.Cli.Commands;

public sealed record AuditDeadCodeOptions(string Scope, DeadCodeConfidence MinConfidence, bool IncludeTests, IReadOnlyList<string> Projects, string Format);

/// <summary><c>offramp audit dead-code</c> (docs/spec/commands/audit.md#audit-dead-code-ofr3400-3499).</summary>
public sealed class AuditDeadCodeCommand(string format) : ICommandHandler<AuditDeadCodeOptions, DeadCodeResult>, IRawOutput<DeadCodeResult>
{
    public string CommandPath => "audit dead-code";

    public JsonTypeInfo<DeadCodeResult> ResultType => AnalysisJsonContext.Default.DeadCodeResult;

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var scope = new Option<string>("--scope") { Description = "public (symbols visible outside their assembly) or all.", DefaultValueFactory = _ => "all" };
        scope.AcceptOnlyFromAmong("public", "all");
        var minimum = new Option<string>("--min-confidence") { Description = "Report candidates at this confidence or above: high, medium, or low.", DefaultValueFactory = _ => "low" };
        minimum.AcceptOnlyFromAmong("high", "medium", "low");
        var tests = new Option<bool>("--include-tests") { Description = "Report production code only test projects use (OFR3402) instead of counting those uses." };
        var project = new Option<string[]>("--project") { Description = "Only candidates declared in these projects (path or name). Repeatable; references are still read solution-wide.", HelpName = "PROJECT" };
        var format = new Option<string>("--format") { Description = "table (the terminal view), json (the result alone), or markdown.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "json", "markdown");
        var command = new Command("dead-code", "Types and members nothing in the solution references, with a confidence level and its evidence, and the lines that would go away.")
        {
            scope, minimum, tests, project, format,
        };
        command.SetAction((parse, ct) =>
        {
            var chosen = parse.GetValue(format) ?? "table";
            var options = new AuditDeadCodeOptions(
                parse.GetValue(scope) ?? "all",
                Enum.Parse<DeadCodeConfidence>(parse.GetValue(minimum) ?? "low", ignoreCase: true),
                parse.GetValue(tests),
                parse.GetValue(project) ?? [],
                chosen);
            return CommandRunner.RunAsync(new AuditDeadCodeCommand(chosen), options, globals.Bind(parse), host, ct);
        });
        HelpExamples.Add(command,
            "offramp audit dead-code",
            "offramp audit dead-code --min-confidence high --scope public",
            "offramp audit dead-code --include-tests --format markdown > dead-code.md");
        return command;
    }

    public async Task<CommandOutcome<DeadCodeResult>> ExecuteAsync(AuditDeadCodeOptions options, CommandContext context, CancellationToken cancellationToken)
    {
        var root = context.Repository.Path;
        var config = context.Config.Config;
        var model = WorkspaceStore.LoadForCommand(context.WorkspacePath, root, config, context.Diagnostics, context.Settings.FailOnStale);
        if (model is null)
        {
            return CommandOutcome<DeadCodeResult>.Environment();
        }

        var projects = new List<string>();
        foreach (var value in options.Projects)
        {
            if (ProjectLookup.Resolve(value, model, context) is not { } id)
            {
                context.Diagnostics.Report(DiagnosticCatalog.OFR0021, $"'{value}' is not a project in the workspace model.",
                    data: [KeyValuePair.Create<string, JsonNode?>("project", value)]);
                return CommandOutcome<DeadCodeResult>.Usage();
            }

            projects.Add(id);
        }

        var result = DeadCodeAnalyzer.Analyze(new DeadCodeRequest
        {
            RepositoryRoot = root,
            Model = model,
            Scope = options.Scope,
            MinConfidence = options.MinConfidence,
            IncludeTests = options.IncludeTests,
            Projects = projects,
            ExternalConsumers = config.DeadCode.ExternalConsumers,
            Diagnostics = context.Diagnostics,
            Progress = context.Progress,
        });
        if (LlmGate.For(context, LlmGate.Classifying) is { } llm)
        {
            result = await ClassifyAsync(llm, result, cancellationToken);
        }

        return CommandOutcome<DeadCodeResult>.Completed(result);
    }

    /// <summary>At most this many low-confidence candidates go to the model, in output order, in one request.</summary>
    public const int MaxClassified = 50;

    /// <summary>
    /// <c>llm.uses: classifying</c>: asks whether each low-confidence candidate's name looks used
    /// by convention (reflection, DI scanning, serializers, frameworks). The answer is added as
    /// evidence and marks the candidate <c>source: llm</c>; it never changes the confidence.
    /// </summary>
    private static async Task<DeadCodeResult> ClassifyAsync(LlmGate llm, DeadCodeResult result, CancellationToken cancellationToken)
    {
        var low = result.Projects.SelectMany(p => p.Candidates).Where(c => c.Confidence == DeadCodeConfidence.Low).Take(MaxClassified).ToList();
        if (low.Count == 0)
        {
            return result;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["symbol"] = new JsonObject { ["type"] = "string" },
                            ["convention"] = new JsonObject { ["type"] = "boolean" },
                            ["reason"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = new JsonArray("symbol", "convention", "reason"),
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JsonArray("items"),
            ["additionalProperties"] = false,
        };
        var prompt = "Nothing in a .NET solution references these symbols by name in code. For each, is its name likely used by convention "
            + "(reflection, dependency-injection scanning, serializers, ASP.NET or test framework conventions, configuration binding)? "
            + "Give a short reason.\n\n" + string.Join("\n", low.Select(c => $"- {c.Symbol} ({c.Kind}, {c.Accessibility})"));
        var answers = await llm.AskAsync(new Offramp.Llm.LlmRequest { Use = LlmGate.Classifying, Prompt = prompt, Schema = schema, MaxTokens = 4000 },
            answer => answer["items"]?.AsArray()
                .Select(i => (Symbol: i?["symbol"]?.GetValue<string>(), Convention: i?["convention"]?.GetValue<bool>(), Reason: i?["reason"]?.GetValue<string>()))
                .Where(i => i.Symbol is not null && i.Convention is not null)
                .GroupBy(i => i.Symbol!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            $"{low.Count} low-confidence dead-code candidate{(low.Count == 1 ? "" : "s")}", cancellationToken);
        if (answers is null)
        {
            return result;
        }

        var asked = low.ToHashSet();
        DeadCodeCandidate Classify(DeadCodeCandidate candidate)
        {
            if (!asked.Contains(candidate) || !answers.TryGetValue(candidate.Symbol, out var answer))
            {
                return candidate;
            }

            var reason = string.IsNullOrWhiteSpace(answer.Reason) ? "" : ": " + answer.Reason!.Trim();
            var evidence = answer.Convention == true ? "the model judges the name likely used by convention" + reason : "the model sees no convention that would use the name" + reason;
            return candidate with { Evidence = [.. candidate.Evidence, evidence], Source = LlmGate.Source };
        }

        return result with { Projects = [.. result.Projects.Select(p => p with { Candidates = [.. p.Candidates.Select(Classify)] })] };
    }

    public string? RawOutput(DeadCodeResult result, CommandContext context) => format switch
    {
        "json" => OfframpJson.Serialize(result, AnalysisJsonContext.Default.DeadCodeResult),
        "markdown" => Markdown(result),
        _ => null,
    };

    public void Render(DeadCodeResult result, CommandContext context, HumanOutput output)
    {
        var s = result.Summary;
        output.Headline(Invariant($"{s.Candidates} dead-code candidates ({s.High} high, {s.Medium} medium, {s.Low} low confidence); {s.RemovableLoc} lines removable at high confidence."),
            s.High > 0 ? Theme.DecisionStyle : Theme.ReadyStyle);
        foreach (var project in result.Projects)
        {
            output.MarkupLine($"[bold]{Markup.Escape(project.Project)}[/] [dim]({Invariant($"{project.Loc.High}")} lines at high confidence)[/]");
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Symbol");
            table.AddColumn("Kind");
            table.AddColumn("Confidence");
            table.AddColumn(new TableColumn("Lines").RightAligned());
            table.AddColumn("Where");
            foreach (var candidate in project.Candidates)
            {
                table.AddRow(
                    Markup.Escape(candidate.Symbol),
                    candidate.Kind,
                    $"[{Color(candidate.Confidence)}]{Wire(candidate.Confidence)}[/]",
                    candidate.Loc.ToString(CultureInfo.InvariantCulture),
                    Markup.Escape($"{candidate.File}:{candidate.Line}"));
            }

            if (project.Candidates.Count > 0)
            {
                output.Write(table);
            }

            foreach (var symbol in project.TestOnly)
            {
                output.MarkupLine($"  [dim]test-only:[/] {Markup.Escape(symbol.Symbol)} [dim]({Markup.Escape(string.Join(", ", symbol.Tests))})[/]");
            }
        }
    }

    /// <summary>The candidates as Markdown, one table per project, with the evidence.</summary>
    public static string Markdown(DeadCodeResult result)
    {
        var s = result.Summary;
        var b = new StringBuilder();
        b.Append("# Dead code\n\n");
        b.Append(Invariant($"{s.Candidates} candidates: {s.High} high, {s.Medium} medium, {s.Low} low confidence. **{s.RemovableLoc} lines removable at high confidence.**\n\n"));
        foreach (var project in result.Projects)
        {
            b.Append("## ").Append(project.Project).Append("\n\n");
            if (project.Candidates.Count > 0)
            {
                b.Append("| Symbol | Kind | Confidence | Lines | Where | Evidence |\n|---|---|---|---:|---|---|\n");
                foreach (var c in project.Candidates)
                {
                    b.Append(Invariant($"| `{c.Symbol}` | {c.Kind} | {Wire(c.Confidence)} | {c.Loc} | `{c.File}:{c.Line}` | {string.Join("; ", c.Evidence).Replace("|", "\\|", StringComparison.Ordinal)} |\n"));
                }

                b.Append('\n');
            }

            foreach (var t in project.TestOnly)
            {
                b.Append(Invariant($"- Used only by tests: `{t.Symbol}` (`{t.File}:{t.Line}`, {t.Loc} lines) from {string.Join(", ", t.Tests)}\n"));
            }

            if (project.TestOnly.Count > 0)
            {
                b.Append('\n');
            }
        }

        return b.ToString().TrimEnd('\n') + "\n";
    }

    private static string Wire(DeadCodeConfidence confidence) => confidence switch
    {
        DeadCodeConfidence.High => "high",
        DeadCodeConfidence.Medium => "medium",
        _ => "low",
    };

    private static string Color(DeadCodeConfidence confidence) => confidence switch
    {
        DeadCodeConfidence.High => Theme.ReadyStyle,
        DeadCodeConfidence.Medium => Theme.DecisionStyle,
        _ => Theme.DimStyle,
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
