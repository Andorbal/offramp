using Microsoft.CodeAnalysis;

namespace Offramp.Analyzers;

internal static class Symbols
{
    /// <summary>The fully qualified name without <c>global::</c>: <c>System.Diagnostics.Process</c>.</summary>
    public static string Name(ISymbol? symbol) => symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "";

    public static bool IsMethod(ISymbol? symbol, string type, string name) =>
        symbol is IMethodSymbol method && method.Name == name && Name(method.ContainingType.OriginalDefinition) == type;

    public static bool IsType(ITypeSymbol? type, string name) => type is not null && Name(type.OriginalDefinition) == name;
}
