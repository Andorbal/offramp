using Microsoft.CodeAnalysis;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>
/// Which types a probe can write a value of as C#: primitives and the framework's value-like
/// types, enums, nullables, arrays and the common collection types of those, and concrete
/// classes and structs it can create. Interfaces and abstract types cannot be (no mocks in v1),
/// nor anything from Newtonsoft.Json (entry probes never reference it).
/// </summary>
internal static class Generatability
{
    private static readonly HashSet<string> ValueLike = new(StringComparer.Ordinal)
    {
        "System.DateTimeOffset", "System.TimeSpan", "System.Guid", "System.Uri",
    };

    private static readonly HashSet<string> Collections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List<T>", "System.Collections.Generic.IList<T>", "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IEnumerable<T>", "System.Collections.Generic.IReadOnlyList<T>", "System.Collections.Generic.IReadOnlyCollection<T>",
        "System.Collections.Generic.HashSet<T>", "System.Collections.Generic.ISet<T>",
        "System.Collections.Generic.Dictionary<TKey, TValue>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
    };

    /// <summary>True when a probe can write a value of the type; <paramref name="accessible"/> says whether the test project sees a symbol.</summary>
    public static bool IsGeneratable(ITypeSymbol type, Func<ISymbol, bool> accessible) =>
        IsGeneratable(type, accessible, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    /// <summary>A parameter the probe reads back after the call: <c>out</c>/<c>ref</c>, or a stream, writer, or builder it passes in empty.</summary>
    public static bool IsOutput(IParameterSymbol parameter) =>
        parameter.RefKind is RefKind.Out or RefKind.Ref
        || parameter.Type.ToDisplayString() is "System.IO.Stream" or "System.IO.MemoryStream" or "System.IO.TextWriter" or "System.IO.StringWriter" or "System.Text.StringBuilder";

    /// <summary>A parameter that can carry JSON text into a deserialize or DOM site.</summary>
    public static bool IsJsonInput(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_String
        || type.ToDisplayString() is "System.IO.TextReader" or "System.IO.StringReader" or "System.IO.Stream" or "System.IO.MemoryStream";

    /// <summary>The constructor a probe uses: the accessible one with the fewest parameters, all generatable; null when there is none.</summary>
    public static IMethodSymbol? Constructor(INamedTypeSymbol type, Func<ISymbol, bool> accessible) =>
        type.InstanceConstructors
            .Where(c => accessible(c) && c.Parameters.All(p => p.RefKind == RefKind.None && IsGeneratable(p.Type, accessible)))
            .OrderBy(c => c.Parameters.Length)
            .ThenBy(c => c.ToDisplayString(), StringComparer.Ordinal)
            .FirstOrDefault();

    private static bool IsGeneratable(ITypeSymbol type, Func<ISymbol, bool> accessible, HashSet<ITypeSymbol> visiting)
    {
        if (type.ContainingNamespace?.ToDisplayString().StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true)
        {
            return false;
        }

        switch (type)
        {
            // A value of type object says nothing about the payload; dynamic and pointers cannot be written.
            case { SpecialType: SpecialType.System_Object or SpecialType.System_Void } or IDynamicTypeSymbol or IPointerTypeSymbol:
                return false;
            case { SpecialType: not SpecialType.None }:
                return true;
            case INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration:
                return accessible(enumeration);
            case IArrayTypeSymbol { Rank: 1 } array:
                return IsGeneratable(array.ElementType, accessible, visiting);
            case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable:
                return IsGeneratable(nullable.TypeArguments[0], accessible, visiting);
            case INamedTypeSymbol named when ValueLike.Contains(named.ToDisplayString()):
                return true;
            case INamedTypeSymbol { IsGenericType: true } generic when Collections.Contains(generic.OriginalDefinition.ToDisplayString()):
                return generic.TypeArguments.All(a => IsGeneratable(a, accessible, visiting));
            case INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false, IsStatic: false } named
                when !named.IsUnboundGenericType && named.TypeArguments.All(a => a is not ITypeParameterSymbol) && accessible(named):
                // A type that contains itself is generatable: nesting stops at the sample depth with null.
                if (!visiting.Add(named))
                {
                    return true;
                }

                var constructible = named.TypeKind == TypeKind.Struct || named.InstanceConstructors.Any(c => accessible(c)
                    && c.Parameters.All(p => p.RefKind == RefKind.None && IsGeneratable(p.Type, accessible, visiting)));
                visiting.Remove(named);
                return constructible;
            default:
                return false;
        }
    }
}
