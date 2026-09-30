using System.Text;
using System.Xml;
using System.Xml.Linq;
using Offramp.Core.Output;
using Offramp.Core.Paths;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Doctor;

/// <summary>A setting in an MSBuild file that only Windows, or only .NET Framework's MSBuild, can carry out.</summary>
public sealed record WindowsGuard
{
    /// <summary>1-based line of the element.</summary>
    public required int Line { get; init; }

    /// <summary>The property it sets, or <c>Exec in target X</c>.</summary>
    public required string Setting { get; init; }

    /// <summary>
    /// <c>sgen</c>, <c>aspnet-compiler</c>, <c>build-event</c>, <c>nuget-restore</c>, or <c>msbuild-extensions-path</c>.
    /// </summary>
    public required string Step { get; init; }

    /// <summary>The condition that keeps it where it works (<see cref="WindowsGuards.OnWindows"/> or <see cref="WindowsGuards.OnFrameworkMsbuild"/>).</summary>
    public required string Condition { get; init; }
}

/// <summary>What <c>doctor --fix</c> would do, or did, to one MSBuild file: the conditions it adds.</summary>
public sealed record ProjectFileFix
{
    /// <summary>Repository-relative path.</summary>
    public required string File { get; init; }

    /// <summary>The settings it conditions, in file order.</summary>
    public required IReadOnlyList<WindowsGuard> Guards { get; init; }

    /// <summary>True when the file was written in this run.</summary>
    public required bool Applied { get; init; }

    /// <summary>The unified diff of the change.</summary>
    public string? Diff { get; init; }
}

/// <summary>
/// The Windows-only settings a project file sets itself, where the compile-only block in
/// <c>Directory.Build.props</c> cannot reach them: MSBuild imports that file before the project's own properties,
/// so <c>&lt;GenerateSerializationAssemblies&gt;On&lt;/GenerateSerializationAssemblies&gt;</c> in the project wins over
/// the block's <c>Off</c>, and <c>Directory.Build.targets</c> comes after the common targets have read it. The fix
/// is a condition on the setting itself, so a plain <c>dotnet build</c> works outside Windows for anyone, with or
/// without Offramp, and Visual Studio on Windows does what it always did
/// (<c>docs/decisions/0063-condition-windows-only-settings-in-project-files.md</c>).
/// Edits are text insertions: every other byte of the file stays as it was.
/// </summary>
public static class WindowsGuards
{
    /// <summary>For what needs Windows itself: cmd.exe build events and commands, NuGet 2 restore, machine-wide MSBuild paths.</summary>
    public const string OnWindows = "'$(OS)' == 'Windows_NT'";

    /// <summary>
    /// For tasks only .NET Framework's MSBuild has (<c>SGen</c>, <c>AspNetCompiler</c>): <c>dotnet build</c> cannot run
    /// them on Windows either (MSB3474, MSB4803), so they are kept for MSBuild.exe, Visual Studio's build.
    /// </summary>
    public const string OnFrameworkMsbuild = "'$(MSBuildRuntimeType)' != 'Core'";

    /// <summary>A condition mentioning any of these already chooses by platform or builder; the setting is left as it is.</summary>
    private static readonly string[] PlatformAware = ["$(OS)", "IsOSPlatform", "$(MSBuildRuntimeType)", "$(OfframpCompileOnly)"];

    private static readonly string[] SharedFiles = ["Directory.Build.props", "Directory.Build.targets"];

    /// <summary>The settings a file needs conditioned, and those it already conditions, each in file order.</summary>
    public sealed record Found(IReadOnlyList<WindowsGuard> Needed, IReadOnlyList<WindowsGuard> Guarded)
    {
        public static readonly Found None = new([], []);
    }

