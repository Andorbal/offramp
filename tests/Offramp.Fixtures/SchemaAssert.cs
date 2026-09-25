using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace Offramp.Fixtures;

/// <summary>Validates JSON against the schemas in <c>schemas/v1/</c>.</summary>
public static class SchemaAssert
{
    private static readonly Lazy<(SchemaRegistry Registry, Dictionary<string, JsonSchema> Schemas)> Loaded = new(Load);

    /// <summary>Validates an envelope and its <c>result</c> against <c>envelope.json</c> and <c>&lt;resultSchema&gt;.json</c>.</summary>
    public static void ValidEnvelope(string json, string resultSchema)
    {
        Valid("envelope", json);
        var node = JsonNode.Parse(json)!;
        var result = node["result"];
        Assert.NotNull(result);
        Valid(resultSchema, result!.ToJsonString());
    }

    public static void Valid(string schemaName, string json)
    {
        var errors = Validate(schemaName, json);
        Assert.True(errors.Count == 0,
            $"JSON does not match schemas/v1/{schemaName}.json:\n  " + string.Join("\n  ", errors) + "\n\n" + json);
    }

    public static IReadOnlyList<string> Validate(string schemaName, string json)
    {
        var (_, schemas) = Loaded.Value;
        if (!schemas.TryGetValue(schemaName, out var schema))
        {
            throw new InvalidOperationException($"schemas/v1/{schemaName}.json does not exist.");
        }

        using var document = JsonDocument.Parse(json);
        var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return [];
        }

        return [.. (results.Details ?? [])
            .Where(d => !d.IsValid && d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Key} {e.Value}"))
            .Distinct()];
    }

    public static IReadOnlyCollection<string> SchemaNames => Loaded.Value.Schemas.Keys;

    private static (SchemaRegistry, Dictionary<string, JsonSchema>) Load()
    {
        var registry = new SchemaRegistry();
        var options = new BuildOptions { SchemaRegistry = registry };
        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepositoryFiles.Path("schemas", "v1"), "*.json").Order(StringComparer.Ordinal))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file), options);
            schemas[System.IO.Path.GetFileNameWithoutExtension(file)] = schema;
        }

        return (registry, schemas);
    }
}
