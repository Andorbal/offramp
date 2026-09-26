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
[JsonSerializable(typeof(Offramp.Workspace.Init.InitResult))]
[JsonSerializable(typeof(Offramp.Workspace.Store.LedgerSnapshot))]
[JsonSerializable(typeof(Offramp.Workspace.Scanning.ScanResult))]
[JsonSerializable(typeof(Offramp.Workspace.Slicing.SliceResult))]
public sealed partial class WorkspaceJsonContext : JsonSerializerContext
{
}
