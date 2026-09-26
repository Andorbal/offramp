using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Offramp.Refactoring.ProjectFiles;

/// <summary>
/// Edits a project file with Microsoft.Build's construction model, which keeps
/// comments, conditions, and formatting, and evaluates nothing (CLAUDE.md). The
/// result keeps the file's byte order mark and line endings.
/// </summary>
public sealed class ProjectFileEditor
{
    private readonly ProjectRootElement _root;
    private readonly bool _bom;
    private readonly string _newline;

    private ProjectFileEditor(ProjectRootElement root, bool bom, string newline)
    {
        _root = root;
        _bom = bom;
        _newline = newline;
    }

    public static ProjectFileEditor Load(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = ProjectRootElement.Create(reader, new ProjectCollection(), preserveFormatting: true);
        return new ProjectFileEditor(root, bom, text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
    }

    /// <summary>True for SDK-style projects (<c>Sdk="..."</c> or an <c>&lt;Sdk&gt;</c> element).</summary>
    public bool IsSdkStyle => !string.IsNullOrEmpty(_root.Sdk) || _root.Children.OfType<ProjectSdkElement>().Any();

    public bool HasItem(string itemType, string include) =>
        Items(itemType).Any(i => Same(i.Include, include));

    /// <summary>Adds <c>&lt;ProjectReference Include="..\Bar\Bar.csproj" /&gt;</c> unless present.</summary>
    public void AddProjectReference(string relativePath) =>
        AddItem("ProjectReference", relativePath.Replace('/', '\\'), null);

    /// <summary>Adds a PackageReference; <paramref name="version"/> is null under central package management.</summary>
    public void AddPackageReference(string id, string? version) =>
        AddItem("PackageReference", id, version is null ? null : [("Version", version)]);

    /// <summary>Adds a PackageVersion (central package management) unless one for the id exists.</summary>
    public void AddPackageVersion(string id, string version) =>
        AddItem("PackageVersion", id, [("Version", version)]);

    public void AddInternalsVisibleTo(string assembly) => AddItem("InternalsVisibleTo", assembly, null);

    public void AddCompile(string include) => AddItem("Compile", include.Replace('/', '\\'), null);

    /// <summary>Removes every unconditioned item of the type whose Include matches; returns how many.</summary>
    public int RemoveItems(string itemType, string include)
    {
        var matches = Items(itemType).Where(i => Same(i.Include, include)).ToList();
        foreach (var item in matches)
        {
            var group = item.Parent;
            group.RemoveChild(item);
            if (group.Count == 0 && group.Parent is not null)
            {
                group.Parent.RemoveChild(group);
            }
        }

        return matches.Count;
    }

    public byte[] Save()
    {
        using var writer = new Utf8StringWriter();
        _root.Save(writer);
        var text = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (_newline != "\n")
        {
            text = text.Replace("\n", _newline, StringComparison.Ordinal);
        }

        var body = new UTF8Encoding(false).GetBytes(text);
        return _bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    private IEnumerable<ProjectItemElement> Items(string itemType) =>
        _root.ItemGroups
            .Where(g => string.IsNullOrEmpty(g.Condition))
            .SelectMany(g => g.Items)
            .Where(i => string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(i.Condition));

    private void AddItem(string itemType, string include, (string Name, string Value)[]? metadata)
    {
        if (HasItem(itemType, include))
        {
            return;
        }

        // Next to items of the same type when an unconditioned group holds them, else in a new group.
        var group = _root.ItemGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition) && g.Items.Any(i => string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase)))
            ?? _root.AddItemGroup();
        var item = _root.CreateItemElement(itemType, include);
        var after = group.Items.LastOrDefault(i => string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase)
            && string.Compare(i.Include, include, StringComparison.OrdinalIgnoreCase) < 0);
        var before = group.Items.FirstOrDefault(i => string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase)
            && string.Compare(i.Include, include, StringComparison.OrdinalIgnoreCase) > 0);
        if (after is not null)
        {
            group.InsertAfterChild(item, after);
        }
        else if (before is not null)
        {
            group.InsertBeforeChild(item, before);
        }
        else
        {
            group.AppendChild(item);
        }

        foreach (var (name, value) in metadata ?? [])
        {
            item.AddMetadata(name, value, expressAsAttribute: true);
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(System.Globalization.CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
