using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Scaffolding.Config;
using Offramp.Scaffolding.Csproj;
using Offramp.Scaffolding.Remote;
using Offramp.Scaffolding.Service;
using Offramp.Scaffolding.Web;

namespace Offramp.Scaffolding;

/// <summary>Source-generated serialization metadata for Offramp.Scaffolding types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    IndentSize = 2,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RemoteResult))]
[JsonSerializable(typeof(ServiceResult))]
[JsonSerializable(typeof(ModernizeResult))]
[JsonSerializable(typeof(ConfigConvertResult))]
[JsonSerializable(typeof(WebInventoryResult))]
[JsonSerializable(typeof(WebScaffoldResult))]
public sealed partial class ScaffoldingJsonContext : JsonSerializerContext
{
}
