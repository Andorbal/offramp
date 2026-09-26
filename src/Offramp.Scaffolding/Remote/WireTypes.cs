using Microsoft.CodeAnalysis;
using Offramp.Analysis.Seams;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Remote;

/// <summary>
/// The data-only copies (DTOs) a boundary needs in the contracts project, and the C#
/// expressions that convert between them and the original types. Simple types cross as
/// they are; enums and POCOs get copies named <c>NameDto</c>; sequences cross as
/// <c>List&lt;T&gt;</c> and string-keyed maps as <c>Dictionary&lt;string, T&gt;</c>.
/// </summary>
internal sealed class WireTypes(string contractsNamespace, string mapper)
{
    private static readonly HashSet<string> Simple = new(StringComparer.Ordinal)
    {
        "System.DateTime", "System.DateTimeOffset", "System.TimeSpan", "System.Guid", "System.Uri",
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

    private readonly Dictionary<INamedTypeSymbol, string> _names = new(SymbolEqualityComparer.Default);

    public string Namespace => contractsNamespace;

    /// <summary>The enums and POCOs with copies, ordered by the original's full name.</summary>
    public IReadOnlyList<(INamedTypeSymbol Type, string Dto)> Dtos =>
        [.. _names.OrderBy(n => CSharpText.Type(n.Key), StringComparer.Ordinal).Select(n => (n.Key, n.Value))];

    /// <summary>Task and ValueTask unwrapped: the type that crosses, or null for none.</summary>
    public static ITypeSymbol? Unwrap(ITypeSymbol type, out bool async)
    {
        var display = type.OriginalDefinition.ToDisplayString();
        async = display is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask"
            or "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>";
        return display switch
        {
            "System.Threading.Tasks.Task<TResult>" or "System.Threading.Tasks.ValueTask<TResult>" => ((INamedTypeSymbol)type).TypeArguments[0],
            "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" => null,
            _ => type.SpecialType == SpecialType.System_Void ? null : type,
        };
    }

    public static bool IsCancellationToken(ITypeSymbol type) => type.ToDisplayString() == "System.Threading.CancellationToken";

    /// <summary>Registers the enums and POCOs inside a type, adding what generation cannot handle to <paramref name="problems"/>.</summary>
    public void Collect(ITypeSymbol type, List<string> problems)
    {
        if (NullableOf(type) is { } inner)
        {
            Collect(inner, problems);
            return;
        }

        if (IsSimple(type))
        {
            return;
        }

        if (ElementOf(type) is { } element)
        {
            Collect(element, problems);
            return;
        }

        if (ValueOf(type) is { } value)
        {
            Collect(value, problems);
            return;
        }

        if (type is not INamedTypeSymbol named || _names.ContainsKey(named))
        {
            return;
        }

        if (named.TypeKind == TypeKind.Enum)
        {
            _names[named] = "";
            return;
        }

        if (named.IsGenericType)
        {
            problems.Add($"{CSharpText.Type(named)} is generic; DTOs are generated for non-generic types only");
            return;
        }

        if (named.TypeKind == TypeKind.Class && !named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            problems.Add($"{CSharpText.Type(named)} has no public parameterless constructor");
            return;
        }

        _names[named] = "";
        foreach (var property in Properties(named))
        {
            var why = property.SetMethod is not { DeclaredAccessibility: Accessibility.Public } ? "no public setter" : WireFriendliness.Why(property.Type);
            if (why is not null)
            {
                problems.Add($"{CSharpText.Type(named)}.{property.Name}: {why}");
                continue;
            }

            Collect(property.Type, problems);
        }
    }

    /// <summary>Names the copies once everything is collected: <c>NameDto</c>, numbered when two types share a name.</summary>
    public void AssignNames()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in _names.Keys.OrderBy(CSharpText.Type, StringComparer.Ordinal).ToList())
        {
            var name = type.Name + "Dto";
            for (var i = 2; !taken.Add(name); i++)
            {
                name = type.Name + "Dto" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            _names[type] = name;
        }
    }

    /// <summary>Public instance properties with a public getter, base class first; a redeclared name keeps the most derived.</summary>
    public static IReadOnlyList<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
        {
            chain.Insert(0, current);
        }

