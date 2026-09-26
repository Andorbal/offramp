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

    /// <summary>
    /// Sets (or, with a null value, removes) a metadata value on every item of the type whose
    /// Include matches, conditioned or not; an existing value keeps its attribute or element
    /// form, a new one is an attribute. Returns how many items matched.
    /// </summary>
    public int SetMetadata(string itemType, string include, string name, string? value)
    {
        var items = _root.ItemGroups.SelectMany(g => g.Items)
            .Where(i => string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase) && Same(i.Include, include))
            .ToList();
        foreach (var item in items)
        {
            var existing = item.Metadata.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                if (existing is not null)
                {
                    item.RemoveChild(existing);
                }
            }
            else if (existing is not null)
            {
                existing.Value = value;
            }
            else
            {
                item.AddMetadata(name, value, expressAsAttribute: true);
            }
        }

        return items.Count;
    }

    /// <summary>Adds <c>&lt;Import Project="..." /&gt;</c> at the end unless an import of that path exists.</summary>
    public void AddImport(string project)
    {
        if (_root.Imports.Any(i => Same(i.Project, project)))
        {
            return;
        }

        _root.AddImport(project);
    }

    /// <summary>The value of the first unconditioned property with the name, or null.</summary>
    public string? Property(string name) =>
        _root.PropertyGroups.Where(g => string.IsNullOrEmpty(g.Condition)).SelectMany(g => g.Properties)
            .FirstOrDefault(p => string.IsNullOrEmpty(p.Condition) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Sets the first unconditioned property with the name, or adds it to the first unconditioned property group.</summary>
    public void SetProperty(string name, string value)
    {
        var existing = _root.PropertyGroups.Where(g => string.IsNullOrEmpty(g.Condition)).SelectMany(g => g.Properties)
            .FirstOrDefault(p => string.IsNullOrEmpty(p.Condition) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        var group = _root.PropertyGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition)) ?? _root.AddPropertyGroup();
        group.AddProperty(name, value);
    }

    /// <summary>Removes every <c>Reference</c> item whose assembly name (the Include up to its first comma) matches; returns how many.</summary>
    public int RemoveReference(string assemblyName)
    {
        var matches = _root.ItemGroups.SelectMany(g => g.Items)
            .Where(i => string.Equals(i.ItemType, "Reference", StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.Include.Split(',')[0].Trim(), assemblyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

    public void AddEmbeddedResource(string include) => AddItem("EmbeddedResource", include.Replace('/', '\\'), null);

    /// <summary>
    /// The metadata of every unconditioned <c>&lt;Type Update="path"&gt;</c> item for a path
    /// (for example a Designer file's <c>DependentUpon</c>), in document order.
    /// </summary>
    public IReadOnlyList<(string ItemType, IReadOnlyList<KeyValuePair<string, string>> Metadata)> UpdatesFor(string path) =>
        [.. _root.ItemGroups.Where(g => string.IsNullOrEmpty(g.Condition)).SelectMany(g => g.Items)
            .Where(i => string.IsNullOrEmpty(i.Condition) && Same(i.Update, path))
            .Select(i => (i.ItemType, (IReadOnlyList<KeyValuePair<string, string>>)[.. i.Metadata.Select(m => KeyValuePair.Create(m.Name, m.Value))]))];

    /// <summary>Removes every unconditioned Update item for a path; returns how many.</summary>
    public int RemoveUpdates(string path)
    {
        var matches = _root.ItemGroups.Where(g => string.IsNullOrEmpty(g.Condition)).SelectMany(g => g.Items)
            .Where(i => string.IsNullOrEmpty(i.Condition) && Same(i.Update, path)).ToList();
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

    /// <summary>Adds <c>&lt;Type Update="path"&gt;</c> with metadata as child elements (<c>LogicalName</c> as an attribute); an existing one gains the metadata it lacks.</summary>
    public void AddUpdate(string itemType, string path, IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        path = path.Replace('/', '\\');
        var item = Items(itemType, includeRemoves: true)
            .FirstOrDefault(i => string.Equals(i.Update.Replace('/', '\\'), path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            var group = _root.ItemGroups.FirstOrDefault(g => string.IsNullOrEmpty(g.Condition) && g.Items.Any(i => !string.IsNullOrEmpty(i.Update)))
                ?? _root.AddItemGroup();
            item = _root.CreateItemElement(itemType);
            item.Update = path;
            group.AppendChild(item);
        }

        // An existing Update item keeps its metadata and gains what it lacks.
        foreach (var (name, value) in metadata.Where(m => !item.Metadata.Any(e => string.Equals(e.Name, m.Key, StringComparison.OrdinalIgnoreCase))))
        {
            item.AddMetadata(name, value, expressAsAttribute: name == "LogicalName");
        }
    }

    /// <summary>The <c>Remove</c> patterns of unconditioned items of a type (<c>&lt;Compile Remove="Legacy\**" /&gt;</c>).</summary>
    public IReadOnlyList<string> RemovePatterns(string itemType) =>
        [.. Items(itemType, includeRemoves: true).Where(i => !string.IsNullOrEmpty(i.Remove)).Select(i => i.Remove.Replace('\\', '/'))];

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

    private IEnumerable<ProjectItemElement> Items(string itemType, bool includeRemoves = false) =>
        _root.ItemGroups
            .Where(g => string.IsNullOrEmpty(g.Condition))
            .SelectMany(g => g.Items)
            .Where(i => string.Equals(i.ItemType, itemType, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(i.Condition))
            .Where(i => includeRemoves || !string.IsNullOrEmpty(i.Include));

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

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Replace('/', '\\'), b.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(System.Globalization.CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
