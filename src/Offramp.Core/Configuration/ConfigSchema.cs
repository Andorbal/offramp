using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Offramp.Core.Configuration;

/// <summary>One problem found by validating configuration against <c>schemas/v1/config.json</c>.</summary>
/// <param name="Pointer">JSON pointer of the offending value (or unknown key).</param>
/// <param name="IsUnknownKey">True for keys the schema does not define.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record ConfigSchemaProblem(string Pointer, bool IsUnknownKey, string Message);

/// <summary>Validates configuration trees against the embedded <c>schemas/v1/config.json</c>.</summary>
public static class ConfigSchema
{
    public const string ResourceName = "Offramp.Schemas.v1.config.json";

    private static readonly HashSet<string> SummaryKeywords = new(StringComparer.Ordinal)
    {
        "properties", "additionalProperties", "patternProperties", "items", "prefixItems",
        "$ref", "allOf", "anyOf", "oneOf", "not", "if", "then", "else", "dependentSchemas",
    };

    private static readonly Lazy<(JsonSchema Schema, JsonNode Document)> Loaded = new(Load);

    /// <summary>The raw schema text, as shipped in <c>schemas/v1/config.json</c>.</summary>
    public static string Text
    {
        get
        {
            using var stream = typeof(ConfigSchema).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static IReadOnlyList<ConfigSchemaProblem> Validate(JsonNode instance)
    {
        var (schema, document) = Loaded.Value;
        using var json = JsonDocument.Parse(instance.ToJsonString());
        var results = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        var problems = new SortedDictionary<string, ConfigSchemaProblem>(StringComparer.Ordinal);
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null || detail.IsValid)
            {
                continue;
            }

            var pointer = detail.InstanceLocation.ToString();
            var evaluationPath = detail.EvaluationPath.ToString();
            foreach (var (keyword, error) in detail.Errors)
            {
                if (keyword.Length == 0 && evaluationPath.EndsWith("/additionalProperties", StringComparison.Ordinal))
                {
                    var parent = evaluationPath[..^"/additionalProperties".Length];
                    var known = KnownKeys(document, parent);
                    var key = LastSegment(pointer);
                    var suggestion = Closest(key, known);
                    var message = $"Unknown key '{DottedPath(pointer)}' is ignored."
                        + (suggestion is null ? "" : $" Did you mean '{suggestion}'?");
                    problems.TryAdd(pointer + "|unknown", new ConfigSchemaProblem(pointer, true, message));
                    continue;
                }

                if (SummaryKeywords.Contains(keyword))
                {
                    continue;
                }

                var text = keyword == "enum"
                    ? $"{DottedPath(pointer)}: {Describe(instance, pointer)} is not one of {AllowedValues(document, evaluationPath)}."
                    : $"{DottedPath(pointer)}: {error}";
                problems.TryAdd(pointer + "|" + keyword, new ConfigSchemaProblem(pointer, false, text));
            }
        }

        return [.. problems.Values];
    }

    /// <summary><c>/verify/mode</c> → <c>verify.mode</c>; <c>/deps/pins/0/version</c> → <c>deps.pins[0].version</c>.</summary>
    public static string DottedPath(string pointer)
    {
        if (pointer.Length == 0)
        {
            return "(root)";
        }

        var builder = new System.Text.StringBuilder();
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = YamlJson.UnescapePointer(raw);
            if (segment.All(char.IsAsciiDigit) && segment.Length > 0 && builder.Length > 0)
            {
                builder.Append('[').Append(segment).Append(']');
            }
            else
            {
                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(segment);
            }
        }

        return builder.ToString();
    }

    private static (JsonSchema, JsonNode) Load()
    {
        var text = Text;
        var document = JsonNode.Parse(text) ?? throw new InvalidOperationException("config schema is empty");
        var schema = JsonSchema.FromText(text, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        return (schema, document);
    }

    private static JsonNode? Navigate(JsonNode document, string pointer)
    {
        JsonNode? current = document;
        foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = YamlJson.UnescapePointer(raw);
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index < array.Count => array[index],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>Follows an evaluation path through the schema document, resolving local <c>$ref</c>s.</summary>
    private static JsonNode? ResolveEvaluationPath(JsonNode document, string evaluationPath)
    {
        JsonNode? current = document;
        foreach (var raw in evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = YamlJson.UnescapePointer(raw);
            if (segment == "$ref" && current?["$ref"] is JsonValue reference
                && reference.TryGetValue<string>(out var target) && target.StartsWith('#'))
            {
                current = Navigate(document, target[1..]);
                continue;
            }

            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index < array.Count => array[index],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static List<string> KnownKeys(JsonNode document, string evaluationPath)
    {
        var node = ResolveEvaluationPath(document, evaluationPath)?["properties"] as JsonObject;
        return node is null ? [] : [.. node.Select(p => p.Key)];
    }

    private static string AllowedValues(JsonNode document, string evaluationPath)
    {
        if (ResolveEvaluationPath(document, evaluationPath)?["enum"] is not JsonArray values)
        {
            return "the allowed values";
        }

        return string.Join(", ", values.Select(v => v is null ? "null" : v.ToJsonString().Trim('"')));
    }

    private static string Describe(JsonNode instance, string pointer)
    {
        var value = Navigate(instance, pointer);
        return value is null ? "null" : value.ToJsonString();
    }

    private static string LastSegment(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        return YamlJson.UnescapePointer(slash < 0 ? pointer : pointer[(slash + 1)..]);
    }

    /// <summary>The known key within edit distance 2 of <paramref name="key"/>, if exactly one is closest.</summary>
    internal static string? Closest(string key, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates.OrderBy(c => c, StringComparer.Ordinal))
        {
            var distance = Levenshtein(key.ToUpperInvariant(), candidate.ToUpperInvariant());
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return bestDistance <= 2 ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
