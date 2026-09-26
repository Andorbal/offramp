using System.Text.Json;
using System.Text.Json.Nodes;
using Offramp.Analysis.Audits;
using Offramp.Core.Diagnostics;

namespace Offramp.Reporting.Audit;

/// <summary>
/// An audit as SARIF 2.1.0, for GitHub code scanning and IDEs. Paths are repository-relative
/// under the <c>SRCROOT</c> base; every rule that ran is listed, with its recommendation as help.
/// </summary>
public static class AuditSarifWriter
{
    public const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, NewLine = "\n" };

    public static string Write(AuditResult result, string toolVersion)
    {
        var rules = AuditRules.Every.Where(r => result.Rules.Contains(r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var index = rules.Select((r, i) => (r.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        var driver = new JsonObject
        {
            ["name"] = "offramp",
            ["informationUri"] = "https://offramp.dev",
            ["semanticVersion"] = toolVersion,
            ["rules"] = new JsonArray([.. rules.Select(Rule)]),
        };
        var run = new JsonObject
        {
            ["tool"] = new JsonObject { ["driver"] = driver },
            ["automationDetails"] = new JsonObject { ["id"] = $"offramp/audit/{AuditRunner.Wire(result.Audit)}/{result.Target}" },
            ["columnKind"] = "unicodeCodePoints",
            ["results"] = new JsonArray([.. result.Findings.Select(f => Result(f, index))]),
        };
        var document = new JsonObject
        {
            ["$schema"] = SchemaUri,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(run),
        };
        return document.ToJsonString(Indented) + "\n";
    }

    private static JsonObject Rule(AuditRule rule) => new JsonObject
    {
        ["id"] = rule.Id,
        ["name"] = Name(rule.Title),
        ["shortDescription"] = new JsonObject { ["text"] = rule.Title },
        ["helpUri"] = rule.HelpUri,
        ["help"] = new JsonObject { ["text"] = rule.Recommendation },
        ["defaultConfiguration"] = new JsonObject { ["level"] = Level(rule.Severity) },
        ["properties"] = new JsonObject
        {
            ["category"] = rule.Category,
            ["tags"] = new JsonArray("offramp", rule.Pack, rule.Category),
        },
    };

    private static JsonObject Result(AuditFinding finding, Dictionary<string, int> index)
    {
        var properties = new JsonObject
        {
            ["project"] = finding.Project,
            ["symbol"] = finding.Symbol,
            ["category"] = finding.Category,
        };
        if (finding.Overridden)
        {
            properties["overridden"] = true;
        }

        foreach (var (key, value) in finding.Details)
        {
            properties[key] = value;
        }

        var result = new JsonObject
        {
            ["ruleId"] = finding.Rule,
            ["level"] = Level(finding.Severity),
            ["message"] = new JsonObject { ["text"] = finding.Message },
            ["locations"] = new JsonArray(new JsonObject
            {
                ["physicalLocation"] = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = finding.File, ["uriBaseId"] = "SRCROOT" },
                    ["region"] = new JsonObject { ["startLine"] = finding.Line, ["startColumn"] = finding.Column },
                },
            }),
            ["partialFingerprints"] = new JsonObject { ["offrampSymbol/v1"] = $"{finding.Rule}:{finding.Project}:{finding.Symbol}" },
            ["properties"] = properties,
        };
        if (index.TryGetValue(finding.Rule, out var ruleIndex))
        {
            result["ruleIndex"] = ruleIndex;
        }

        return result;
    }

    /// <summary>SARIF levels: error, warning, note.</summary>
    public static string Level(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        _ => "note",
    };

    /// <summary>A PascalCase rule name from its title ("culture-sensitive string operation" → CultureSensitiveStringOperation).</summary>
    private static string Name(string title) =>
        string.Concat(title.Split([' ', '-', '/', '(', ')', '.', ',', '[', ']', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
