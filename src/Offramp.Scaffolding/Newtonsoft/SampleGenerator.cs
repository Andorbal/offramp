using System.Globalization;
using Microsoft.CodeAnalysis;
using Offramp.Analysis.Newtonsoft;

namespace Offramp.Scaffolding.Newtonsoft;

/// <summary>
/// Sample values written as C# 6 expressions (docs/spec/commands/newtonsoft.md, "Samples"):
/// <c>typical</c> sets everything, <c>empty</c> leaves references null and collections empty, and
/// <c>edge</c> uses the values Newtonsoft.Json releases disagree about. Values come from a seed
/// derived from the type and member names, so a type gets the same samples everywhere; dates are
/// never local; nesting stops at <see cref="ObjectDepth"/> with null.
/// </summary>
internal sealed class SampleGenerator(Compilation compilation, Func<ISymbol, bool> accessible, int objectDepth)
{
    public static readonly IReadOnlyList<string> Labels = ["typical", "empty", "edge"];

    private readonly Dictionary<ITypeSymbol, INamedTypeSymbol?> _implementations = new(SymbolEqualityComparer.Default);

    public int ObjectDepth => objectDepth;

    /// <summary>A value of the type for the label, or null when the type cannot be written.</summary>
    public string? Expression(ITypeSymbol type, string label) => Value(type, label, CSharp6.Type(type), 0);

    private string? Value(ITypeSymbol type, string label, string seed, int depth)
    {
        if (!Generatability.IsGeneratable(type, accessible))
        {
            return null;
        }

        var n = Seed(seed);
        switch (type)
        {
            case { SpecialType: SpecialType.System_Boolean }:
                return label == "empty" ? "false" : "true";
            case { SpecialType: SpecialType.System_String }:
                return label switch
                {
                    "empty" => "null",
                    "edge" => CSharp6.String("\u00dcnic\u00f8de \"quoted\" \\ tab\t\u2603"),
                    _ => CSharp6.String(Word(seed) + " " + (n % 100).ToString(CultureInfo.InvariantCulture)),
                };
            case { SpecialType: SpecialType.System_Char }:
                return label == "edge" ? "'\\u00e9'" : "'" + (char)('a' + n % 26) + "'";
            case { SpecialType: SpecialType.System_Decimal }:
                return label switch { "empty" => "0m", "edge" => "1.10m", _ => (n % 900 + 100).ToString(CultureInfo.InvariantCulture) + ".25m" };
            case { SpecialType: SpecialType.System_Double }:
                return label switch { "empty" => "0d", "edge" => "-0.0", _ => "0.1d" };
            case { SpecialType: SpecialType.System_Single }:
                return label switch { "empty" => "0f", "edge" => "-0.0f", _ => "0.5f" };
            case { SpecialType: SpecialType.System_DateTime }:
                return label switch
                {
                    "empty" => "default(global::System.DateTime)",
                    "edge" => "new global::System.DateTime(2024, 2, 29, 23, 59, 59, 999, global::System.DateTimeKind.Unspecified)",
                    _ => $"new global::System.DateTime(2024, 3, {n % 28 + 1}, 9, 30, 0, global::System.DateTimeKind.Utc)",
                };
            case { SpecialType: not SpecialType.None } special when IsIntegral(special.SpecialType):
                var keyword = CSharp6.Type(special);
                return label switch { "empty" => $"({keyword})0", "edge" => $"{keyword}.MaxValue", _ => $"({keyword}){n % 100 + 1}" };
            case INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration:
                return Enumeration(enumeration, label, n);
            case INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable:
                return label == "empty" ? "null" : Value(nullable.TypeArguments[0], label, seed, depth);
            case IArrayTypeSymbol array:
                return Collection(array.ElementType, label, seed, depth, items => $"new {CSharp6.Type(array.ElementType)}[] {{ {items} }}", $"new {CSharp6.Type(array.ElementType)}[0]");
            case INamedTypeSymbol named when ValueLike(named, label, n) is { } value:
                return value;
            case INamedTypeSymbol named when PayloadClosure.Dictionary(named) is { } dictionary:
                return Dictionary(named, dictionary, label, seed, depth);
            case INamedTypeSymbol named when PayloadClosure.Elements(named).FirstOrDefault() is { } element:
                var concrete = Concrete(named, "System.Collections.Generic.List", element);
                return Collection(element, label, seed, depth, items => $"new {concrete} {{ {items} }}", $"new {concrete}()");
            case INamedTypeSymbol named:
                return Object(named, label, seed, depth);
            default:
                return null;
        }
    }

