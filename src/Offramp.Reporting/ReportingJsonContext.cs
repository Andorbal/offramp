using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Reporting.Graph;

namespace Offramp.Reporting;

/// <summary>Source-generated serialization metadata for Offramp.Reporting types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GraphDocument))]
[JsonSerializable(typeof(GraphResult))]
public sealed partial class ReportingJsonContext : JsonSerializerContext
{
}
