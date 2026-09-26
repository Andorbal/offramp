using System.Text;
using System.Xml;

namespace Offramp.Refactoring.Dependencies.Redirects;

/// <summary>A binding redirect in a configuration file, with where it sits in the text.</summary>
internal sealed record ExistingRedirect(
    string Assembly, string? PublicKeyToken, string? Culture, string? OldVersion, string? NewVersion,
    (int Start, int End) DependentAssembly, (int Start, int End)? BindingRedirect);

/// <summary>
/// Reads and edits the <c>&lt;assemblyBinding&gt;</c> section of an app.config or web.config
/// as text: each edit replaces only the characters of the entry it changes, so everything else
/// (comments, formatting, other sections, the byte order mark, line endings) stays byte for byte.
/// </summary>
internal sealed class ConfigBindings
{
    private const string BindingNamespace = "urn:schemas-microsoft-com:asm.v1";

    private readonly string _text;
    private readonly bool _bom;
    private readonly string _newline;
    private readonly int[] _lineStarts;
    private readonly List<(int Start, int End, string Replacement)> _edits = [];

    /// <summary>Where new entries go: just before <c>&lt;/assemblyBinding&gt;</c>, or null when the section is missing.</summary>
    private int? _bindingEnd;

    private string? _entryIndent;

    private int? _configurationEnd;

    private ConfigBindings(string text, bool bom)
    {
        _text = text;
        _bom = bom;
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lineStarts = [0, .. text.Select((c, i) => (c, i)).Where(p => p.c == '\n').Select(p => p.i + 1)];
        Parse();
    }

    public IReadOnlyList<ExistingRedirect> Redirects { get; private set; } = [];

