using System.Text.Json.Serialization;
using Offramp.Core.Json;

namespace Offramp.Core.Diagnostics;

/// <summary>Severity of a diagnostic. Ordered: a higher value is more severe.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<Severity>))]
public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// The <c>--fail-on</c> threshold: the lowest severity that makes a command exit 1.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<FailOn>))]
public enum FailOn
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Never = 3,
}

public static class SeverityExtensions
{
    public static bool Meets(this Severity severity, FailOn threshold) =>
        threshold != FailOn.Never && (int)severity >= (int)threshold;

    public static string ToWire(this Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Warning => "warning",
        Severity.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(severity)),
    };
}
