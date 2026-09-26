using System.Text.Json.Nodes;
using Offramp.Analysis.Rules;
using Offramp.Core.Diagnostics;

namespace Offramp.Analysis.Audits;

/// <summary>The audit rule packs shipped as <c>rules/audit-*.yml</c>.</summary>
public static class AuditRules
{
    private static readonly Lazy<IReadOnlyList<AuditRule>> All = new(Load);

    public static IReadOnlyList<AuditRule> For(AuditKind audit) => [.. All.Value.Where(r => r.Audit == audit)];

    public static IReadOnlyList<AuditRule> Every => All.Value;

    private static List<AuditRule> Load()
    {
        var rules = new List<AuditRule>();
        foreach (var (audit, file) in new[]
        {
            (AuditKind.Api, "audit-api.yml"), (AuditKind.Behavior, "audit-behavior.yml"),
            (AuditKind.Serialization, "audit-serialization.yml"), (AuditKind.Native, "audit-native.yml"),
        })
        {
            foreach (var (id, node) in RuleFiles.Load(file)["rules"]!.AsObject())
            {
                rules.Add(new AuditRule
                {
                    Id = id,
                    Audit = audit,
                    Pack = Text(node, "pack"),
                    Severity = ParseSeverity(Text(node, "severity")),
                    SeverityBelowTarget = node!["severityBelowTarget"] is JsonObject below
                        ? (below["target"]!.GetValue<int>(), ParseSeverity(below["severity"]!.GetValue<string>()))
                        : null,
                    Title = Text(node, "title"),
                    Category = Text(node, "category"),
                    Recommendation = Text(node, "recommendation"),
                    Symbols = List(node, "symbols"),
                    BaseTypes = List(node, "baseTypes"),
                    Attributes = List(node, "attributes"),
                    Matcher = node["matcher"]?.GetValue<string>(),
                });
            }
        }

        return [.. rules.OrderBy(r => r.Id, StringComparer.Ordinal)];
    }

    private static string Text(JsonNode? node, string key) =>
        node?[key]?.GetValue<string>() ?? throw new InvalidDataException($"An audit rule lacks '{key}'.");

    private static List<string> List(JsonNode? node, string key) =>
        node?[key] is JsonArray array ? [.. array.Select(v => v!.GetValue<string>())] : [];

    private static Severity ParseSeverity(string value) => value switch
    {
        "error" => Severity.Error,
        "warning" => Severity.Warning,
        "info" => Severity.Info,
        _ => throw new InvalidDataException($"Unknown severity '{value}' in an audit rule."),
    };
}
