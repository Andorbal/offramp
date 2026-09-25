using System.Reflection;

namespace Offramp.Core.Diagnostics;

/// <summary>
/// Every diagnostic code Offramp emits. <c>docs/diagnostics.md</c> is generated
/// from this catalog (<c>eng/gen-diagnostics.sh</c>), and a test fails when the
/// document and the catalog disagree. Codes are never renumbered or reused.
/// </summary>
public static partial class DiagnosticCatalog
{
    private static readonly Lazy<IReadOnlyList<DiagnosticDescriptor>> AllDescriptors = new(Discover);

    /// <summary>All emitted codes, sorted by code.</summary>
    public static IReadOnlyList<DiagnosticDescriptor> All => AllDescriptors.Value;

    public static DiagnosticDescriptor? Find(string code) =>
        All.FirstOrDefault(d => string.Equals(d.Code, code, StringComparison.Ordinal));

    private static IReadOnlyList<DiagnosticDescriptor> Discover() =>
        [.. typeof(DiagnosticCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(DiagnosticDescriptor))
            .Select(f => (DiagnosticDescriptor)f.GetValue(null)!)
            .OrderBy(d => d.Code, StringComparer.Ordinal)];
}
