using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Output;
using Offramp.Core.Progress;

namespace Offramp.Core.Json;

/// <summary>Source-generated serialization metadata for Offramp.Core types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(List<Diagnostic>))]
[JsonSerializable(typeof(DiagnosticSummary))]
[JsonSerializable(typeof(EnvelopeHeader))]
[JsonSerializable(typeof(ProgressEventBase))]
[JsonSerializable(typeof(OfframpConfig))]
[JsonSerializable(typeof(WorkspaceModel))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
public sealed partial class OfframpCoreJsonContext : JsonSerializerContext
{
    private static OfframpCoreJsonContext? _compact;

    /// <summary>Single-line output, for NDJSON progress events.</summary>
    public static OfframpCoreJsonContext Compact => _compact ??= new OfframpCoreJsonContext(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