        var byName = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var property in chain.SelectMany(t => t.GetMembers().OfType<IPropertySymbol>()))
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public || property.GetMethod is null)
            {
                continue;
            }

            if (!byName.ContainsKey(property.Name))
            {
                order.Add(property.Name);
            }

            byName[property.Name] = property;
        }

        return [.. order.Select(n => byName[n])];
    }

    /// <summary>The type as the contracts project spells it.</summary>
    public string WireType(ITypeSymbol type)
    {
        if (NullableOf(type) is { } inner)
        {
            return inner is INamedTypeSymbol { TypeKind: TypeKind.Enum } ? Dto(inner) + "?" : IsSimple(inner) ? CSharpText.Type(type) : Dto(inner);
        }

        if (IsSimple(type))
        {
            return CSharpText.Type(type);
        }

        if (ElementOf(type) is { } element)
        {
            return $"System.Collections.Generic.List<{WireType(element)}>";
        }

        return ValueOf(type) is { } value ? $"System.Collections.Generic.Dictionary<string, {WireType(value)}>" : Dto(type);
    }

    /// <summary>Converts <paramref name="expression"/> (an original value, evaluated once) to its wire form.</summary>
    public string ToWire(ITypeSymbol type, string expression, int depth = 0)
    {
        if (NullableOf(type) is { } inner)
        {
            return inner is INamedTypeSymbol { TypeKind: TypeKind.Enum } ? $"(({Dto(inner)}?)({expression}))" : IsSimple(inner) ? expression : $"{mapper}.ToWire({expression})";
        }

        if (IsSimple(type))
        {
            return expression;
        }

        var e = "e" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ElementOf(type) is { } element)
        {
            return $"{mapper}.MapList({expression}, {e} => {ToWire(element, e, depth + 1)})";
        }

        if (ValueOf(type) is { } value)
        {
            return $"{mapper}.MapDictionary({expression}, {e} => {ToWire(value, e, depth + 1)})";
        }

        return type.TypeKind == TypeKind.Enum ? $"(({Dto(type)})({expression}))" : $"{mapper}.ToWire({expression})";
    }

    /// <summary>Converts <paramref name="expression"/> (a wire value, evaluated once) back to the original type.</summary>
    public string FromWire(ITypeSymbol type, string expression, int depth = 0)
    {
        if (NullableOf(type) is { } inner)
        {
            return inner is INamedTypeSymbol { TypeKind: TypeKind.Enum } ? $"(({CSharpText.Type(type)})({expression}))" : IsSimple(inner) ? expression : $"{mapper}.FromWireNullable({expression})";
        }

        if (IsSimple(type))
        {
            return expression;
        }

        var e = "e" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ElementOf(type) is { } element)
        {
            var display = type.OriginalDefinition.ToDisplayString();
            var helper = type is IArrayTypeSymbol ? "MapArray" : display is "System.Collections.Generic.HashSet<T>" or "System.Collections.Generic.ISet<T>" ? "MapSet" : "MapList";
            return $"{mapper}.{helper}({expression}, {e} => {FromWire(element, e, depth + 1)})";
        }

        if (ValueOf(type) is { } value)
        {
            return $"{mapper}.MapDictionary({expression}, {e} => {FromWire(value, e, depth + 1)})";
        }

        return type.TypeKind == TypeKind.Enum ? $"(({CSharpText.Type(type)})({expression}))" : $"{mapper}.FromWire({expression})";
    }

    private string Dto(ITypeSymbol type) =>
        type is INamedTypeSymbol named && _names.TryGetValue(named, out var name) ? contractsNamespace + "." + name : CSharpText.Type(type);

    private static ITypeSymbol? NullableOf(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : null;

    public static bool IsSimple(ITypeSymbol type) =>
        type.SpecialType is not (SpecialType.None or SpecialType.System_Object or SpecialType.System_IntPtr or SpecialType.System_UIntPtr
            or SpecialType.System_Collections_Generic_IEnumerable_T or SpecialType.System_Collections_Generic_IList_T
            or SpecialType.System_Collections_Generic_ICollection_T or SpecialType.System_Collections_Generic_IReadOnlyList_T
            or SpecialType.System_Collections_Generic_IReadOnlyCollection_T or SpecialType.System_Void)
        || Simple.Contains(type.OriginalDefinition.ToDisplayString());

    private static ITypeSymbol? ElementOf(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol { Rank: 1 } array => array.ElementType,
        INamedTypeSymbol { IsGenericType: true } named when Sequences.Contains(named.OriginalDefinition.ToDisplayString()) => named.TypeArguments[0],
        _ => null,
    };

    private static ITypeSymbol? ValueOf(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && Maps.Contains(named.OriginalDefinition.ToDisplayString()) && named.TypeArguments[0].SpecialType == SpecialType.System_String
            ? named.TypeArguments[1]
            : null;
}
