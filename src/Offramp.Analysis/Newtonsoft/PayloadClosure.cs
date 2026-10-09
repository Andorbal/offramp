using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>A member Newtonsoft.Json writes and reads by default, with the JSON name it uses.</summary>
internal sealed record PayloadMember(INamedTypeSymbol Owner, ISymbol Member, ITypeSymbol Type, string JsonName);

/// <summary>
/// What a payload contains, as Newtonsoft.Json's default contracts see it: element types of
/// arrays and collections, keys and values of dictionaries, and the public instance properties
/// and fields (and members with <c>[JsonProperty]</c>, without <c>[JsonIgnore]</c>) of object
/// types outside the framework, transitively.
/// </summary>
internal sealed class PayloadClosure
{
    private const int MaxDepth = 8;

    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _edges = new(SymbolEqualityComparer.Default);

    private PayloadClosure()
    {
    }

    /// <summary>Every type reached, roots included.</summary>
    public List<ITypeSymbol> Types { get; } = [];

    public List<PayloadMember> Members { get; } = [];

    public static PayloadClosure Of(IEnumerable<ITypeSymbol> roots)
    {
        var closure = new PayloadClosure();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<(ITypeSymbol Type, INamedTypeSymbol? From, int Depth)>(roots.Select(r => (r, (INamedTypeSymbol?)null, 0)));
        while (queue.TryDequeue(out var next))
        {
            var (type, from, depth) = next;
            if (from is not null && type is INamedTypeSymbol reached && IsObject(reached))
            {
                closure.Edge(from, reached);
            }

            if (!seen.Add(type) || depth > MaxDepth)
            {
                continue;
            }

            closure.Types.Add(type);
            foreach (var inner in Elements(type))
            {
                queue.Enqueue((inner, from, depth + 1));
            }

            if (type is not INamedTypeSymbol named || !IsObject(named))
            {
                continue;
            }

            foreach (var member in Serialized(named))
            {
                closure.Members.Add(member);
                queue.Enqueue((member.Type, named, depth + 1));
            }
        }

        return closure;
    }

    /// <summary>A type of the closure that contains itself, through members or collections.</summary>
    public INamedTypeSymbol? Recursive()
    {
        foreach (var start in _edges.Keys.OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal))
        {
            var stack = new Stack<INamedTypeSymbol>(_edges[start]);
            var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            while (stack.TryPop(out var current))
            {
                if (SymbolEqualityComparer.Default.Equals(current, start))
                {
                    return start;
                }

                if (visited.Add(current) && _edges.TryGetValue(current, out var targets))
                {
                    foreach (var target in targets)
                    {
                        stack.Push(target);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The key and value types of a dictionary, the element type of an array or collection, a nullable's underlying type.</summary>
    internal static IEnumerable<ITypeSymbol> Elements(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return [array.ElementType];
        }

        if (type is not INamedTypeSymbol named || type.SpecialType == SpecialType.System_String)
        {
            return [];
        }

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return [named.TypeArguments[0]];
        }

        if (Dictionary(named) is { } dictionary)
        {
            return [dictionary.TypeArguments[0], dictionary.TypeArguments[1]];
        }

        return Interfaces(named).FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) is { } enumerable
            ? [enumerable.TypeArguments[0]]
            : [];
    }

    /// <summary>The <c>IDictionary&lt;K,V&gt;</c> (or read-only one) a type is or implements.</summary>
    internal static INamedTypeSymbol? Dictionary(ITypeSymbol type) =>
        type is INamedTypeSymbol named
            ? Interfaces(named).FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IDictionary<TKey, TValue>"
                or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                or "System.Collections.Immutable.IImmutableDictionary<TKey, TValue>")
            : null;

    /// <summary>A type Newtonsoft.Json writes as an object of members (not a primitive, collection, or framework type).</summary>
    internal static bool IsObject(INamedTypeSymbol type) =>
        type.TypeKind is TypeKind.Class or TypeKind.Struct
        && type.SpecialType == SpecialType.None
        && !Elements(type).Any()
        && !IsFramework(type);

    private static bool IsFramework(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal) || ns.StartsWith("Newtonsoft.", StringComparison.Ordinal);
    }

    private static ImmutableArray<INamedTypeSymbol> Interfaces(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Interface ? type.AllInterfaces.Insert(0, type) : type.AllInterfaces;

    private static IEnumerable<PayloadMember> Serialized(INamedTypeSymbol type)
    {
        var members = new List<PayloadMember>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers().Where(m => !m.IsStatic && !m.IsImplicitlyDeclared))
            {
                var memberType = member switch
                {
                    IPropertySymbol { IsIndexer: false, GetMethod: not null } property => property.Type,
                    IFieldSymbol { AssociatedSymbol: null } field => field.Type,
                    _ => null,
                };
                var jsonProperty = Attribute(member, "Newtonsoft.Json.JsonPropertyAttribute");
                if (memberType is null || Attribute(member, "Newtonsoft.Json.JsonIgnoreAttribute") is not null
                    || member.DeclaredAccessibility != Accessibility.Public && jsonProperty is null
                    || members.Any(m => m.Member.Name == member.Name))
                {
                    continue;
                }

                var name = jsonProperty?.ConstructorArguments.FirstOrDefault().Value as string
                    ?? jsonProperty?.NamedArguments.FirstOrDefault(a => a.Key == "PropertyName").Value.Value as string
                    ?? member.Name;
                members.Add(new PayloadMember(type, member, memberType, name));
            }
        }

        return members.OrderBy(m => m.Member.Name, StringComparer.Ordinal);
    }

    internal static AttributeData? Attribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);

    private void Edge(INamedTypeSymbol from, INamedTypeSymbol to)
    {
        if (!_edges.TryGetValue(from, out var targets))
        {
            _edges[from] = targets = [];
        }

        if (!targets.Contains(to, SymbolEqualityComparer.Default))
        {
            targets.Add(to);
        }
    }
}
