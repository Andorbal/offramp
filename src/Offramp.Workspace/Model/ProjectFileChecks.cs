using System.Xml;
using System.Xml.Linq;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Model;

/// <summary>A <c>.resx</c> file with resources .NET's MSBuild embeds only preserialized, and how many.</summary>
/// <param name="Path">Absolute path of the <c>.resx</c> file.</param>
/// <param name="Count">How many of its resources are not strings.</param>
public sealed record NonStringResourceFile(string Path, int Count);

/// <summary>What a project's own files show about building it outside Windows (docs/decisions/0047-static-checks-of-project-files.md).</summary>
public sealed record ProjectFileFindings
{
    public static readonly ProjectFileFindings None = new();

    /// <summary>
    /// Referenced paths that exist only in another letter case: imports first, then <c>Compile</c>,
    /// <c>EmbeddedResource</c>, and copied <c>None</c> and <c>Content</c> items, then <c>.resx</c> file references,
    /// each by path.
    /// </summary>
    public IReadOnlyList<CaseMismatch> CaseMismatches { get; init; } = [];

    /// <summary>The project's <c>.resx</c> files with non-string resources, by path.</summary>
    public IReadOnlyList<NonStringResourceFile> NonStringResources { get; init; } = [];

    /// <summary>Absolute paths of <c>Compile</c> items whose file exists in no letter case, by path.</summary>
    public IReadOnlyList<string> MissingSources { get; init; } = [];

    public bool IsEmpty => CaseMismatches.Count == 0 && NonStringResources.Count == 0 && MissingSources.Count == 0;
}

/// <summary>
/// Reads the paths a project refers to, from its project file and, when MSBuild evaluated it, from its
/// evaluations, and checks them against the file system in one pass. A build finds these one project at a
/// time, and on Linux which projects fail can depend on build scheduling; the files do not.
/// </summary>
public static class ProjectFileChecks
{
    private const string Import = "Import";

    /// <summary>Item types whose files the build reads, in the order their findings are reported.</summary>
    private static readonly string[] Kinds = [Import, "Compile", "EmbeddedResource", "None", "Content"];

    /// <summary>Output folders beside the project file, whose files the build writes.</summary>
    private static readonly string[] OutputFolders = ["bin", "obj"];

