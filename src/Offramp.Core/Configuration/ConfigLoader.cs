using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Paths;

namespace Offramp.Core.Configuration;

/// <summary>Inputs to configuration loading, one per precedence level.</summary>
public sealed record ConfigSources
{
    /// <summary>Absolute repository root; relative paths resolve against it.</summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>Absolute or root-relative path from <c>--config</c>; null means look for <c>offramp.yml</c>.</summary>
    public string? ExplicitPath { get; init; }

    /// <summary>The process environment (only <c>OFFRAMP_*</c> keys matter).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Values from command-line flags, as a partial configuration tree.</summary>
    public JsonObject? CommandLine { get; init; }
}

/// <summary>The result of loading configuration.</summary>
public sealed record ConfigLoadResult
{
    public required OfframpConfig Config { get; init; }

    /// <summary>The merged tree with sorted keys, echoed in every envelope as <c>effectiveConfig</c>.</summary>
    public required JsonNode Effective { get; init; }

    /// <summary>Repository-relative path of the file that was read, or null.</summary>
    public string? File { get; init; }

    /// <summary>False when the file had errors and was ignored.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Diagnostics about the configuration, before severity overrides.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Loads configuration with the precedence of <c>docs/spec/03-configuration.md</c>:
/// built-in defaults, then <c>offramp.yml</c>, then <c>OFFRAMP_*</c> environment
/// variables, then command-line flags; last wins.
/// </summary>
public static class ConfigLoader
{
    public const string DefaultFileName = "offramp.yml";
    public const string ConfigPathVariable = "OFFRAMP_CONFIG";

    /// <summary>Single-underscore aliases the specification names explicitly.</summary>
    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["OFFRAMP_TARGET"] = "/target",
        ["OFFRAMP_STATE"] = "/paths/state",
        ["OFFRAMP_LLM_PROVIDER"] = "/llm/provider",
        ["OFFRAMP_LLM_URL"] = "/llm/url",
        ["OFFRAMP_LLM_MODEL"] = "/llm/model",
    };

