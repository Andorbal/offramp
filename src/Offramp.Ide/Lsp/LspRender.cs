using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;

namespace Offramp.Ide.Lsp;

/// <summary>
/// File reports as Language Server Protocol values (docs/spec/commands/ide.md#offramp-ide-serve):
/// diagnostics, lenses, and code actions. Pure functions of the report, so the editor shows
/// exactly what <c>ide check</c> reports.
/// </summary>
public static class LspRender
{
    public const string Source = "offramp";
    public const string MoveCommand = "offramp.move";
    public const string ScanCommand = "offramp.scan";
    public const string RefreshCommand = "offramp.refresh";

    public static readonly IReadOnlyList<string> Commands = [MoveCommand, ScanCommand, RefreshCommand];

    /// <summary>LSP severities, one step down from Offramp's: nothing here breaks the build.</summary>
    public static int Severity(Severity severity) => severity == Core.Diagnostics.Severity.Error ? 2 : 3;

    public static JsonArray Diagnostics(IdeFileReport report)
    {
        var result = new JsonArray();
        foreach (var finding in report.Findings)
        {
            result.Add(Diagnostic(
                Range(finding.Line, finding.Column, finding.EndLine, finding.EndColumn),
                Severity(finding.Severity), finding.Code,
                finding.Message + "\n" + finding.Recommendation,
                null));
        }

        foreach (var type in IdeCheck.NewMovableTypes(report))
        {
            var target = report.Moves.First(m => m.Movable);
            result.Add(Diagnostic(
                Range(type.Line, type.Column, type.EndLine, type.EndColumn),
                Severity(DiagnosticCatalog.OFR6001.DefaultSeverity), DiagnosticCatalog.OFR6001.Code,
                IdeCheck.Message(report, type),
                MoveArguments(report.File, target.To)));
        }

        return result;
    }

    public static JsonArray CodeLenses(IdeFileReport report)
    {
        var lenses = new JsonArray();
        if (report.LensType() is not { } type)
        {
            return lenses;
        }

        foreach (var move in report.Moves.Where(m => m.Movable))
        {
            lenses.Add(new JsonObject
            {
                ["range"] = Range(type.Line, type.Column, type.EndLine, type.EndColumn),
                ["command"] = Command(LensTitle(report, move.To), report.File, move.To),
            });
        }

        return lenses;
    }

    /// <summary>
    /// Quick fixes for the OFR6001 diagnostics the client sends back, and a <c>refactor.move</c>
    /// action when the range touches a movable file's type declaration.
    /// </summary>
    public static JsonArray CodeActions(IdeFileReport report, int startLine, int endLine, JsonArray? diagnostics, IReadOnlyList<string>? only)
    {
        var actions = new JsonArray();
        var offered = new HashSet<string>(StringComparer.Ordinal);
        bool Wants(string kind) => only is null || only.Count == 0 || only.Any(o => kind == o || kind.StartsWith(o + ".", StringComparison.Ordinal));

        if (Wants("quickfix"))
        {
            foreach (var diagnostic in diagnostics?.OfType<JsonObject>() ?? [])
            {
                if ((string?)diagnostic["source"] != Source || (string?)diagnostic["code"] != DiagnosticCatalog.OFR6001.Code
                    || diagnostic["data"] is not JsonObject data || (string?)data["file"] != report.File || (string?)data["to"] is not { } to
                    || !report.Moves.Any(m => m.Movable && m.To == to) || !offered.Add(to))
                {
                    continue;
                }

                actions.Add(new JsonObject
                {
                    ["title"] = ActionTitle(report, to),
                    ["kind"] = "quickfix",
                    ["diagnostics"] = new JsonArray(diagnostic.DeepClone()),
                    ["isPreferred"] = true,
                    ["command"] = Command(ActionTitle(report, to), report.File, to),
                });
            }
        }

        if (Wants("refactor.move") && report.Types.Any(t => t.Line <= endLine + 1 && t.EndLine >= startLine + 1))
        {
            foreach (var move in report.Moves.Where(m => m.Movable && !offered.Contains(m.To)))
            {
                actions.Add(new JsonObject
                {
                    ["title"] = ActionTitle(report, move.To),
                    ["kind"] = "refactor.move",
                    ["command"] = Command(ActionTitle(report, move.To), report.File, move.To),
                });
            }
        }

        return actions;
    }

    public static JsonObject MoveArguments(string file, string to) => new() { ["file"] = file, ["to"] = to };

    /// <summary>A 1-based inclusive-start, exclusive-end position pair as an LSP range (0-based).</summary>
    public static JsonObject Range(int line, int column, int endLine, int endColumn) => new()
    {
        ["start"] = new JsonObject { ["line"] = line - 1, ["character"] = column - 1 },
        ["end"] = new JsonObject { ["line"] = endLine - 1, ["character"] = endColumn - 1 },
    };

    private static JsonObject Diagnostic(JsonObject range, int severity, string code, string message, JsonObject? data)
    {
        var diagnostic = new JsonObject
        {
            ["range"] = range,
            ["severity"] = severity,
            ["code"] = code,
            ["codeDescription"] = new JsonObject { ["href"] = DiagnosticDescriptor.HelpBaseUri + code },
            ["source"] = Source,
            ["message"] = message,
        };
        if (data is not null)
        {
            diagnostic["data"] = data;
        }

        return diagnostic;
    }

    private static JsonObject Command(string title, string file, string to) => new()
    {
        ["title"] = title,
        ["command"] = MoveCommand,
        ["arguments"] = new JsonArray(MoveArguments(file, to)),
    };

    private static string LensTitle(IdeFileReport report, string to) =>
        report.Types.Count > 1
            ? $"Offramp: move {Path.GetFileName(report.File)} ({report.Types.Count} types) to {IdeCheck.ProjectName(to)}"
            : $"Offramp: move to {IdeCheck.ProjectName(to)}";

    private static string ActionTitle(IdeFileReport report, string to) =>
        $"Move {Path.GetFileName(report.File)} to {IdeCheck.ProjectName(to)} (Offramp)";
}
