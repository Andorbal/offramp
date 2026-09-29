using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Analysis.DeadCode;

/// <summary>A resource, configuration, or markup file in a project folder, with its text.</summary>
internal sealed record ProjectFile(string Project, string Relative, string Text, bool Markup);

/// <summary>
/// The files beside the C# sources that name code without compiling it: resources and
/// configuration (<c>.resx</c>, <c>.config</c>, <c>.xaml</c>, <c>.xml</c>, <c>.json</c>, and XML
/// under other extensions, like plugin manifests) and ASP.NET markup (<c>.aspx</c>, <c>.ascx</c>, <c>.master</c>, <c>.ashx</c>, <c>.asmx</c>,
/// <c>.asax</c>, <c>.svc</c>, Razor views), which the runtime compiles and binds by name.
/// </summary>
internal static class ProjectFiles
{
    private static readonly HashSet<string> Resources = new(StringComparer.OrdinalIgnoreCase) { ".resx", ".config", ".xaml", ".xml", ".json" };

    private static readonly HashSet<string> MarkupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aspx", ".ascx", ".master", ".ashx", ".asmx", ".asax", ".svc", ".cshtml", ".vbhtml",
    };

    /// <summary>Files that are not read for names even when they hold XML: sources, and web page assets.</summary>
    private static readonly HashSet<string> NotScanned = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".html", ".htm", ".svg", ".xsd", ".xsl", ".xslt",
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages" };

    private const long MaxXmlSniffLength = 1024 * 1024;

    /// <summary>
    /// Every such file under the projects' folders, once (a file under nested project folders
    /// belongs to the first project by id), sorted by path. A file that cannot be read is left
    /// out and named in <paramref name="skipped"/>.
    /// </summary>
    public static List<ProjectFile> Read(string root, IEnumerable<ProjectInfo> projects, List<string> skipped)
    {
        var files = new SortedDictionary<string, (string Project, string Path, bool Markup)>(StringComparer.Ordinal);
        foreach (var project in projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var directory = Path.GetDirectoryName(RepoPaths.ToAbsolute(root, project.Id))!;
            foreach (var path in Walk(directory))
            {
                var extension = Path.GetExtension(path);
                var markup = MarkupExtensions.Contains(extension);
                var relative = RepoPaths.ToRepositoryRelative(root, path);
                if ((markup || Resources.Contains(extension) || IsXml(path, extension)) && !files.ContainsKey(relative))
                {
                    files[relative] = (project.Id, path, markup);
                }
            }
        }

        var read = new List<ProjectFile>();
        foreach (var (relative, (project, path, markup)) in files)
        {
            try
            {
                read.Add(new ProjectFile(project, relative, File.ReadAllText(path), markup));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{relative}: could not be read, so names in it are not seen.");
            }
        }

        return read;
    }

    /// <summary>
    /// Whether a file with another extension holds XML, such as a plugin manifest (DotNetNuke's
    /// <c>.dnn</c>, <c>.nuspec</c>, <c>.addin</c>): it starts with <c>&lt;</c> and parses.
    /// </summary>
    private static bool IsXml(string path, string extension)
    {
        if (Resources.Contains(extension) || MarkupExtensions.Contains(extension) || NotScanned.Contains(extension))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > MaxXmlSniffLength)
            {
                return false;
            }

            using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
            {
                int next;
                while ((next = reader.Peek()) >= 0 && char.IsWhiteSpace((char)next))
                {
                    reader.Read();
                }

                if (next != '<')
                {
                    return false;
                }
            }

            System.Xml.Linq.XDocument.Load(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// The types an ASP.NET markup file names for the runtime to create: the <c>Inherits</c>,
    /// <c>Class</c>, and <c>Service</c> attributes of its <c>&lt;%@ … %&gt;</c> directives, and a
    /// Razor view's <c>@inherits</c> and <c>@model</c>. Full names, with nested types joined by a dot.
    /// </summary>
    public static IEnumerable<string> NamedTypes(string text)
    {
        for (var start = text.IndexOf("<%@", StringComparison.Ordinal); start >= 0; start = text.IndexOf("<%@", start + 3, StringComparison.Ordinal))
        {
            var end = text.IndexOf("%>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }

            foreach (var (name, value) in Attributes(text[(start + 3)..end]))
            {
                if (name.Equals("Inherits", StringComparison.OrdinalIgnoreCase) || name.Equals("Class", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Service", StringComparison.OrdinalIgnoreCase))
                {
                    yield return TypeName(value);
                }
            }
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var keyword in new[] { "@inherits ", "@model " })
            {
                if (trimmed.StartsWith(keyword, StringComparison.Ordinal))
                {
                    yield return TypeName(trimmed[keyword.Length..].Trim().TrimEnd(';'));
                }
            }
        }
    }

    /// <summary>A full type name without its assembly (<c>Ns.Type, Assembly</c>) or generic arguments, nested types joined by a dot.</summary>
    private static string TypeName(string value)
    {
        var name = value.Split(',')[0].Trim();
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        return (generic >= 0 ? name[..generic] : name).Replace('+', '.');
    }

    /// <summary>The <c>name="value"</c> (or single-quoted) attributes of a directive.</summary>
    private static IEnumerable<(string Name, string Value)> Attributes(string directive)
    {
        var at = 0;
        while (at < directive.Length)
        {
            var equals = directive.IndexOf('=', at);
            if (equals < 0)
            {
                yield break;
            }

            var nameEnd = equals;
            while (nameEnd > at && char.IsWhiteSpace(directive[nameEnd - 1]))
            {
                nameEnd--;
            }

            var nameStart = nameEnd;
            while (nameStart > at && !char.IsWhiteSpace(directive[nameStart - 1]))
            {
                nameStart--;
            }

            var valueStart = equals + 1;
            while (valueStart < directive.Length && char.IsWhiteSpace(directive[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= directive.Length || directive[valueStart] is not ('"' or '\''))
            {
                at = valueStart + 1;
                continue;
            }

            var quote = directive[valueStart];
            var valueEnd = directive.IndexOf(quote, valueStart + 1);
            if (valueEnd < 0)
            {
                yield break;
            }

            yield return (directive[nameStart..nameEnd], directive[(valueStart + 1)..valueEnd]);
            at = valueEnd + 1;
        }
    }

    private static IEnumerable<string> Walk(string directory)
    {
        IEnumerable<string> files, subdirectories;
        try
        {
            files = Directory.EnumerateFiles(directory).ToList();
            subdirectories = Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var subdirectory in subdirectories)
        {
            var name = Path.GetFileName(subdirectory);
            if (!name.StartsWith('.') && !SkippedDirectories.Contains(name))
            {
                foreach (var file in Walk(subdirectory))
                {
                    yield return file;
                }
            }
        }
    }
}
