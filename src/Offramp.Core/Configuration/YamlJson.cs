using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Offramp.Core.Configuration;

/// <summary>A position in a source file, 1-based.</summary>
public readonly record struct SourcePosition(int Line, int Column);

/// <summary>The JSON view of a YAML document plus where each value came from.</summary>
public sealed record YamlJsonDocument(JsonNode Root, IReadOnlyDictionary<string, SourcePosition> Positions)
{
    /// <summary>Finds the position of a JSON pointer, or of its nearest ancestor.</summary>
    public SourcePosition? PositionOf(string pointer)
    {
        var current = pointer;
        while (true)
        {
            if (Positions.TryGetValue(current, out var pos))
            {
                return pos;
            }

            var slash = current.LastIndexOf('/');
            if (slash <= 0)
            {
                return Positions.TryGetValue("", out var rootPos) ? rootPos : null;
            }

            current = current[..slash];
        }
    }
}

/// <summary>Thrown when a YAML document cannot be parsed.</summary>
public sealed class YamlSyntaxException(string message, SourcePosition position, Exception inner)
    : Exception(message, inner)
{
    public SourcePosition Position { get; } = position;
}

/// <summary>
/// Converts YAML to <see cref="JsonNode"/> using the YAML 1.2 core schema for
/// plain scalars (null, booleans, integers, floats); quoted scalars are always strings.
/// </summary>
public static partial class YamlJson
{
    public static YamlJsonDocument Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            var position = new SourcePosition((int)ex.Start.Line, (int)ex.Start.Column);
            throw new YamlSyntaxException(ex.InnerException?.Message ?? ex.Message, position, ex);
        }
        catch (InvalidOperationException ex)
        {
            // YamlDotNet reports some malformed flow collections this way, without a position.
            throw new YamlSyntaxException("malformed document (" + ex.Message + ")", new SourcePosition(1, 1), ex);
        }

        var positions = new Dictionary<string, SourcePosition>(StringComparer.Ordinal);
        if (stream.Documents.Count == 0)
        {
            return new YamlJsonDocument(new JsonObject(), positions);
        }

        var root = Convert(stream.Documents[0].RootNode, "", positions) ?? new JsonObject();
        return new YamlJsonDocument(root, positions);
    }

    private static JsonNode? Convert(YamlNode node, string pointer, Dictionary<string, SourcePosition> positions)
    {
        positions[pointer] = new SourcePosition((int)node.Start.Line, (int)node.Start.Column);
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (keyNode, valueNode) in mapping.Children)
                {
                    var key = keyNode is YamlScalarNode scalarKey ? scalarKey.Value ?? "" : keyNode.ToString();
                    var childPointer = pointer + "/" + EscapePointer(key);
                    obj[key] = Convert(valueNode, childPointer, positions);
                    positions[childPointer + "#key"] = new SourcePosition((int)keyNode.Start.Line, (int)keyNode.Start.Column);
                }

                return obj;

            case YamlSequenceNode sequence:
                var array = new JsonArray();
                var index = 0;
                foreach (var item in sequence.Children)
                {
                    array.Add(Convert(item, pointer + "/" + index.ToString(CultureInfo.InvariantCulture), positions));
                    index++;
                }

                return array;

            case YamlScalarNode scalar:
                return ConvertScalar(scalar);

            default:
                return null;
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted
            or ScalarStyle.Literal or ScalarStyle.Folded)
        {
            return JsonValue.Create(value);
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (IntegerPattern().IsMatch(value) || FloatPattern().IsMatch(value))
        {
            // Keep the literal text ("2.0" stays "2.0") so values bound to strings,
            // such as package versions, round-trip exactly.
            return ParseNumber(value);
        }

        return JsonValue.Create(value);
    }

    private static JsonNode ParseNumber(string value)
    {
        var text = value.TrimStart('+');
        if (text.StartsWith('.'))
        {
            text = "0" + text;
        }
        else if (text.StartsWith("-.", StringComparison.Ordinal))
        {
            text = "-0" + text[1..];
        }

        if (text.EndsWith('.'))
        {
            text += "0";
        }

        try
        {
            return JsonNode.Parse(text) ?? JsonValue.Create(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : JsonValue.Create(value);
        }
    }

    public static string EscapePointer(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static string UnescapePointer(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    [GeneratedRegex(@"^[-+]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPattern();
}
