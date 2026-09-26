using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Workspace.Doctor;

namespace Offramp.Workspace;

/// <summary>Source-generated serialization metadata for Offramp.Workspace result types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(Offramp.Workspace.Guide.GuideResult))]
[JsonSerializable(typeof(Offramp.Workspace.Guide.GuideState))]
[JsonSerializable(typeof(Offramp.Workspace.Init.InitResult))]
[JsonSerializable(typeof(Offramp.Workspace.Planning.PlanResult))]
[JsonSerializable(typeof(Offramp.Workspace.Store.LedgerSnapshot))]
[JsonSerializable(typeof(Offramp.Workspace.Scanning.ScanResult))]
[JsonSerializable(typeof(Offramp.Workspace.Slicing.SliceResult))]
[JsonSerializable(typeof(Offramp.Workspace.Verification.VerifyResult))]
[JsonSerializable(typeof(Offramp.Workspace.Verification.VerifyBaseline))]
public sealed partial class WorkspaceJsonContext : JsonSerializerContext
{
}
