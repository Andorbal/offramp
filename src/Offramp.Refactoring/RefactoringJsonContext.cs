using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Moves;

namespace Offramp.Refactoring;

/// <summary>Source-generated serialization metadata for Offramp.Refactoring types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Journal))]
[JsonSerializable(typeof(MoveTestsResult))]
[JsonSerializable(typeof(MoveRollbackResult))]
public sealed partial class RefactoringJsonContext : JsonSerializerContext
{
}
