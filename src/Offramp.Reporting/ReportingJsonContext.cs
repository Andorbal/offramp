using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Reporting.Graph;
using Offramp.Reporting.Report;

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
[JsonSerializable(typeof(ReportData))]
[JsonSerializable(typeof(ReportResult))]
public sealed partial class ReportingJsonContext : JsonSerializerContext
{
}