    private static string Enumeration(INamedTypeSymbol type, string label, int n)
    {
        var members = type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
        var name = CSharp6.Type(type);
        return label switch
        {
            "empty" => $"default({name})",
            // A value with no name: what StringEnumConverter's integer handling is about.
            "edge" => $"({name})" + (members.Select(f => Convert.ToInt64(f.ConstantValue, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1).ToString(CultureInfo.InvariantCulture),
            _ => members.Count == 0 ? $"default({name})" : $"{name}.{members[n % members.Count].Name}",
        };
    }

    private static string? ValueLike(INamedTypeSymbol type, string label, int n) => type.ToDisplayString() switch
    {
        "System.DateTimeOffset" => label switch
        {
            "empty" => "default(global::System.DateTimeOffset)",
            "edge" => "new global::System.DateTimeOffset(2024, 2, 29, 23, 59, 59, global::System.TimeSpan.FromMinutes(-330))",
            _ => $"new global::System.DateTimeOffset(2024, 3, {n % 28 + 1}, 9, 30, 0, global::System.TimeSpan.FromHours(2))",
        },
        "System.TimeSpan" => label switch { "empty" => "global::System.TimeSpan.Zero", "edge" => "global::System.TimeSpan.FromTicks(1)", _ => "global::System.TimeSpan.FromMinutes(90)" },
        "System.Guid" => label switch
        {
            "empty" => "global::System.Guid.Empty",
            "edge" => "new global::System.Guid(\"ffffffff-ffff-ffff-ffff-ffffffffffff\")",
            _ => $"new global::System.Guid(\"{n:x8}-0000-4000-8000-000000000000\")",
        },
        "System.Uri" => label switch
        {
            "empty" => "null",
            "edge" => "new global::System.Uri(\"https://example.com/a b?c=\\u00e4&d=%20\", global::System.UriKind.RelativeOrAbsolute)",
            _ => $"new global::System.Uri(\"https://example.com/items/{n % 100}\")",
        },
        _ => null,
    };

    private string? Collection(ITypeSymbol element, string label, string seed, int depth, Func<string, string> filled, string empty)
    {
        if (label == "empty")
        {
            return empty;
        }

        var count = label == "edge" ? 1 : 2;
        var items = Enumerable.Range(0, count).Select(i => Value(element, label, seed + "[" + i + "]", depth + 1)).ToList();
        return items.Any(i => i is null) ? empty : filled(string.Join(", ", items));
    }

    private string? Dictionary(INamedTypeSymbol type, INamedTypeSymbol dictionary, string label, string seed, int depth)
    {
        var (key, value) = (dictionary.TypeArguments[0], dictionary.TypeArguments[1]);
        var concrete = Concrete(type, "System.Collections.Generic.Dictionary", key, value);
        if (label == "empty")
        {
            return $"new {concrete}()";
        }

        // Distinct keys: the enum's members in order, or values from different seeds.
        List<string?> keys = key is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration
            ? [.. enumeration.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Take(label == "edge" ? 1 : 2).Select(f => $"{CSharp6.Type(enumeration)}.{f.Name}")]
            : [.. Enumerable.Range(0, label == "edge" ? 1 : 2).Select(i => Value(key, i == 0 ? "typical" : "edge", seed + ".key" + i, depth + 1)).Distinct()];
        var entries = keys.Select((k, i) => (Key: k, Value: Value(value, label, seed + ".value" + i, depth + 1))).ToList();
        return entries.Any(e => e.Key is null || e.Value is null)
            ? $"new {concrete}()"
            : $"new {concrete} {{ {string.Join(", ", entries.Select(e => $"{{ {e.Key}, {e.Value} }}"))} }}";
    }

    private string? Object(INamedTypeSymbol type, string label, string seed, int depth)
    {
        if (type.IsAbstract)
        {
            return Implementation(type) is { } implementation ? Object(implementation, label, seed, depth) : null;
        }

        if (depth >= objectDepth)
        {
            return type.IsValueType ? $"default({CSharp6.Type(type)})" : "null";
        }

        var constructor = Generatability.Constructor(type, accessible);
        if (constructor is null && type.TypeKind != TypeKind.Struct)
        {
            return null;
        }

        // A bare null matches every overload that takes a reference; default(T) names the one chosen.
        var arguments = constructor?.Parameters.Select(p => Value(p.Type, label, CSharp6.Type(type) + "." + p.Name, depth + 1) is { } value and not "null"
            ? value : $"default({CSharp6.Type(p.Type)})") ?? [];
        var assigned = new List<string>();
        foreach (var member in Settable(type).Where(m => constructor is null || !constructor.Parameters.Any(p => string.Equals(p.Name, m.Name, StringComparison.OrdinalIgnoreCase))))
        {
            var memberType = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;
            if (Value(memberType, label, CSharp6.Type(type) + "." + member.Name, depth + 1) is { } value)
            {
                assigned.Add($"{member.Name} = {value}");
            }
        }

        var creation = $"new {CSharp6.Type(type)}({string.Join(", ", arguments)})";
        return assigned.Count == 0 ? creation : $"{creation} {{ {string.Join(", ", assigned)} }}";
    }

    /// <summary>Public instance properties with an accessible setter, and writable public fields, in name order.</summary>
    private IEnumerable<ISymbol> Settable(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers().Where(m => !m.IsStatic && accessible(m) && seen.Add(m.Name)))
            {
                if (member is IPropertySymbol { IsIndexer: false, SetMethod: { } setter } && accessible(setter) && !setter.IsInitOnly
                    || member is IFieldSymbol { IsReadOnly: false, IsConst: false, IsImplicitlyDeclared: false })
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>The one concrete, generatable type in the compilation that derives from or implements an abstract type; null when there are none or several.</summary>
    private INamedTypeSymbol? Implementation(INamedTypeSymbol type)
    {
        if (_implementations.TryGetValue(type, out var known))
        {
            return known;
        }

        var candidates = Types(compilation.Assembly.GlobalNamespace)
            .Where(t => !t.IsAbstract && t.TypeKind == TypeKind.Class && (Derives(t, type) || t.AllInterfaces.Contains(type, SymbolEqualityComparer.Default)))
            .Where(t => Generatability.IsGeneratable(t, accessible))
            .Take(2)
            .ToList();
        return _implementations[type] = candidates.Count == 1 ? candidates[0] : null;
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns) =>
        ns.GetTypeMembers().Concat(ns.GetNamespaceMembers().SelectMany(Types));

    private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The collection type to create: the type itself when it is concrete, else the given generic type over its type arguments.</summary>
    private static string Concrete(INamedTypeSymbol type, string fallback, params ITypeSymbol[] arguments) =>
        type.TypeKind == TypeKind.Class && !type.IsAbstract
            ? CSharp6.Type(type)
            : $"global::{(type.OriginalDefinition.ToDisplayString().Contains("Set<", StringComparison.Ordinal) ? "System.Collections.Generic.HashSet" : fallback)}<{string.Join(", ", arguments.Select(CSharp6.Type))}>";

    private static bool IsIntegral(SpecialType type) => type is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16
        or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64;

    /// <summary>A stable small number for a seed (FNV-1a), the same on every machine and run.</summary>
    private static int Seed(string seed)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in seed)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash % 10007u);
        }
    }

    private static string Word(string seed)
    {
        var name = seed[(seed.LastIndexOf('.') + 1)..];
        name = name.Contains('[', StringComparison.Ordinal) ? name[..name.IndexOf('[', StringComparison.Ordinal)] : name;
        return name.Length == 0 ? "value" : name;
    }
}