    /// <param name="repositoryRoot">Local repository root; paths outside it are not checked.</param>
    /// <param name="projectFile">Local absolute path of the project file.</param>
    /// <param name="evaluations">The project's evaluations from the log, with capture paths; empty when MSBuild did not evaluate it.</param>
    /// <param name="toLocal">Maps a capture path to this checkout, or null outside the repository.</param>
    /// <param name="solutionDirectory">Local directory of the solution the build ran on, for <c>$(SolutionDir)</c>.</param>
    /// <param name="writtenByBuild">
    /// Whether the build writes a missing source itself (<see cref="WrittenByBuild"/>); by default, from the project's
    /// own imports and project file.
    /// </param>
    public static ProjectFileFindings Check(
        string repositoryRoot, string projectFile, IReadOnlyList<EvaluatedProject> evaluations, Func<string, string?> toLocal, string? solutionDirectory,
        Func<string, bool>? writtenByBuild = null)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        writtenByBuild ??= WrittenByBuild(RepositoryImports(evaluations, toLocal, root), [projectFile]);
        var references = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string kind, string? path)
        {
            if (path is not null && path.StartsWith(root, StringComparison.Ordinal))
            {
                var order = Array.IndexOf(Kinds, kind);
                references[path] = references.TryGetValue(path, out var known) ? Math.Min(known, order) : order;
            }
        }

        foreach (var (kind, path) in FromProjectFile(projectFile, solutionDirectory))
        {
            Add(kind, path);
        }

        foreach (var evaluation in evaluations)
        {
            var directory = Path.GetDirectoryName(evaluation.ProjectFile.Replace('\\', '/'))!.Replace('\\', '/');
            foreach (var import in evaluation.Imports)
            {
                Add(Import, Full(toLocal(import)));
            }

            foreach (var kind in Kinds.Skip(1))
            {
                foreach (var item in evaluation.ItemsOf(kind).Where(i => ReadByBuild(kind, i.Get("CopyToOutputDirectory"))))
                {
                    var include = item.Include.Replace('\\', '/');
                    Add(kind, Full(toLocal(CapturePathMapper.IsWindowsStyle(include) || include.StartsWith('/') ? include : directory + "/" + include)));
                }
            }
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var outputs = OutputFolders.Select(d => Path.Combine(projectDirectory, d) + Path.DirectorySeparatorChar).ToList();
        var mismatches = new List<(int Order, CaseMismatch Mismatch)>();
        var resources = new List<NonStringResourceFile>();
        var missing = new List<string>();
        foreach (var (path, order) in references.OrderBy(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            var onDisk = path;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                if (CaseMismatch.Find(path) is { } mismatch)
                {
                    mismatches.Add((order, mismatch));
                    onDisk = mismatch.OnDisk;
                }
                else
                {
                    if (Kinds[order] == "Compile" && !outputs.Any(o => path.StartsWith(o, StringComparison.Ordinal)))
                    {
                        missing.Add(path);
                    }

                    continue;
                }
            }

            if (Kinds[order] == "EmbeddedResource" && path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            {
                var resx = ResxFile.Read(onDisk);
                if (resx.NonStringCount > 0)
                {
                    resources.Add(new NonStringResourceFile(path, resx.NonStringCount));
                }

                foreach (var reference in resx.FileReferences.Where(r => !File.Exists(r)))
                {
                    if (CaseMismatch.Find(reference) is { } mismatch)
                    {
                        mismatches.Add((Kinds.Length, mismatch with { Evidence = $"{mismatch.Evidence} (ResXFileRef in {path})" }));
                    }
                }
            }
        }

        return new ProjectFileFindings
        {
            CaseMismatches = [.. mismatches
                .DistinctBy(m => m.Mismatch.Spelled, StringComparer.Ordinal)
                .OrderBy(m => m.Order)
                .ThenBy(m => m.Mismatch.Spelled, StringComparer.Ordinal)
                .Select(m => m.Mismatch)],
            NonStringResources = [.. resources.OrderBy(r => r.Path, StringComparer.Ordinal)],
            MissingSources = [.. missing.Where(m => !writtenByBuild(m)).Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// The imports and items the project file itself lists. Only unconditional ones whose path needs no
    /// property but the project's and the solution's directories: the rest need an evaluation.
    /// </summary>
    private static IEnumerable<(string Kind, string Path)> FromProjectFile(string projectFile, string? solutionDirectory)
    {
        var document = Load(projectFile);
        if (document?.Root is null)
        {
            yield break;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectDirectory"] = directory,
            ["MSBuildThisFileDirectory"] = directory + "/",
            ["ProjectDir"] = directory + "/",
            ["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectFile),
        };
        if (solutionDirectory is not null)
        {
            properties["SolutionDir"] = solutionDirectory.TrimEnd('/', '\\') + "/";
        }

        foreach (var element in document.Root.Descendants())
        {
            var name = element.Name.LocalName;
            string? value;
            if (name == Import)
            {
                value = element.Attribute("Project")?.Value;
            }
            else if (Kinds.Contains(name) && element.Parent?.Name.LocalName == "ItemGroup"
                && ReadByBuild(name, (string?)element.Attribute("CopyToOutputDirectory") ?? element.Elements().FirstOrDefault(m => m.Name.LocalName == "CopyToOutputDirectory")?.Value))
            {
                value = element.Attribute("Include")?.Value;
            }
            else
            {
                continue;
            }

            if (value is null || element.Attribute("Sdk") is not null || !(Unconditional(element) || ExistsHolds(element, properties, directory)))
            {
                continue;
            }

            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Expand(part, properties) is { } expanded && expanded.IndexOfAny(['*', '?', '@', '%']) < 0)
                {
                    yield return (name, Path.GetFullPath(Path.Combine(directory, expanded.Replace('\\', '/'))));
                }
            }
        }
    }

    /// <summary>
    /// A test of whether the build itself writes a file: the repository's MSBuild files that projects import name it,
    /// or a target or property of a project file does. Such a file is missing only because the build stopped before
    /// the target ran (Open Live Writer's <c>writer.build.targets</c> writes <c>GlobalAssemblyVersionInfo.cs</c>
    /// before <c>CoreCompile</c>, and another project compiles it), not because a script outside MSBuild has to run
    /// first. The files are read once, on the first question.
    /// </summary>
    /// <param name="importedFiles">Local paths of the repository's imported MSBuild files (not project files).</param>
    /// <param name="projectFiles">Local paths of the project files.</param>
    public static Func<string, bool> WrittenByBuild(IEnumerable<string> importedFiles, IEnumerable<string> projectFiles)
    {
        var texts = new Lazy<List<string>>(() =>
        [
            .. importedFiles.Where(File.Exists).Select(File.ReadAllText),
            .. projectFiles.Select(p => Load(p)?.Root).OfType<XElement>()
                .SelectMany(r => r.Elements().Where(e => e.Name.LocalName is "Target" or "PropertyGroup"))
                .Select(e => e.ToString()),
        ]);
        return path => texts.Value.Any(t => t.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The local paths of the repository's MSBuild files the evaluations imported, project files left out.</summary>
    public static IEnumerable<string> RepositoryImports(IEnumerable<EvaluatedProject> evaluations, Func<string, string?> toLocal, string root) =>
        evaluations
            .SelectMany(e => e.Imports)
            .Select(i => Full(toLocal(i)))
            .OfType<string>()
            .Where(i => i.StartsWith(root, StringComparison.Ordinal) && !i.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// True for the items a build reads: every import, source, and resource, but a <c>None</c> or <c>Content</c>
    /// item only when it is copied to the output (MSB3030); otherwise only publishing reads it.
    /// </summary>
    private static bool ReadByBuild(string kind, string? copyToOutputDirectory) =>
        kind is not ("None" or "Content")
        || string.Equals(copyToOutputDirectory?.Trim(), "Always", StringComparison.OrdinalIgnoreCase)
        || string.Equals(copyToOutputDirectory?.Trim(), "PreserveNewest", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when neither the element nor a parent has a condition, and it is not inside a target or a <c>Choose</c>.</summary>
    private static bool Unconditional(XElement element) =>
        element.AncestorsAndSelf().TakeWhile(e => e.Parent is not null).All(e =>
            e.Attribute("Condition") is null && e.Name.LocalName is not ("Target" or "Choose"));

    /// <summary>
    /// True for an import guarded only by <c>Exists('path')</c> of a file that exists as spelled: the import then
    /// happens here too. SmartStoreNET imports <c>.nuget\nuget.targets</c> when <c>.nuget\NuGet.targets</c> exists.
    /// </summary>
    private static bool ExistsHolds(XElement element, Dictionary<string, string> properties, string directory)
    {
        const string prefix = "Exists(";
        var condition = element.Attribute("Condition")?.Value.Trim();
        if (element.Name.LocalName != Import || condition is null
            || !Unconditional(element.Parent!)
            || !condition.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !condition.EndsWith(')'))
        {
            return false;
        }

        var argument = condition[prefix.Length..^1].Trim();
        return argument.Length > 2 && argument[0] == '\'' && argument[^1] == '\''
            && Expand(argument[1..^1], properties) is { } path
            && File.Exists(Path.GetFullPath(Path.Combine(directory, path.Replace('\\', '/'))));
    }

    /// <summary>The value with the known properties substituted, or null when it uses any other.</summary>
    private static string? Expand(string value, Dictionary<string, string> properties)
    {
        var result = new System.Text.StringBuilder();
        var at = 0;
        while (at < value.Length)
        {
            var start = value.IndexOf("$(", at, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(value, at, value.Length - at);
                break;
            }

            var end = value.IndexOf(')', start + 2);
            if (end < 0 || !properties.TryGetValue(value[(start + 2)..end], out var replacement))
            {
                return null;
            }

            result.Append(value, at, start - at).Append(replacement);
            at = end + 1;
        }

        return result.ToString();
    }

    private static string? Full(string? path) => path is null ? null : Path.GetFullPath(path);

    internal static XDocument? Load(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }
}

/// <summary>
/// The parts of a <c>.resx</c> file the build reads: which resources .NET's MSBuild can embed only
/// preserialized (MSB3822, MSB3823), and the files its <c>ResXFileRef</c> entries name (MSB3554).
/// The rules follow MSBuild's own resx reader, checked against the .NET 10 SDK: strings, byte arrays, and
/// file references to text, byte arrays, and memory streams embed as they are; everything else (images,
/// icons, type-converted values, binary-serialized objects) needs preserialization.
/// </summary>
internal sealed record ResxFile(int NonStringCount, IReadOnlyList<string> FileReferences)
{
    private const string StringType = "System.String";
    private const string StringTypeInMscorlib = "System.String, mscorlib,";

    public static ResxFile Read(string path)
    {
        var document = ProjectFileChecks.Load(path);
        if (document?.Root is null)
        {
            return new ResxFile(0, []);
        }

        var aliases = document.Root.Elements("assembly")
            .Where(a => a.Attribute("alias") is not null && a.Attribute("name") is not null)
            .GroupBy(a => a.Attribute("alias")!.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Attribute("name")!.Value, StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var nonString = 0;
        var references = new List<string>();
        foreach (var data in document.Root.Elements("data"))
        {
            var type = Unalias(data.Attribute("type")?.Value, aliases);
            var mimeType = data.Attribute("mimetype")?.Value;
            if (type is not null && type.StartsWith("System.Resources.ResXFileRef", StringComparison.Ordinal))
            {
                var parts = (data.Element("value")?.Value ?? "").Split(';');
                if (parts[0].Trim().Length > 0)
                {
                    references.Add(Path.GetFullPath(Path.Combine(directory, parts[0].Trim().Replace('\\', '/'))));
                }

                var fileType = parts.Length > 1 ? parts[1].Trim() : "";
                if (!fileType.StartsWith(StringTypeInMscorlib, StringComparison.Ordinal)
                    && !fileType.StartsWith("System.Byte[]", StringComparison.Ordinal)
                    && !fileType.StartsWith("System.IO.MemoryStream, mscorlib,", StringComparison.Ordinal))
                {
                    nonString++;
                }
            }
            else if (NeedsPreserialization(type, mimeType))
            {
                nonString++;
            }
        }

        return new ResxFile(nonString, references);
    }

    private static bool NeedsPreserialization(string? type, string? mimeType) =>
        (type, mimeType) switch
        {
            (null, null) => false,
            _ when type is StringType || type?.StartsWith(StringTypeInMscorlib, StringComparison.Ordinal) == true => false,
            _ when type?.StartsWith("System.Resources.ResXNullRef", StringComparison.Ordinal) == true => false,
            (_, not null) => true,
            _ => !type!.StartsWith("System.Byte[]", StringComparison.Ordinal),
        };

    /// <summary>The type name with an assembly alias the file declares replaced by the assembly's name, as the reader does.</summary>
    private static string? Unalias(string? type, Dictionary<string, string> aliases)
    {
        var comma = type?.IndexOf(',') ?? -1;
        return comma >= 0 && aliases.TryGetValue(type![(comma + 1)..].Trim(), out var assembly)
            ? type[..comma] + ", " + assembly
            : type;
    }
}
