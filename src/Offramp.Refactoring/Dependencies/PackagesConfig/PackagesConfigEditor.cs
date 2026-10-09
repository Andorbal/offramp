using System.Text;
using System.Xml;

namespace Offramp.Refactoring.Dependencies.PackagesConfig;

/// <summary>What a packages.config says about one package.</summary>
public sealed record PackagesConfigEntry(string Version, string? AllowedVersions);

/// <summary>
/// Changes a package's version in a packages.config without touching any other byte: the file is
/// read with an XML reader to find the <c>version</c> attribute's value, and only that value is
/// replaced (encoding, byte order mark, quotes, and whitespace kept).
/// </summary>
public static class PackagesConfigEditor
{
    public static PackagesConfigEntry? Find(byte[] content, string id) =>
        Locate(content, id) is { } found ? new PackagesConfigEntry(found.Version, found.AllowedVersions) : null;

    /// <summary>The file with the package at <paramref name="version"/>, or null when it does not list the package.</summary>
    public static byte[]? SetVersion(byte[] content, string id, string version)
    {
        if (Locate(content, id) is not { } found)
        {
            return null;
        }

        var (text, encoding, bom) = Decode(content);
        var updated = text[..found.Start] + version + text[(found.Start + found.Version.Length)..];
        return [.. bom, .. encoding.GetBytes(updated)];
    }

    private static (string Version, string? AllowedVersions, int Start)? Locate(byte[] content, string id)
    {
        var (text, _, _) = Decode(content);
        var lines = LineStarts(text);
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var info = (IXmlLineInfo)reader;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "package"
                || !string.Equals(reader.GetAttribute("id"), id, StringComparison.OrdinalIgnoreCase)
                || !reader.MoveToAttribute("version"))
            {
                continue;
            }

            // The reader puts an attribute at its name; its value follows the next quote.
            var start = lines[info.LineNumber - 1] + info.LinePosition - 1;
            var quote = text.IndexOfAny(['"', '\''], start);
            var version = reader.Value;
            reader.MoveToElement();
            return (version, reader.GetAttribute("allowedVersions"), quote + 1);
        }

        return null;
    }

    private static (string Text, Encoding Encoding, byte[] Bom) Decode(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return (new UTF8Encoding(false).GetString(content, 3, content.Length - 3), new UTF8Encoding(false), [0xEF, 0xBB, 0xBF]);
        }

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(content, 2, content.Length - 2), new UnicodeEncoding(false, false), [0xFF, 0xFE]);
        }

        return (new UTF8Encoding(false).GetString(content), new UTF8Encoding(false), []);
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }
}
