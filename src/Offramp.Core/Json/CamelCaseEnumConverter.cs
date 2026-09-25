using System.Text.Json;
using System.Text.Json.Serialization;

namespace Offramp.Core.Json;

/// <summary>
/// Serializes an enum as its camelCase member name (<c>Warning</c> → <c>"warning"</c>)
/// and rejects integers, so JSON contracts never leak ordinal values.
/// </summary>
public sealed class CamelCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public CamelCaseEnumConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Serializes an enum as its kebab-case member name (<c>PerProject</c> → <c>"per-project"</c>).
/// </summary>
public sealed class KebabCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public KebabCaseEnumConverter()
        : base(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false)
    {
    }
}
