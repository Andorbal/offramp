using System.Text.Json;
using System.Text.Json.Serialization;

namespace Offramp.Ide;

/// <summary>Source-generated serialization metadata for Offramp.Ide types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(IdeCheckResult))]
[JsonSerializable(typeof(IdeFileReport))]
[JsonSerializable(typeof(IdeMoveResult))]
[JsonSerializable(typeof(IdeSettings))]
[JsonSerializable(typeof(List<ProjectCounterparts>))]
public sealed partial class IdeJsonContext : JsonSerializerContext
{
}
