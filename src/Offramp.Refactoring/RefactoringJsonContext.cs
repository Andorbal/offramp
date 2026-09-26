using System.Text.Json;
using System.Text.Json.Serialization;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Codemods;
using Offramp.Refactoring.Conditional;
using Offramp.Refactoring.Dependencies.Consolidation;
using Offramp.Refactoring.Dependencies.Redirects;
using Offramp.Refactoring.Dependencies.Resolution;
using Offramp.Refactoring.Extract;
using Offramp.Refactoring.Forwarders;
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
[JsonSerializable(typeof(MovePlanDocument))]
[JsonSerializable(typeof(MovePlanResult))]
[JsonSerializable(typeof(MoveApplyResult))]
[JsonSerializable(typeof(ForwardersResult))]
[JsonSerializable(typeof(ConsolidateResult))]
[JsonSerializable(typeof(RedirectsResult))]
[JsonSerializable(typeof(ResolveDllsResult))]
[JsonSerializable(typeof(IfdefStripResult))]
[JsonSerializable(typeof(IfdefWrapResult))]
[JsonSerializable(typeof(ExtractInterfaceResult))]
[JsonSerializable(typeof(CodemodListResult))]
[JsonSerializable(typeof(CodemodRunResult))]
public sealed partial class RefactoringJsonContext : JsonSerializerContext
{
}
