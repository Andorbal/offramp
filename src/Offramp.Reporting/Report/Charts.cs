using System.Globalization;
using System.Net;
using System.Text;
using Offramp.Core.Model;

namespace Offramp.Reporting.Report;

/// <summary>
/// Server-side SVG charts for the report: no scripts, invariant number formatting,
/// and colors from <see cref="FrameworkPalette"/>, so the same data always draws
/// the same bytes. Text and grid colors come from the page's CSS (light and dark).
/// </summary>
public static class Charts
{
    private const int Width = 720;

    public static string Color(string frameworkClass) => frameworkClass switch
    {
        "framework" => FrameworkPalette.Framework,
        "standard" => FrameworkPalette.Standard,
        "modern" => FrameworkPalette.Modern,
        "dual" => FrameworkPalette.Dual,
        _ => throw new ArgumentOutOfRangeException(nameof(frameworkClass), frameworkClass, null),
    };

    /// <summary>
    /// Lines of code by framework class at each point, stacked with framework at the
    /// bottom and its top edge drawn as the burn-down line. The x axis is time.
    /// </summary>
    public static string BurnDown(IReadOnlyList<ReportPoint> series)
    {
        const int height = 280, left = 64, right = 16, top = 16, bottom = 40;
        const int plotWidth = Width - left - right, plotHeight = height - top - bottom;
        var (max, step) = Scale(series.Max(p => p.Loc));
        var times = series.Select(p => ParseTime(p.CreatedAt)).ToList();
        var span = (times[^1] - times[0]).TotalSeconds;
        double X(int i) => series.Count == 1 ? left + (plotWidth / 2.0)
            : span <= 0 ? left + (plotWidth * i / (double)(series.Count - 1))
            : left + (plotWidth * (times[i] - times[0]).TotalSeconds / span);
        double Y(double value) => top + plotHeight - (plotHeight * value / max);

        var svg = Open(height, "Lines of code by framework class at each scan");
        Grid(svg, max, step, left, Width - right, Y);

        if (series.Count == 1)
        {
            StackedColumn(svg, series[0], X(0), Y);
        }
        else
        {
            var baseline = new double[series.Count];
            foreach (var name in ReportBuilder.Classes)
            {
                var upper = series.Select((p, i) => baseline[i] + p.ByFrameworkClass[name].Loc).ToArray();
                var d = new StringBuilder();
                for (var i = 0; i < series.Count; i++)
                {
                    d.Append(i == 0 ? 'M' : 'L').Append(N(X(i))).Append(' ').Append(N(Y(upper[i]))).Append(' ');
                }

                for (var i = series.Count - 1; i >= 0; i--)
                {
                    d.Append('L').Append(N(X(i))).Append(' ').Append(N(Y(baseline[i]))).Append(' ');
                }

                svg.Append("<path class=\"band\" d=\"").Append(d.Append('Z')).Append("\" fill=\"").Append(Color(name)).Append("\"><title>")
                    .Append(name).Append("</title></path>\n");
                baseline = upper;
            }

            var line = string.Join(" ", series.Select((p, i) => N(X(i)) + "," + N(Y(p.ByFrameworkClass["framework"].Loc))));
            svg.Append("<polyline class=\"burn\" points=\"").Append(line).Append("\" fill=\"none\" stroke=\"").Append(FrameworkPalette.Framework).Append("\"/>\n");
            for (var i = 0; i < series.Count; i++)
            {
                var framework = series[i].ByFrameworkClass["framework"].Loc;
                svg.Append("<circle cx=\"").Append(N(X(i))).Append("\" cy=\"").Append(N(Y(framework))).Append("\" r=\"3\" fill=\"")
                    .Append(FrameworkPalette.Framework).Append("\"><title>").Append(Describe(series[i])).Append("</title></circle>\n");
            }
        }

        TimeLabels(svg, series, X, height - bottom + 18);
        return Close(svg);
    }

    /// <summary>One horizontal bar per area, split by framework class, scaled to the largest area.</summary>
    public static string ByArea(IReadOnlyList<ReportArea> areas)
    {
        const int labelWidth = 200, valueWidth = 84, row = 26, bar = 16, top = 8;
        var height = top * 2 + Math.Max(1, areas.Count) * row;
        var plotWidth = Width - labelWidth - valueWidth;
        var max = Math.Max(1, areas.Count == 0 ? 0 : areas.Max(a => a.Loc));
        var svg = Open(height, "Lines of code by framework class in each area");
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var y = top + (i * row);
            svg.Append("<text class=\"label\" x=\"").Append(labelWidth - 8).Append("\" y=\"").Append(N(y + (bar / 2.0) + 4))
                .Append("\" text-anchor=\"end\"><title>").Append(Encode(area.Area)).Append("</title>").Append(Encode(Truncate(area.Area, 30)))
                .Append("</text>\n");
            double x = labelWidth;
            foreach (var name in ReportBuilder.Classes)
            {
                var totals = area.ByFrameworkClass[name];
                if (totals.Loc == 0)
                {
                    continue;
                }

                var w = plotWidth * totals.Loc / (double)max;
                svg.Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(y).Append("\" width=\"").Append(N(w)).Append("\" height=\"").Append(bar)
                    .Append("\" fill=\"").Append(Color(name)).Append("\"><title>").Append(Encode(area.Area)).Append(": ").Append(name).Append(", ")
                    .Append(ReportText.Count(totals.Projects, "project")).Append(", ").Append(ReportText.Count(totals.Loc, "line")).Append("</title></rect>\n");
                x += w;
            }

            svg.Append("<text class=\"value\" x=\"").Append(N(x + 6)).Append("\" y=\"").Append(N(y + (bar / 2.0) + 4)).Append("\">")
                .Append(Compact(area.Loc)).Append("</text>\n");
        }

