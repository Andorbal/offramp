using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.NuGet.Audit;
using Offramp.NuGet.Gac;
using Offramp.NuGet.Inspection;

namespace Offramp.NuGet;

/// <summary>Source-generated serialization metadata for Offramp.NuGet types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PackageInspection))]
[JsonSerializable(typeof(DepsAuditResult))]
[JsonSerializable(typeof(GacResult))]
public sealed partial class NuGetJsonContext : JsonSerializerContext
{
}
