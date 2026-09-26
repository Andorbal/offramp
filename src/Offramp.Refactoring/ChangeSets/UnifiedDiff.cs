using System.Globalization;
using System.Text;

namespace Offramp.Refactoring.ChangeSets;

/// <summary>A unified diff (three lines of context) between two texts, compared line by line.</summary>
public static class UnifiedDiff
{
    private const int Context = 3;

    /// <param name="path">The repository-relative path shown in the headers.</param>
    /// <param name="before">The old text, or null for a new file.</param>
    /// <param name="after">The new text.</param>
    public static string Render(string path, string? before, string after)
    {
        var a = Lines(before ?? "");
        var b = Lines(after);
        var operations = Diff(a, b);
        if (operations.All(o => o.Kind == ' '))
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.Append("--- ").Append(before is null ? "/dev/null" : "a/" + path).Append('\n');
        builder.Append("+++ b/").Append(path).Append('\n');
        foreach (var hunk in Hunks(operations))
        {
            builder.Append(hunk);
        }

        return builder.ToString();
    }

    private static string[] Lines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private readonly record struct Operation(char Kind, string Line, int OldLine, int NewLine);

    /// <summary>Longest-common-subsequence diff; project and solution files are small.</summary>
    private static List<Operation> Diff(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<Operation>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                result.Add(new Operation(' ', a[x], x + 1, y + 1));
                x++;
                y++;
            }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                // Removals before additions, as git prints a replaced line.
                result.Add(new Operation('-', a[x], x + 1, y));
                x++;
            }
            else
            {
                result.Add(new Operation('+', b[y], x, y + 1));
                y++;
            }
        }

        return result;
    }

    private static IEnumerable<string> Hunks(List<Operation> operations)
    {
        var changed = operations.Select((o, i) => (o, i)).Where(p => p.o.Kind != ' ').Select(p => p.i).ToList();
        var index = 0;
        while (index < changed.Count)
        {
            var start = Math.Max(0, changed[index] - Context);
            var end = changed[index];
            while (index + 1 < changed.Count && changed[index + 1] - end <= Context * 2)
            {
                index++;
                end = changed[index];
            }

            end = Math.Min(operations.Count - 1, end + Context);
            index++;
            var slice = operations.GetRange(start, end - start + 1);
            var oldCount = slice.Count(o => o.Kind != '+');
            var newCount = slice.Count(o => o.Kind != '-');
            var oldStart = oldCount == 0 ? slice[0].OldLine : slice.First(o => o.Kind != '+').OldLine;
            var newStart = newCount == 0 ? slice[0].NewLine : slice.First(o => o.Kind != '-').NewLine;
            var builder = new StringBuilder();
            builder.Append(string.Create(CultureInfo.InvariantCulture, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n"));
            foreach (var operation in slice)
            {
                builder.Append(operation.Kind).Append(operation.Line).Append('\n');
            }

            yield return builder.ToString();
        }
    }
}