    /// <summary>Environment variables that are not configuration keys.</summary>
    private static readonly HashSet<string> NonConfigVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ConfigPathVariable, "OFFRAMP_NO_COLOR", "OFFRAMP_LLM_API_KEY",
    };

    public static JsonObject Defaults() => (JsonObject)OfframpJson.ToNode(new OfframpConfig())!;

    public static ConfigLoadResult Load(ConfigSources sources)
    {
        var diagnostics = new List<Diagnostic>();
        var defaults = Defaults();
        var merged = (JsonObject)defaults.DeepClone();

        var (filePath, relativeFile) = ResolveFile(sources, diagnostics);
        var fileValid = true;
        if (filePath is not null)
        {
            var fileTree = ReadFile(filePath, relativeFile!, diagnostics);
            fileValid = fileTree is not null && !diagnostics.Any(d => d.Severity == Severity.Error);
            if (fileValid && fileTree is not null)
            {
                Merge(merged, fileTree);
            }
        }
        else if (sources.ExplicitPath is null && diagnostics.Count == 0)
        {
            diagnostics.Add(Create(DiagnosticCatalog.OFR0016,
                $"No {DefaultFileName} at the repository root; built-in defaults are in effect. Run `offramp init` to create one.",
                file: null, position: null));
        }

        var environmentPointers = ApplyEnvironment(merged, defaults, sources.Environment, diagnostics);
        if (sources.CommandLine is not null)
        {
            Merge(merged, (JsonObject)sources.CommandLine.DeepClone());
        }

        ValidateOverlay(merged, environmentPointers, diagnostics);
        Normalize(merged);

        OfframpConfig config;
        try
        {
            config = OfframpJson.Deserialize<OfframpConfig>(merged) ?? new OfframpConfig();
        }
        catch (System.Text.Json.JsonException ex)
        {
            diagnostics.Add(Create(DiagnosticCatalog.OFR0053, $"Configuration could not be bound: {ex.Message}", relativeFile, null));
            fileValid = false;
            config = new OfframpConfig();
            merged = Defaults();
        }

        return new ConfigLoadResult
        {
            Config = config,
            Effective = OfframpJson.SortKeys(merged)!,
            File = relativeFile,
            IsValid = fileValid && !diagnostics.Any(d => d.Severity == Severity.Error),
            Diagnostics = [.. diagnostics.OrderBy(d => d, DiagnosticOrder.Instance)],
        };
    }

    private static (string? Absolute, string? Relative) ResolveFile(ConfigSources sources, List<Diagnostic> diagnostics)
    {
        var explicitPath = sources.ExplicitPath;
        if (explicitPath is null && sources.Environment.TryGetValue(ConfigPathVariable, out var fromEnv)
            && !string.IsNullOrWhiteSpace(fromEnv))
        {
            explicitPath = fromEnv;
        }

        if (explicitPath is not null)
        {
            var absolute = Path.GetFullPath(explicitPath, sources.RepositoryRoot);
            if (!System.IO.File.Exists(absolute))
            {
                diagnostics.Add(Create(DiagnosticCatalog.OFR0055,
                    $"Configuration file '{explicitPath}' does not exist.", null, null,
                    [KeyValuePair.Create<string, JsonNode?>("path", explicitPath)]));
                return (null, null);
            }

            return (absolute, RepoPaths.ToRepositoryRelative(sources.RepositoryRoot, absolute));
        }

        var candidate = Path.Combine(sources.RepositoryRoot, DefaultFileName);
        return System.IO.File.Exists(candidate) ? (candidate, DefaultFileName) : (null, null);
    }

    private static JsonObject? ReadFile(string path, string relative, List<Diagnostic> diagnostics)
    {
        YamlJsonDocument document;
        try
        {
            document = YamlJson.Parse(System.IO.File.ReadAllText(path));
        }
        catch (YamlSyntaxException ex)
        {
            diagnostics.Add(Create(DiagnosticCatalog.OFR0054, $"{relative} is not valid YAML: {ex.Message}", relative, ex.Position));
            return null;
        }

        if (document.Root is not JsonObject root)
        {
            diagnostics.Add(Create(DiagnosticCatalog.OFR0053,
                $"{relative} must be a mapping of settings at the top level.", relative, document.PositionOf("")));
            return null;
        }

        foreach (var problem in ConfigSchema.Validate(root))
        {
            if (problem.IsUnknownKey)
            {
                var keyPosition = document.Positions.TryGetValue(problem.Pointer + "#key", out var p) ? p : document.PositionOf(problem.Pointer);
                diagnostics.Add(Create(DiagnosticCatalog.OFR0050, problem.Message, relative, keyPosition,
                    [KeyValuePair.Create<string, JsonNode?>("key", ConfigSchema.DottedPath(problem.Pointer))]));
                Remove(root, problem.Pointer);
            }
            else
            {
                diagnostics.Add(Create(DiagnosticCatalog.OFR0053, problem.Message, relative, document.PositionOf(problem.Pointer),
                    [KeyValuePair.Create<string, JsonNode?>("key", ConfigSchema.DottedPath(problem.Pointer))]));
            }
        }

        CheckReasons(root, document, relative, diagnostics);
        return root;
    }

    private static void CheckReasons(JsonObject root, YamlJsonDocument document, string relative, List<Diagnostic> diagnostics)
    {
        if (root["deps"]?["pins"] is JsonArray pins)
        {
            for (var i = 0; i < pins.Count; i++)
            {
                if (pins[i] is JsonObject pin && !HasText(pin["reason"]))
                {
                    var package = pin["package"]?.ToString() ?? "?";
                    var pointer = "/deps/pins/" + i.ToString(CultureInfo.InvariantCulture);
                    diagnostics.Add(Create(DiagnosticCatalog.OFR0051,
                        $"Pin of {package} has no reason; add `reason:` so the pin stays attributable.",
                        relative, document.PositionOf(pointer),
                        [KeyValuePair.Create<string, JsonNode?>("package", package)]));
                }
            }
        }

        if (root["rules"] is JsonObject rules)
        {
            foreach (var (code, value) in rules.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (code == "packs" || value is not JsonObject rule)
                {
                    continue;
                }

                if (!HasText(rule["reason"]))
                {
                    diagnostics.Add(Create(DiagnosticCatalog.OFR0052,
                        $"Override of {code} has no reason; add `reason:` so the suppression stays attributable.",
                        relative, document.PositionOf("/rules/" + YamlJson.EscapePointer(code)),
                        [KeyValuePair.Create<string, JsonNode?>("rule", code)]));
                }
            }
        }
    }

    private static bool HasText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Applies <c>OFFRAMP_*</c> variables in ordinal name order. <c>__</c> separates
    /// levels and <c>UPPER_SNAKE</c> segments become camelCase
    /// (<c>OFFRAMP_VERIFY__TIMEOUT_SECONDS</c> → <c>verify.timeoutSeconds</c>).
    /// Variables that do not name a known setting are ignored.
    /// </summary>
    private static Dictionary<string, string> ApplyEnvironment(
        JsonObject merged, JsonObject defaults, IReadOnlyDictionary<string, string> environment, List<Diagnostic> diagnostics)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in environment.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!name.StartsWith("OFFRAMP_", StringComparison.OrdinalIgnoreCase) || NonConfigVariables.Contains(name))
            {
                continue;
            }

            var pointer = Aliases.TryGetValue(name, out var alias) ? alias : EnvironmentPointer(name);
            if (pointer is null || !TryGetParent(defaults, pointer, out var defaultParent, out var key)
                || !defaultParent.ContainsKey(key))
            {
                continue;
            }

            var converted = ConvertEnvironmentValue(defaultParent[key], value);
            if (converted.Error is not null)
            {
                diagnostics.Add(Create(DiagnosticCatalog.OFR0056,
                    $"{name}={value}: {converted.Error}", null, null,
                    [KeyValuePair.Create<string, JsonNode?>("variable", name)]));
                continue;
            }

            if (TryGetParent(merged, pointer, out var parent, out var leaf))
            {
                parent[leaf] = converted.Value;
                applied[pointer] = name;
            }
        }

        return applied;
    }

    internal static string? EnvironmentPointer(string name)
    {
        var rest = name["OFFRAMP_".Length..];
        if (rest.Length == 0)
        {
            return null;
        }

        var segments = rest.Split("__", StringSplitOptions.None);
        if (segments.Any(s => s.Length == 0))
        {
            return null;
        }

        return "/" + string.Join('/', segments.Select(ToCamel));
    }

    private static string ToCamel(string upperSnake)
    {
        var parts = upperSnake.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static (JsonNode? Value, string? Error) ConvertEnvironmentValue(JsonNode? template, string raw)
    {
        switch (template)
        {
            case JsonArray:
                var items = raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return (new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]), null);
            case JsonObject:
                return (null, "this setting is a section and cannot be set from one variable");
            case JsonValue value when value.TryGetValue<bool>(out _):
                return raw.Trim().ToUpperInvariant() switch
                {
                    "TRUE" or "1" or "YES" => (JsonValue.Create(true), null),
                    "FALSE" or "0" or "NO" => (JsonValue.Create(false), null),
                    _ => (null, "expected true or false"),
                };
            case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                return long.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                    ? (JsonValue.Create(number), null)
                    : (null, "expected an integer");
            default:
                return (raw.Length == 0 ? null : JsonValue.Create(raw), null);
        }
    }

    private static void ValidateOverlay(JsonObject merged, Dictionary<string, string> environmentPointers, List<Diagnostic> diagnostics)
    {
        if (environmentPointers.Count == 0)
        {
            return;
        }

        foreach (var problem in ConfigSchema.Validate(merged))
        {
            if (problem.IsUnknownKey || !environmentPointers.TryGetValue(problem.Pointer, out var variable))
            {
                continue;
            }

            diagnostics.Add(Create(DiagnosticCatalog.OFR0056, $"{variable}: {problem.Message}", null, null,
                [KeyValuePair.Create<string, JsonNode?>("variable", variable)]));
        }
    }

    /// <summary>Values the schema accepts as numbers or booleans but the model binds as strings.</summary>
    private static void Normalize(JsonObject merged)
    {
        if (merged["deps"]?["pins"] is JsonArray pins)
        {
            foreach (var pin in pins.OfType<JsonObject>())
            {
                if (pin["version"] is JsonValue version && version.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    pin["version"] = version.ToJsonString();
                }
            }
        }

        if (merged["verify"]?["properties"] is JsonObject properties)
        {
            foreach (var key in properties.Select(p => p.Key).ToList())
            {
                if (properties[key] is JsonValue value && value.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    properties[key] = value.GetValueKind() switch
                    {
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        _ => value.ToJsonString(),
                    };
                }
            }
        }

        if (merged["verify"]?["noWarn"] is JsonArray noWarn)
        {
            for (var i = 0; i < noWarn.Count; i++)
            {
                if (noWarn[i] is JsonValue item && item.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    noWarn[i] = item.ToJsonString();
                }
            }
        }
    }

    /// <summary>Deep merge: objects merge key by key; arrays and scalars replace.</summary>
    internal static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            if (value is JsonObject overlayObject && target[key] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static bool TryGetParent(JsonObject root, string pointer, out JsonObject parent, out string key)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(YamlJson.UnescapePointer).ToList();
        parent = root;
        key = segments[^1];
        foreach (var segment in segments.Take(segments.Count - 1))
        {
            if (parent[segment] is not JsonObject next)
            {
                return false;
            }

            parent = next;
        }

        return true;
    }

    private static void Remove(JsonObject root, string pointer)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(YamlJson.UnescapePointer).ToList();
        JsonNode? current = root;
        foreach (var segment in segments.Take(segments.Count - 1))
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
                _ => null,
            };
        }

        if (current is JsonObject parent)
        {
            parent.Remove(segments[^1]);
        }
    }

    private static Diagnostic Create(
        DiagnosticDescriptor descriptor, string message, string? file, SourcePosition? position,
        IEnumerable<KeyValuePair<string, JsonNode?>>? data = null)
    {
        var sorted = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in data ?? [])
        {
            sorted[key] = value;
        }

        return new Diagnostic
        {
            Code = descriptor.Code,
            Severity = descriptor.DefaultSeverity,
            Message = message,
            File = file,
            Line = position?.Line,
            Column = position?.Column,
            Data = sorted,
            Help = descriptor.HelpUri,
        };
    }
}
