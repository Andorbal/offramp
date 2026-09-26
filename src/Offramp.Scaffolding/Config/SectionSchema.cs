using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Offramp.Scaffolding.Config;

/// <summary>How a configuration property maps to appsettings.json.</summary>
public enum SettingKind
{
    /// <summary>A value the JSON holds and the binder converts: string, bool, numbers, TimeSpan, Guid, DateTime, Uri, enums.</summary>
    Value,

    /// <summary>A nested <c>ConfigurationElement</c>: a JSON object and a nested options class.</summary>
    Element,

    /// <summary>No faithful JSON form (an element collection, a custom TypeConverter, an unknown type): OFR4401.</summary>
    Unsupported,
}

/// <summary>One <c>[ConfigurationProperty]</c> of a section or element class.</summary>
public sealed record SettingProperty
{
    /// <summary>The XML attribute or child element name.</summary>
    public required string XmlName { get; init; }

    /// <summary>The C# property name, which is also the JSON key the binder matches.</summary>
    public required string Name { get; init; }

    public required SettingKind Kind { get; init; }

    /// <summary>The property's type as C# writes it (fully qualified), for the options class.</summary>
    public required string TypeName { get; init; }

    /// <summary>The special type or <c>TimeSpan</c>/<c>Guid</c>/<c>DateTime</c>/<c>Uri</c>/<c>enum</c>, for parsing XML values.</summary>
    public required string ValueType { get; init; }

    /// <summary>The <c>DefaultValue</c> as written in the attribute, else null.</summary>
    public object? DefaultValue { get; init; }

    /// <summary>Why the property has no JSON form, for <see cref="SettingKind.Unsupported"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>The nested element's schema, for <see cref="SettingKind.Element"/>.</summary>
    public SectionSchema? Element { get; init; }
}

/// <summary>
/// The shape of a <c>ConfigurationSection</c> or <c>ConfigurationElement</c> class, read from
/// its <c>[ConfigurationProperty]</c> attributes with the semantic model.
/// </summary>
public sealed record SectionSchema
{
    /// <summary>The class, fully qualified.</summary>
    public required string TypeName { get; init; }

    /// <summary>The generated options class name: the class name without Section or Element, plus Options.</summary>
    public required string OptionsName { get; init; }

    public required IReadOnlyList<SettingProperty> Properties { get; init; }

    public static SectionSchema From(INamedTypeSymbol type) => From(type, depth: 0);

    private static SectionSchema From(INamedTypeSymbol type, int depth)
    {
        var properties = new List<SettingProperty>();
        foreach (var property in Members(type))
        {
            var attribute = property.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Configuration.ConfigurationPropertyAttribute");
            if (attribute is null || attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is not string xmlName || xmlName.Length == 0)
            {
                continue;
            }

            var defaultValue = attribute.NamedArguments.FirstOrDefault(a => a.Key == "DefaultValue").Value;
            var propertyType = property.Type;
            var converter = property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.TypeConverterAttribute");
            var (kind, valueType, reason) = Classify(propertyType, converter, depth);
            properties.Add(new SettingProperty
            {
                XmlName = xmlName,
                Name = property.Name,
                Kind = kind,
                TypeName = propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes)),
                ValueType = valueType,
                DefaultValue = defaultValue.IsNull ? null : defaultValue.Value,
                Reason = reason,
                Element = kind == SettingKind.Element ? From((INamedTypeSymbol)propertyType, depth + 1) : null,
            });
        }

        return new SectionSchema { TypeName = type.ToDisplayString(), OptionsName = OptionsNameOf(type.Name), Properties = properties };
    }

    /// <summary>Parses an XML value as the property's type; the JSON value (bool, number, or string), or null when it does not parse.</summary>
    public static object? Parse(SettingProperty property, string text)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (property.ValueType)
        {
            case "string" or "enum" or "Uri":
                return text;
            case "bool":
                return bool.TryParse(text, out var flag) ? flag : null;
            case "int" or "short" or "byte" or "sbyte" or "ushort":
                return int.TryParse(text, NumberStyles.Integer, culture, out var integer) ? integer : null;
            case "long" or "uint" or "ulong":
                return long.TryParse(text, NumberStyles.Integer, culture, out var wide) ? wide : null;
            case "double" or "float":
                return double.TryParse(text, NumberStyles.Float, culture, out var real) ? real : null;
            case "decimal":
                return decimal.TryParse(text, NumberStyles.Number, culture, out var money) ? money : null;
            case "TimeSpan":
                return TimeSpan.TryParse(text, culture, out var span) ? span.ToString("c", culture) : null;
            case "Guid":
                return Guid.TryParse(text, out var guid) ? guid.ToString("D") : null;
            case "DateTime":
                return DateTime.TryParse(text, culture, DateTimeStyles.RoundtripKind, out var date) ? date.ToString("o", culture) : null;
            default:
                return null;
        }
    }

    private static (SettingKind Kind, string ValueType, string? Reason) Classify(ITypeSymbol type, bool converter, int depth)
    {
        if (converter)
        {
            return (SettingKind.Unsupported, "", "it uses a custom TypeConverter.");
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return (SettingKind.Value, "enum", null);
        }

        var special = type.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Int32 => "int",
            SpecialType.System_Int16 => "short",
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Double => "double",
            SpecialType.System_Single => "float",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_DateTime => "DateTime",
            _ => null,
        };
        if (special is not null)
        {
            return (SettingKind.Value, special, null);
        }

        switch (type.ToDisplayString())
        {
            case "System.TimeSpan":
                return (SettingKind.Value, "TimeSpan", null);
            case "System.Guid":
                return (SettingKind.Value, "Guid", null);
            case "System.Uri":
                return (SettingKind.Value, "Uri", null);
        }

        if (DerivesFrom(type, "System.Configuration.ConfigurationElementCollection"))
        {
            return (SettingKind.Unsupported, "", "it is an element collection.");
        }

        if (DerivesFrom(type, "System.Configuration.ConfigurationElement") && type is INamedTypeSymbol && depth < 8)
        {
            return (SettingKind.Element, "", null);
        }

        return (SettingKind.Unsupported, "", $"{type.ToDisplayString()} has no JSON form.");
    }

    /// <summary>Instance properties, the declaring class's first, then its bases' (the binder sees them all).</summary>
    private static IEnumerable<IPropertySymbol> Members(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.ToDisplayString() is not ("System.Configuration.ConfigurationSection" or "System.Configuration.ConfigurationElement" or "object"); current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().Where(p => !p.IsStatic && !p.IsIndexer))
            {
                yield return property;
            }
        }
    }

    private static bool DerivesFrom(ITypeSymbol type, string name)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == name)
            {
                return true;
            }
        }

        return false;
    }

    public static string OptionsNameOf(string className)
    {
        foreach (var suffix in new[] { "Section", "Element", "Configuration", "Settings" })
        {
            if (className.Length > suffix.Length && className.EndsWith(suffix, StringComparison.Ordinal))
            {
                return className[..^suffix.Length] + "Options";
            }
        }

        return className + "Options";
    }
}
