using System.Text;

namespace Offramp.Refactoring.Conditional;

/// <summary>
/// A source file as lines that keep their own line endings, so edits that insert or remove
/// whole lines leave every other byte (including the byte order mark) as it was.
/// </summary>
internal sealed class SourceLines
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private SourceLines(byte[] bytes, bool bom, List<string> lines)
    {
        Bytes = bytes;
        HasBom = bom;
        Lines = lines;
    }

    public byte[] Bytes { get; }

    public bool HasBom { get; }

    /// <summary>Each line with its terminator (the last may have none).</summary>
    public List<string> Lines { get; }

    public string Text => string.Concat(Lines);

    /// <summary>The file's line ending: the first one found, else LF.</summary>
    public string NewLine => Lines.FirstOrDefault(l => l.EndsWith('\n')) is { } line && line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    public static SourceLines Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.AsSpan().StartsWith(Bom);
        var text = new UTF8Encoding(false).GetString(bom ? bytes[3..] : bytes);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return new SourceLines(bytes, bom, lines);
    }

    public byte[] Encode(IEnumerable<string> lines)
    {
        var body = new UTF8Encoding(false).GetBytes(string.Concat(lines));
        return HasBom ? [.. Bom, .. body] : body;
    }
}
