using System.Globalization;

namespace Offramp.Reporting.Report;

/// <summary>Phrases shared by every rendering of the report, so the page, the Markdown, and the terminal agree.</summary>
public static class ReportText
{
    /// <summary>", down 1,200 since 2026-09-01" (framework-only lines against the first point), or "" with one point.</summary>
    public static string Change(ReportData report)
    {
        if (report.Series.Count < 2)
        {
            return "";
        }

        var change = report.Headline.FrameworkLocChange;
        var since = report.Series[0].CreatedAt[..Math.Min(10, report.Series[0].CreatedAt.Length)];
        return change switch
        {
            < 0 => ", down " + (-change).ToString("N0", CultureInfo.InvariantCulture) + " since " + since,
            > 0 => ", up " + change.ToString("N0", CultureInfo.InvariantCulture) + " since " + since,
            _ => ", unchanged since " + since,
        };
    }

    public static string Fraction(int part, int whole) =>
        part.ToString(CultureInfo.InvariantCulture) + " of " + whole.ToString(CultureInfo.InvariantCulture);

    public static string Wire<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static string Count(int value, string noun) =>
        value.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (value == 1 ? "" : "s");

    /// <summary>2026-09-25T20:11:04Z as 2026-09-25 20:11.</summary>
    public static string Moment(string createdAt) =>
        createdAt.Length >= 16 ? createdAt[..10] + " " + createdAt[11..16] : createdAt;
}
