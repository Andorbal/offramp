using System.Text;

namespace Offramp.Core.Output;

/// <summary>
/// Line-based unified diffs (Myers' algorithm) for dry-run previews. Output
/// uses <c>a/</c> and <c>b/</c> prefixes and <c>/dev/null</c> for created or
/// deleted files, like <c>git diff</c>.
/// </summary>
public static class UnifiedDiff
{
    public const string DevNull = "/dev/null";

    /// <summary>A diff from <paramref name="oldText"/> to <paramref name="newText"/>; empty when equal.</summary>
    public static string Create(string? oldPath, string? newPath, string oldText, string newText, int context = 3)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var edits = Diff(a, b);
        if (edits.All(e => e.Kind == EditKind.Equal))
        {
            return "";
        }

        var output = new StringBuilder();
        output.Append("--- ").Append(oldPath is null ? DevNull : "a/" + oldPath).Append('\n');
        output.Append("+++ ").Append(newPath is null ? DevNull : "b/" + newPath).Append('\n');
        foreach (var hunk in Hunks(edits, context))
        {
            output.Append(hunk);
        }

        return output.ToString();
    }

    public static string ForNewFile(string path, string content) => Create(null, path, "", content);

    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private enum EditKind
    {
        Equal,
        Delete,
        Insert,
    }

    private readonly record struct Edit(EditKind Kind, int OldIndex, int NewIndex, string Text);

    private static List<Edit> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        var max = n + m;
        var v = new int[(2 * max) + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]))
                {
                    x = v[k + 1 + max];
                }
                else
                {
                    x = v[k - 1 + max] + 1;
                }

                var y = x - k;
                while (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                v[k + max] = x;
                if (x >= n && y >= m)
                {
                    return Backtrack(trace, a, b, max, d);
                }
            }
        }

        return [];
    }

    private static List<Edit> Backtrack(List<int[]> trace, IReadOnlyList<string> a, IReadOnlyList<string> b, int max, int depth)
    {
        var edits = new List<Edit>();
        var x = a.Count;
        var y = b.Count;
        for (var d = depth; d > 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var prevK = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? k + 1 : k - 1;
            var prevX = v[prevK + max];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY)
            {
                edits.Add(new Edit(EditKind.Equal, x - 1, y - 1, a[x - 1]));
                x--;
                y--;
            }

            if (x == prevX)
            {
                edits.Add(new Edit(EditKind.Insert, x, y - 1, b[y - 1]));
            }
            else
            {
                edits.Add(new Edit(EditKind.Delete, x - 1, y, a[x - 1]));
            }

            x = prevX;
            y = prevY;
        }

        while (x > 0 && y > 0)
        {
            edits.Add(new Edit(EditKind.Equal, x - 1, y - 1, a[x - 1]));
            x--;
            y--;
        }

        edits.Reverse();
        return edits;
    }

    private static IEnumerable<string> Hunks(List<Edit> edits, int context)
    {
        var changeIndexes = edits.Select((e, i) => (e, i)).Where(t => t.e.Kind != EditKind.Equal).Select(t => t.i).ToList();
        var start = 0;
        while (start < changeIndexes.Count)
        {
            var end = start;
            while (end + 1 < changeIndexes.Count && changeIndexes[end + 1] - changeIndexes[end] <= (2 * context) + 1)
            {
                end++;
            }

            var from = Math.Max(0, changeIndexes[start] - context);
            var to = Math.Min(edits.Count - 1, changeIndexes[end] + context);
            yield return FormatHunk(edits, from, to);
            start = end + 1;
        }
    }

    private static string FormatHunk(List<Edit> edits, int from, int to)
    {
        var slice = edits.GetRange(from, to - from + 1);
        var oldCount = slice.Count(e => e.Kind != EditKind.Insert);
        var newCount = slice.Count(e => e.Kind != EditKind.Delete);
        var oldStart = slice.FirstOrDefault(e => e.Kind != EditKind.Insert) is { } firstOld && oldCount > 0 ? firstOld.OldIndex + 1 : StartWhenEmpty(slice, true);
        var newStart = slice.FirstOrDefault(e => e.Kind != EditKind.Delete) is { } firstNew && newCount > 0 ? firstNew.NewIndex + 1 : StartWhenEmpty(slice, false);

        var builder = new StringBuilder();
        builder.Append("@@ -").Append(Range(oldStart, oldCount)).Append(" +").Append(Range(newStart, newCount)).Append(" @@\n");
        foreach (var edit in slice)
        {
            builder.Append(edit.Kind switch { EditKind.Equal => ' ', EditKind.Delete => '-', _ => '+' });
            builder.Append(edit.Text).Append('\n');
        }

        return builder.ToString();
    }

    private static int StartWhenEmpty(List<Edit> slice, bool old)
    {
        // An empty side is reported at the line before the change (0 at the file start).
        var first = slice[0];
        return old ? first.OldIndex : first.NewIndex;
    }

    private static string Range(int start, int count) =>
        count == 1
            ? start.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{start},{count}");
}
