namespace Offramp.Core.Model;

/// <summary>
/// The fixed, colorblind-safe framework class palette shared by the terminal,
/// the graph, and the report (<c>docs/spec/01-cli-conventions.md</c>).
/// </summary>
public static class FrameworkPalette
{
    public const string Framework = "#E07A1F";
    public const string Standard = "#2B6CB0";
    public const string Modern = "#2F855A";
    public const string Dual = "#2C7A7B";

    /// <summary>The border of a project in a reference cycle.</summary>
    public const string Cycle = "#C53030";

    public static string Of(FrameworkClass frameworkClass) => frameworkClass switch
    {
        FrameworkClass.Framework => Framework,
        FrameworkClass.Standard => Standard,
        FrameworkClass.Modern => Modern,
        FrameworkClass.Dual => Dual,
        _ => throw new ArgumentOutOfRangeException(nameof(frameworkClass), frameworkClass, null),
    };
}
