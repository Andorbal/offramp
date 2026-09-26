using System.Text;

namespace Offramp.Scaffolding.Text;

/// <summary>
/// Edits to one text, applied in a single pass: replacements, removals, and insertions by
/// absolute position. An edit that starts inside a span an earlier edit replaced is dropped
/// (a removed statement takes the edits inside it along). Insertions at the same position keep
/// their <c>Order</c>, so a closing <c>#endif</c> lands before the next opening <c>#if</c>.
/// </summary>
internal sealed class TextEdits
{
    private readonly List<(int Start, int End, string Text, int Order)> _edits = [];

    public void Replace(int start, int end, string text) => _edits.Add((start, end, text, 1));

    public void Remove(int start, int end) => _edits.Add((start, end, "", 1));

    /// <summary>Text that closes something before <paramref name="position"/> (applied first there).</summary>
    public void InsertClosing(int position, string text) => _edits.Add((position, position, text, 0));

    /// <summary>Text that opens something at <paramref name="position"/> (applied last there).</summary>
    public void InsertOpening(int position, string text) => _edits.Add((position, position, text, 2));

    /// <summary>Applies the edits that fall inside [<paramref name="start"/>, <paramref name="end"/>) to that slice of <paramref name="text"/>.</summary>
    public string Apply(string text, int start, int end)
    {
        var result = new StringBuilder();
        var cursor = start;
        foreach (var edit in _edits.Where(e => e.Start >= start && e.End <= end).OrderBy(e => e.Start).ThenBy(e => e.Order).ThenBy(e => e.End))
        {
            if (edit.Start < cursor)
            {
                continue;
            }

            result.Append(text, cursor, edit.Start - cursor).Append(edit.Text);
            cursor = edit.End;
        }

        return result.Append(text, cursor, end - cursor).ToString();
    }
}
