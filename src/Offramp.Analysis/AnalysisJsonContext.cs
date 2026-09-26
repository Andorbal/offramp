using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Analysis.ApiCompat;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Conditional;
using Offramp.Analysis.DeadCode;

namespace Offramp.Analysis;

/// <summary>Source-generated serialization metadata for Offramp.Analysis types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AuditResult))]
[JsonSerializable(typeof(IfdefReportResult))]
[JsonSerializable(typeof(DeadCodeResult))]
[JsonSerializable(typeof(ApiCompatResult))]
public sealed partial class AnalysisJsonContext : JsonSerializerContext
{
}
