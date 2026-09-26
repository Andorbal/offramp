using Microsoft.CodeAnalysis;

namespace Offramp.Analysis.Seams;

/// <summary>
/// Whether a member can cross a network boundary as data (docs/spec/commands/seams.md): its
/// parameters and return are primitives, strings, enums, <c>DateTime(Offset)</c>,
/// <c>TimeSpan</c>, <c>Guid</c>, <c>decimal</c>, nullable, arrays or lists or string-keyed
/// dictionaries of those, or classes and structs whose public settable properties are. Tasks
/// are unwrapped and <c>CancellationToken</c> is allowed. Delegates, events, streams,
/// pointers, <c>ref</c>/<c>out</c>, <c>object</c>, and other interfaces are not.
/// </summary>
public static class WireFriendliness
{
    private static readonly HashSet<string> Simple = new(StringComparer.Ordinal)
    {
        "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.Guid", "System.Uri", "System.Threading.CancellationToken",
    };

    private static readonly HashSet<string> Sequences = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List<T>", "System.Collections.Generic.IList<T>", "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.IEnumerable<T>", "System.Collections.Generic.ICollection<T>", "System.Collections.Generic.IReadOnlyCollection<T>",
        "System.Collections.Generic.HashSet<T>", "System.Collections.Generic.ISet<T>",
    };

    private static readonly HashSet<string> Maps = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.Dictionary<TKey, TValue>", "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
    };

    /// <summary>Why a member cannot cross the wire, one entry per offending part; empty when it can.</summary>
    public static IEnumerable<string> Problems(ISymbol member)
    {
        switch (member)
        {
            case IMethodSymbol method:
                foreach (var parameter in method.Parameters)
                {
                    if (parameter.RefKind != RefKind.None)
                    {
                        yield return $"'{parameter.Name}' is passed by {parameter.RefKind.ToString().ToLowerInvariant()}";
                    }
                    else if (Why(parameter.Type) is { } why)
                    {
                        yield return $"'{parameter.Name}': {why}";
                    }
                }

                if (!method.ReturnsVoid && Why(method.ReturnType) is { } returned)
                {
                    yield return $"return: {returned}";
                }

                break;
            case IPropertySymbol property when Why(property.Type) is { } propertyWhy:
                yield return propertyWhy;
                break;
            case IFieldSymbol field when Why(field.Type) is { } fieldWhy:
                yield return fieldWhy;
                break;
            case IEventSymbol:
                yield return "events cannot cross the wire";
                break;
        }
    }

    /// <summary>Why a type is not DTO-able, or null.</summary>
    public static string? Why(ITypeSymbol type) => Why(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));

    private static string? Why(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        var display = type.OriginalDefinition.ToDisplayString();
        switch (type)
        {
            case { SpecialType: SpecialType.System_Object }:
                return "object has no wire shape";
            case { SpecialType: not SpecialType.None and not SpecialType.System_IntPtr and not SpecialType.System_UIntPtr } when type.SpecialType != SpecialType.System_Collections_Generic_IEnumerable_T:
                return null;
            case { SpecialType: SpecialType.System_IntPtr or SpecialType.System_UIntPtr }:
                return $"{display} is a native handle";
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                return "pointers cannot cross the wire";
            case { TypeKind: TypeKind.Enum }:
                return null;
            case { TypeKind: TypeKind.Delegate }:
                return $"{type.ToDisplayString()} is a delegate";
            case IArrayTypeSymbol array:
                return Why(array.ElementType, visiting);
            case INamedTypeSymbol named:
                return Named(named, display, visiting);
            default:
                return $"{type.ToDisplayString()} has no wire shape";
        }
    }

    private static string? Named(INamedTypeSymbol named, string display, HashSet<ITypeSymbol> visiting)
    {
        if (Simple.Contains(display))
        {
            return null;
        }

        if (display is "System.Nullable<T>" or "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>")
        {
            return Why(named.TypeArguments[0], visiting);
        }

        if (display is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
        {
            return null;
        }

        if (Sequences.Contains(display) || display == "System.Collections.Generic.IEnumerable<T>")
        {
            return Why(named.TypeArguments[0], visiting);
        }

        if (Maps.Contains(display))
        {
            return named.TypeArguments[0].SpecialType == SpecialType.System_String ? Why(named.TypeArguments[1], visiting) : "dictionaries need string keys";
        }

        if (named.ContainingNamespace?.ToDisplayString() is { } ns && (ns == "System.IO" || ns.StartsWith("System.IO.", StringComparison.Ordinal)))
        {
            return $"{named.ToDisplayString()} is a stream or file handle";
        }

        if (named.TypeKind == TypeKind.Interface)
        {
            return $"{named.ToDisplayString()} is an interface";
        }

        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct) || named.IsAbstract)
        {
            return $"{named.ToDisplayString()} has no wire shape";
        }

        // A POCO: every public instance property settable, and each of those DTO-able.
        if (!visiting.Add(named))
        {
            return null;
        }

        var properties = named.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public).ToList();
        if (properties.Count == 0)
        {
            return $"{named.ToDisplayString()} has no public properties";
        }

        foreach (var property in properties)
        {
            if (property.SetMethod is null || property.SetMethod.DeclaredAccessibility != Accessibility.Public)
            {
                return $"{named.ToDisplayString()}.{property.Name} has no public setter";
            }

            if (Why(property.Type, visiting) is { } why)
            {
                return $"{named.ToDisplayString()}.{property.Name}: {why}";
            }
        }

        if (named.GetMembers().OfType<IEventSymbol>().Any(e => e.DeclaredAccessibility == Accessibility.Public))
        {
            return $"{named.ToDisplayString()} has events";
        }

        return null;
    }
}