        return Close(svg);
    }

    /// <summary>A round axis maximum at or above <paramref name="value"/> and a 1-2-5 tick step giving four or five ticks.</summary>
    internal static (double Max, double Step) Scale(int value)
    {
        if (value <= 0)
        {
            return (1, 1);
        }

        var raw = value / 4.0;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var residual = raw / magnitude;
        var step = (residual <= 1 ? 1 : residual <= 2 ? 2 : residual <= 5 ? 5 : 10) * magnitude;
        return (Math.Ceiling(value / step) * step, step);
    }

    /// <summary>12, 1.2k, 12k, 1.2M: axis and bar labels.</summary>
    internal static string Compact(double value) => value switch
    {
        >= 1_000_000 => N(value / 1_000_000) + "M",
        >= 1_000 => N(value / 1_000) + "k",
        _ => N(value),
    };

    private static void StackedColumn(StringBuilder svg, ReportPoint point, double x, Func<double, double> y)
    {
        const int width = 48;
        double baseline = 0;
        foreach (var name in ReportBuilder.Classes)
        {
            var loc = point.ByFrameworkClass[name].Loc;
            if (loc == 0)
            {
                continue;
            }

            svg.Append("<rect x=\"").Append(N(x - (width / 2.0))).Append("\" y=\"").Append(N(y(baseline + loc))).Append("\" width=\"").Append(width)
                .Append("\" height=\"").Append(N(y(baseline) - y(baseline + loc))).Append("\" fill=\"").Append(Color(name)).Append("\"><title>")
                .Append(name).Append(": ").Append(ReportText.Count(loc, "line")).Append("</title></rect>\n");
            baseline += loc;
        }
    }

    private static void Grid(StringBuilder svg, double max, double step, int from, int to, Func<double, double> y)
    {
        for (var value = 0.0; value <= max + (step / 2); value += step)
        {
            svg.Append("<line class=\"grid\" x1=\"").Append(from).Append("\" x2=\"").Append(to).Append("\" y1=\"").Append(N(y(value)))
                .Append("\" y2=\"").Append(N(y(value))).Append("\"/>\n");
            svg.Append("<text class=\"axis\" x=\"").Append(from - 8).Append("\" y=\"").Append(N(y(value) + 4)).Append("\" text-anchor=\"end\">")
                .Append(Compact(value)).Append("</text>\n");
        }
    }

    /// <summary>At most six date labels, first and last always, none closer than 90 units to its neighbors.</summary>
    private static void TimeLabels(StringBuilder svg, IReadOnlyList<ReportPoint> series, Func<int, double> x, int y)
    {
        var candidates = Enumerable.Range(0, 6)
            .Select(k => series.Count == 1 ? 0 : (int)Math.Round(k * (series.Count - 1) / 5.0, MidpointRounding.AwayFromZero))
            .Distinct()
            .ToList();
        var last = double.NegativeInfinity;
        var lastIndex = series.Count - 1;
        foreach (var i in candidates)
        {
            var tooCloseToEnd = i != lastIndex && x(lastIndex) - x(i) < 90;
            if (x(i) - last < 90 || tooCloseToEnd)
            {
                continue;
            }

            // The ends align with the plot's edges so neither label is clipped.
            var anchor = series.Count == 1 ? "middle" : i == 0 ? "start" : i == lastIndex ? "end" : "middle";
            svg.Append("<text class=\"axis\" x=\"").Append(N(x(i))).Append("\" y=\"").Append(y).Append("\" text-anchor=\"").Append(anchor).Append("\">")
                .Append(series[i].CreatedAt[..Math.Min(10, series[i].CreatedAt.Length)]).Append("</text>\n");
            last = x(i);
        }
    }

    private static string Describe(ReportPoint point) =>
        ReportText.Moment(point.CreatedAt) + ": " + ReportText.Count(point.ByFrameworkClass["framework"].Loc, "framework line") + " of " + ReportText.Count(point.Loc, "line");

    private static StringBuilder Open(int height, string label) => new StringBuilder()
        .Append("<svg class=\"chart\" viewBox=\"0 0 ").Append(Width).Append(' ').Append(height).Append("\" role=\"img\" aria-label=\"")
        .Append(Encode(label)).Append("\">\n");

    private static string Close(StringBuilder svg) => svg.Append("</svg>").ToString();

    private static DateTimeOffset ParseTime(string createdAt) =>
        DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : DateTimeOffset.UnixEpoch;

    private static string Truncate(string value, int length) => value.Length <= length ? value : "…" + value[^(length - 1)..];

    private static string N(double value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
