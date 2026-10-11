using System.Globalization;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>
/// Newtonsoft.Json's camel casing (<c>StringUtils.ToCamelCase</c>) as each segment of rule
/// <c>OFR3703</c> does it. The differential test checks these against the real releases.
/// </summary>
internal static class CamelCase
{
    /// <summary>The camel-cased name under the behavior labeled <paramref name="label"/>.</summary>
    public static string For(string label, string name) => label switch
    {
        "old" => Convert(name, stopAfterLowerSecond: false, lowerBeforeSeparator: false),
        "lowercase-first-word-start" => Convert(name, stopAfterLowerSecond: true, lowerBeforeSeparator: false),
        "lowercase-all-caps-word" => Convert(name, stopAfterLowerSecond: true, lowerBeforeSeparator: true),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Not a label of OFR3703."),
    };

    private static string Convert(string name, bool stopAfterLowerSecond, bool lowerBeforeSeparator)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return name;
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            // 8.0.3: a second character that is not upper case ends the first word ("A B" stays "a B").
            if (stopAfterLowerSecond && i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }

            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
            {
                // 11.0.1: an upper-case word followed by a space is lowered whole ("URL VALUE" is "url VALUE").
                if (lowerBeforeSeparator && char.IsSeparator(chars[i + 1]))
                {
                    chars[i] = char.ToLower(chars[i], CultureInfo.InvariantCulture);
                }

                break;
            }

            chars[i] = char.ToLower(chars[i], CultureInfo.InvariantCulture);
        }

        return new string(chars);
    }
}
