using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Offramp.Core.Json;

/// <summary>
/// The one place JSON output settings are defined. Every JSON document Offramp
/// writes (envelopes, the workspace model, plans, caches) uses these options, so
/// output bytes are identical on every OS and runtime.
/// </summary>
public static class OfframpJson
{
    private static readonly object Gate = new();
    private static readonly List<IJsonTypeInfoResolver> Resolvers = [OfframpCoreJsonContext.Default];
    private static JsonSerializerOptions? _options;

    /// <summary>Options with every registered source-generated context.</summary>
    public static JsonSerializerOptions Options
    {
        get
        {
            lock (Gate)
            {
                return _options ??= Create([.. Resolvers]);
            }
        }
    }

    /// <summary>
    /// Registers a library's source-generated context. Call from a module
    /// initializer or before first use of <see cref="Options"/>.
    /// </summary>
    public static void Register(IJsonTypeInfoResolver resolver)
    {
        lock (Gate)
        {
            if (Resolvers.Contains(resolver))
            {
                return;
            }

            Resolvers.Add(resolver);
            _options = null;
        }
    }

    public static JsonSerializerOptions Create(params IJsonTypeInfoResolver[] resolvers)
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers),
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.MakeReadOnly();
        return options;
    }

    public static JsonWriterOptions WriterOptions => new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static string Serialize<T>(T value) => Serialize(value, TypeInfo<T>());

    /// <summary>Serializes with Offramp's writer settings (relaxed escaping, LF, two spaces), ending with a newline.</summary>
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, TypeInfo<T>());

    public static T? Deserialize<T>(JsonNode node) => node.Deserialize(TypeInfo<T>());

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, TypeInfo<T>());

    /// <summary>Serializes a node with Offramp's formatting, ending with a newline.</summary>
    public static string Format(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            if (node is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                node.WriteTo(writer);
            }
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// Returns a deep copy of <paramref name="node"/> with every object's keys in
    /// ordinal order. Used where maps come from user input (configuration).
    /// </summary>
    public static JsonNode? SortKeys(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(
            obj.OrderBy(p => p.Key, StringComparer.Ordinal)
               .Select(p => KeyValuePair.Create(p.Key, SortKeys(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(SortKeys)]),
        null => null,
        _ => node.DeepClone(),
    };
}
