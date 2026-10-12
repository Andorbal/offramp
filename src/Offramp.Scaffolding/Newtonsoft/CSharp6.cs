using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Offramp.Scaffolding.Newtonsoft;

/// <summary>Spelling values in the C# 6 subset generated probes are written in (they compile in legacy projects).</summary>
internal static class CSharp6
{
    /// <summary><c>global::</c>-qualified, with keywords for special types: valid in any namespace without usings.</summary>
    private static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static string Type(ITypeSymbol type) => type.ToDisplayString(Qualified);

    /// <summary>A regular string literal, ASCII only: everything else as <c>\uXXXX</c>.</summary>
    public static string String(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < ' ' || c > '~' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    /// <summary>A literal of a constant value of a special type, with the suffix its type needs.</summary>
    public static string? Literal(object? value) => value switch
    {
        null => "null",
        string text => String(text),
        bool flag => flag ? "true" : "false",
        char c => c is '\'' or '\\' or < ' ' or > '~' ? "'\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) + "'" : "'" + c + "'",
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
        double number => double.IsNaN(number) ? "double.NaN" : double.IsPositiveInfinity(number) ? "double.PositiveInfinity" : double.IsNegativeInfinity(number) ? "double.NegativeInfinity"
            : number == 0 && double.IsNegative(number) ? "-0.0" : number.ToString("R", CultureInfo.InvariantCulture) + "d",
        float number => float.IsNaN(number) ? "float.NaN" : number == 0 && float.IsNegative(number) ? "-0.0f" : number.ToString("R", CultureInfo.InvariantCulture) + "f",
        long number => number == long.MinValue ? "long.MinValue" : number.ToString(CultureInfo.InvariantCulture) + "L",
        ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
        uint number => number.ToString(CultureInfo.InvariantCulture) + "U",
        int number => number == int.MinValue ? "int.MinValue" : number.ToString(CultureInfo.InvariantCulture),
        short or ushort or byte or sbyte => "(" + Keyword(value) + ")" + Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>A method name or test name made of letters, digits, and underscores.</summary>
    public static string Identifier(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        var identifier = builder.ToString();
        return identifier.Length == 0 || char.IsAsciiDigit(identifier[0]) ? "_" + identifier : identifier;
    }

    private static string Keyword(object value) => value switch
    {
        short => "short",
        ushort => "ushort",
        byte => "byte",
        _ => "sbyte",
    };
}