    public static ConfigBindings Load(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return new ConfigBindings(Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    public void Change(ExistingRedirect redirect, string oldVersion, string newVersion)
    {
        var replacement = $"<bindingRedirect oldVersion=\"{oldVersion}\" newVersion=\"{newVersion}\" />";
        if (redirect.BindingRedirect is { } span)
        {
            _edits.Add((span.Start, span.End, replacement));
        }
    }

    /// <summary>Removes the entry and, when it has its line to itself, the line.</summary>
    public void Remove(ExistingRedirect redirect)
    {
        var (start, end) = redirect.DependentAssembly;
        var lineStart = _text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = _text.IndexOf('\n', end);
        if (_text[lineStart..start].Trim().Length == 0 && (lineEnd < 0 ? _text[end..] : _text[end..lineEnd]).Trim().Length == 0)
        {
            start = lineStart;
            end = lineEnd < 0 ? _text.Length : lineEnd + 1;
        }

        _edits.Add((start, end, ""));
    }

    public void Add(string assembly, string publicKeyToken, string culture, string oldVersion, string newVersion)
    {
        var indent = _entryIndent ?? "      ";
        var unit = indent.Length >= 2 && indent.All(c => c == ' ') ? "  " : "\t";
        var nl = _newline;
        var block = $"{indent}<dependentAssembly>{nl}"
            + $"{indent}{unit}<assemblyIdentity name=\"{assembly}\" publicKeyToken=\"{publicKeyToken}\" culture=\"{culture}\" />{nl}"
            + $"{indent}{unit}<bindingRedirect oldVersion=\"{oldVersion}\" newVersion=\"{newVersion}\" />{nl}"
            + $"{indent}</dependentAssembly>{nl}";
        if (_bindingEnd is { } at)
        {
            var lineStart = _text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
            var insertAt = _text[lineStart..at].Trim().Length == 0 ? lineStart : at;
            _edits.Add((insertAt, insertAt, insertAt == at ? nl + block : block));
            return;
        }

        if (_configurationEnd is { } configurationEnd)
        {
            var section = $"  <runtime>{nl}    <assemblyBinding xmlns=\"{BindingNamespace}\">{nl}{block.Replace(indent, "      ", StringComparison.Ordinal)}    </assemblyBinding>{nl}  </runtime>{nl}";
            _edits.Add((configurationEnd, configurationEnd, section));
        }
    }

    public byte[] Save()
    {
        var builder = new StringBuilder(_text);
        foreach (var (start, end, replacement) in _edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            builder.Remove(start, end - start).Insert(start, replacement);
        }

        var body = Encoding.UTF8.GetBytes(builder.ToString());
        return _bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    private void Parse()
    {
        var redirects = new List<ExistingRedirect>();
        using var reader = XmlReader.Create(new StringReader(_text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true });
        var info = (IXmlLineInfo)reader;
        var depth = new Stack<string>();
        (int Start, Dictionary<string, string> Identity, Dictionary<string, string>? Redirect, (int, int)? RedirectSpan)? entry = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var start = Offset(info) - 1;
                if (reader.LocalName == "dependentAssembly" && reader.NamespaceURI == BindingNamespace)
                {
                    entry = (start, [], null, null);
                    _entryIndent ??= Indent(start);
                }
                else if (entry is { } open && reader.LocalName == "assemblyIdentity")
                {
                    entry = open with { Identity = Attributes(reader) };
                }
                else if (entry is { } withRedirect && reader.LocalName == "bindingRedirect")
                {
                    entry = withRedirect with { Redirect = Attributes(reader), RedirectSpan = (start, TagEnd(start)) };
                }

                if (!reader.IsEmptyElement)
                {
                    depth.Push(reader.LocalName);
                }
                else if (reader.LocalName == "dependentAssembly" && entry is { } empty)
                {
                    Close(redirects, empty, TagEnd(start));
                    entry = null;
                }

            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var closeStart = Offset(info) - 2;
                var end = TagEnd(closeStart);
                if (reader.LocalName == "dependentAssembly" && entry is { } done)
                {
                    Close(redirects, done, end);
                    entry = null;
                }
                else if (reader.LocalName == "assemblyBinding" && reader.NamespaceURI == BindingNamespace)
                {
                    _bindingEnd ??= closeStart;
                    _entryIndent ??= Indent(closeStart) + "  ";
                }
                else if (reader.LocalName == "configuration" && depth.Count == 1)
                {
                    _configurationEnd = _text.LastIndexOf('\n', Math.Max(0, closeStart - 1)) + 1;
                }

                if (depth.Count > 0)
                {
                    depth.Pop();
                }
            }
        }

        Redirects = redirects;
    }

    private static void Close(List<ExistingRedirect> redirects, (int Start, Dictionary<string, string> Identity, Dictionary<string, string>? Redirect, (int, int)? RedirectSpan) entry, int end)
    {
        if (!entry.Identity.TryGetValue("name", out var name))
        {
            return;
        }

        redirects.Add(new ExistingRedirect(
            name, entry.Identity.GetValueOrDefault("publicKeyToken"), entry.Identity.GetValueOrDefault("culture"),
            entry.Redirect?.GetValueOrDefault("oldVersion"), entry.Redirect?.GetValueOrDefault("newVersion"),
            (entry.Start, end), entry.RedirectSpan));
    }

    private static Dictionary<string, string> Attributes(XmlReader reader)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                attributes[reader.LocalName] = reader.Value;
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        return attributes;
    }

    /// <summary>The offset of the character a line-info position names.</summary>
    private int Offset(IXmlLineInfo info) => _lineStarts[info.LineNumber - 1] + info.LinePosition - 1;

    /// <summary>The offset just past the <c>&gt;</c> ending the tag that starts at <paramref name="start"/>, skipping quoted values.</summary>
    private int TagEnd(int start)
    {
        char? quote = null;
        for (var i = start; i < _text.Length; i++)
        {
            var c = _text[i];
            if (quote is not null)
            {
                quote = c == quote ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        return _text.Length;
    }

    private string Indent(int start)
    {
        var lineStart = _text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var prefix = _text[lineStart..start];
        return prefix.Trim().Length == 0 ? prefix : "";
    }
}