    /// <summary>
    /// The MSBuild files that can set a project's properties from the repository: each C#, Visual Basic, or F#
    /// project file, every <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> between it and the root, and
    /// <paramref name="extra"/> (a shared settings file a diagnostic named). Repository-relative, sorted, existing.
    /// </summary>
    public static IReadOnlyList<string> Files(string repositoryRoot, IEnumerable<string> projects, IEnumerable<string>? extra = null)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in projects.Where(OtherProjects.IsDotNet))
        {
            if (!File.Exists(RepoPaths.ToAbsolute(repositoryRoot, project)))
            {
                continue;
            }

            files.Add(project);
            AddImports(repositoryRoot, project, Parent(project), files);
            for (var folder = Parent(project); folder is not null; folder = folder.Length == 0 ? null : Parent(folder))
            {
                foreach (var name in SharedFiles)
                {
                    var file = folder.Length == 0 ? name : folder + "/" + name;
                    if (File.Exists(RepoPaths.ToAbsolute(repositoryRoot, file)))
                    {
                        files.Add(file);
                    }
                }
            }
        }

        foreach (var file in extra ?? [])
        {
            if (File.Exists(RepoPaths.ToAbsolute(repositoryRoot, file)))
            {
                files.Add(file);
            }
        }

        return [.. files];
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>
    /// The repository's own files <paramref name="file"/> imports by a path MSBuild would resolve without evaluation
    /// (relative, or from <c>$(MSBuildThisFileDirectory)</c> or <c>$(MSBuildProjectDirectory)</c>), transitively:
    /// a shared <c>.settings</c> or <c>.targets</c> file sets properties for every project that imports it. Files in a
    /// <c>packages</c> folder are restored package content, not the repository's.
    /// </summary>
    private static void AddImports(string repositoryRoot, string file, string projectFolder, SortedSet<string> files)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(RepoPaths.ToAbsolute(repositoryRoot, file), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return;
        }

        var root = Path.GetFullPath(repositoryRoot);
        foreach (var import in document.Descendants().Where(e => e.Name.LocalName == "Import"))
        {
            var project = import.Attribute("Project")?.Value
                .Replace("$(MSBuildThisFileDirectory)", Folder(Parent(file)), StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildProjectDirectory)", Folder(projectFolder).TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                .Replace('\\', '/');
            if (project is null || project.Contains("$(", StringComparison.Ordinal) || project.Contains('*', StringComparison.Ordinal)
                || (Path.IsPathRooted(project) && !project.StartsWith(Folder(""), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.IsPathRooted(project) ? project : Path.Combine(root, Parent(file), project));
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || !File.Exists(full)
                || relative.Split('/').Any(s => s.Equals("packages", StringComparison.OrdinalIgnoreCase))
                || !files.Add(relative))
            {
                continue;
            }

            AddImports(repositoryRoot, relative, projectFolder, files);
        }

        string Folder(string relative) => Path.Combine(root, relative).Replace('\\', '/') + "/";
    }

    /// <summary>What <paramref name="file"/> needs; null when it cannot be read or needs nothing.</summary>
    public static ProjectFileFix? Plan(string repositoryRoot, string file) => Fix(repositoryRoot, file, write: false);

    /// <summary>Writes the conditions <paramref name="file"/> needs, keeping its encoding; null when it needs none.</summary>
    public static ProjectFileFix? Apply(string repositoryRoot, string file) => Fix(repositoryRoot, file, write: true);

    private static ProjectFileFix? Fix(string repositoryRoot, string file, bool write)
    {
        var path = RepoPaths.ToAbsolute(repositoryRoot, file);
        if (Read(path) is not { } content)
        {
            return null;
        }

        var needed = Locate(content.Text).Where(s => !s.Guarded).ToList();
        if (needed.Count == 0)
        {
            return null;
        }

        var updated = Edit(content.Text, needed);
        if (write)
        {
            File.WriteAllBytes(path, [.. content.Encoding.GetPreamble(), .. content.Encoding.GetBytes(updated)]);
        }

        return new ProjectFileFix
        {
            File = file,
            Guards = [.. needed.Select(s => s.Guard)],
            Applied = write,
            Diff = UnifiedDiff.Create(file, file, content.Text, updated),
        };
    }

    /// <summary>The settings in <paramref name="repositoryRoot"/>/<paramref name="file"/>; <see cref="Found.None"/> when unreadable.</summary>
    public static Found Find(string repositoryRoot, string file) =>
        Read(RepoPaths.ToAbsolute(repositoryRoot, file)) is { } content ? Find(content.Text) : Found.None;

    /// <summary>The settings in an MSBuild file's text; <see cref="Found.None"/> when it is not XML.</summary>
    public static Found Find(string text)
    {
        var sites = Locate(text);
        return new Found([.. sites.Where(s => !s.Guarded).Select(s => s.Guard)], [.. sites.Where(s => s.Guarded).Select(s => s.Guard)]);
    }

    /// <summary><paramref name="text"/> with every needed condition added; the same text when none is needed.</summary>
    public static string Apply(string text) => Edit(text, [.. Locate(text).Where(s => !s.Guarded)]);

    private sealed record Site(WindowsGuard Guard, bool Guarded, int NameOffset, int? ConditionOffset);

    private static List<Site> Locate(string text)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return [];
        }

        var lines = LineStarts(text);
        var sites = new List<Site>();
        foreach (var element in document.Descendants())
        {
            if (Rule(element) is not { } rule)
            {
                continue;
            }

            var info = (IXmlLineInfo)element;
            var condition = element.Attribute("Condition");
            sites.Add(new Site(
                new WindowsGuard { Line = info.LineNumber, Setting = rule.Setting, Step = rule.Step, Condition = rule.Condition },
                element.AncestorsAndSelf().Any(e => IsPlatformAware(e.Attribute("Condition")?.Value)),
                Offset(lines, info),
                condition is null ? null : Offset(lines, condition)));
        }

        return sites;
    }

    /// <summary>Why an element needs Windows, and the condition that keeps it there; null for everything else.</summary>
    private static (string Setting, string Step, string Condition)? Rule(XElement element)
    {
        var name = element.Name.LocalName;
        if (element.Parent?.Name.LocalName == "PropertyGroup")
        {
            var value = element.Value.Trim();
            return name switch
            {
                "GenerateSerializationAssemblies" when value.Length > 0 && !value.Equals("Off", StringComparison.OrdinalIgnoreCase) => (name, "sgen", OnFrameworkMsbuild),
                "MvcBuildViews" when value.Equals("true", StringComparison.OrdinalIgnoreCase) => (name, "aspnet-compiler", OnFrameworkMsbuild),

                // Visual Studio runs build events as batch files; elsewhere MSBuild hands them to /bin/sh.
                "PreBuildEvent" or "PostBuildEvent" when value.Length > 0 => (name, "build-event", OnWindows),

                // A NuGet 2 .nuget/NuGet.targets runs NuGet.exe, through Mono outside Windows.
                "RestorePackages" when value.Equals("true", StringComparison.OrdinalIgnoreCase) => (name, "nuget-restore", OnWindows),

                // Hides Microsoft.Common.props, and with it Directory.Build.props (OFR0122).
                "MSBuildExtensionsPath" or "MSBuildExtensionsPath32" or "MSBuildExtensionsPath64" when value.Length > 0 => (name, "msbuild-extensions-path", OnWindows),
                _ => null,
            };
        }

        if (name == "Exec"
            && element.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Target") is { } target
            && element.Attribute("Command")?.Value is { } command
            && WindowsOnlyBuildSteps.IsWindowsCommand(command))
        {
            return ($"Exec in target {target.Attribute("Name")?.Value}", "build-event", OnWindows);
        }

        return null;
    }

    private static bool IsPlatformAware(string? condition) =>
        condition is not null && PlatformAware.Any(m => condition.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds each site's condition: a new attribute after the element name, or the guard joined to the one it has.</summary>
    private static string Edit(string text, IReadOnlyList<Site> sites)
    {
        var builder = new StringBuilder(text);
        foreach (var site in sites.OrderByDescending(s => s.ConditionOffset ?? s.NameOffset))
        {
            if (site.ConditionOffset is { } attribute && ConditionValue(text, attribute) is { } value)
            {
                var existing = text[value.Start..value.End].Trim();
                var guard = value.Quote == '"' ? site.Guard.Condition : site.Guard.Condition.Replace("'", "&apos;", StringComparison.Ordinal);
                builder.Remove(value.Start, value.End - value.Start)
                    .Insert(value.Start, existing.Length == 0 ? guard : $"{guard} And ({existing})");
                continue;
            }

            var end = site.NameOffset;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('>' or '/'))
            {
                end++;
            }

            builder.Insert(end, $" Condition=\"{site.Guard.Condition}\"");
        }

        return builder.ToString();
    }

    /// <summary>The span between the quotes of the attribute whose name starts at <paramref name="offset"/>.</summary>
    private static (int Start, int End, char Quote)? ConditionValue(string text, int offset)
    {
        var at = text.IndexOf('=', offset);
        if (at < 0)
        {
            return null;
        }

        at++;
        while (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            at++;
        }

        if (at >= text.Length || text[at] is not ('"' or '\''))
        {
            return null;
        }

        var quote = text[at];
        var close = text.IndexOf(quote, at + 1);
        return close < 0 ? null : (at + 1, close, quote);
    }

    /// <summary>The offset where each line starts, counting line breaks as the XML reader does (CR LF, CR, LF).</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
                starts.Add(i + 1);
            }
            else if (text[i] is '\r' or '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int Offset(List<int> lines, IXmlLineInfo info) => lines[info.LineNumber - 1] + info.LinePosition - 1;

    private sealed record Content(string Text, Encoding Encoding);

    /// <summary>
    /// The file's text and encoding (UTF-8 with or without a byte order mark, or UTF-16); null when it is missing or
    /// not valid in its encoding, so a file in a legacy code page is never rewritten.
    /// </summary>
    private static Content? Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Encoding encoding = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true, true),
            [0xFF, 0xFE, ..] => new UnicodeEncoding(false, true, true),
            [0xFE, 0xFF, ..] => new UnicodeEncoding(true, true, true),
            _ => new UTF8Encoding(false, true),
        };
        var preamble = encoding.GetPreamble().Length;
        try
        {
            return new Content(encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
