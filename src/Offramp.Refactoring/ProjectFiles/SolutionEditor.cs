using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;

namespace Offramp.Refactoring.ProjectFiles;

/// <summary>Adds a project to a solution (.sln, .slnx) or a solution filter (.slnf), returning the new bytes.</summary>
public static class SolutionEditor
{
    /// <summary>The edits adding <paramref name="project"/>: the solution, and for a filter also the solution it filters.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="solution">The solution or filter, repository-relative.</param>
    /// <param name="project">The project to add, repository-relative.</param>
    /// <param name="cancellationToken">Stops the serializer.</param>
    /// <param name="diagnostics">Receives <c>OFR2115</c> when a <c>.sln</c> could only be rewritten whole, losing lines.</param>
    public static async Task<IReadOnlyList<(string Path, byte[] Before, byte[] After)>> AddProjectAsync(
        string repositoryRoot, string solution, string project, CancellationToken cancellationToken, DiagnosticBag? diagnostics = null)
    {
        if (!solution.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return [await AddToSolutionAsync(repositoryRoot, solution, project, diagnostics, cancellationToken)];
        }

        var filterPath = RepoPaths.ToAbsolute(repositoryRoot, solution);
        var before = await File.ReadAllBytesAsync(filterPath, cancellationToken);
        var filter = JsonNode.Parse(before)!;
        var inner = filter["solution"]!["path"]!.GetValue<string>();
        var underlying = RepoPaths.ToRepositoryRelative(repositoryRoot,
            Path.GetFullPath(inner.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(filterPath)!));
        var solutionDirectory = Path.GetDirectoryName(RepoPaths.ToAbsolute(repositoryRoot, underlying))!;
        var entry = Path.GetRelativePath(solutionDirectory, RepoPaths.ToAbsolute(repositoryRoot, project)).Replace('/', '\\');
        var projects = filter["solution"]!["projects"]!.AsArray();
        if (!projects.Any(p => string.Equals(p!.GetValue<string>(), entry, StringComparison.OrdinalIgnoreCase)))
        {
            projects.Add(entry);
        }

        var after = new UTF8Encoding(false).GetBytes(filter.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        return [await AddToSolutionAsync(repositoryRoot, underlying, project, diagnostics, cancellationToken), (solution, before, after)];
    }

    private static async Task<(string, byte[], byte[])> AddToSolutionAsync(
        string repositoryRoot, string solution, string project, DiagnosticBag? diagnostics, CancellationToken cancellationToken)
    {
        var path = RepoPaths.ToAbsolute(repositoryRoot, solution);
        var before = await File.ReadAllBytesAsync(path, cancellationToken);
        var serializer = SolutionSerializers.GetSerializerByMoniker(path)
            ?? throw new InvalidDataException($"'{solution}' is not a recognized solution file.");
        var model = await serializer.OpenAsync(path, cancellationToken);
        var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, RepoPaths.ToAbsolute(repositoryRoot, project)).Replace('\\', '/');
        if (model.SolutionProjects.Any(p => string.Equals(p.FilePath.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase)))
        {
            return (solution, before, before);
        }

        // Put it in the solution folder of the project's siblings, if they share one.
        var siblingFolder = model.SolutionProjects
            .Where(p => string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(p.FilePath.Replace('\\', '/'))), Path.GetDirectoryName(Path.GetDirectoryName(relative)), StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Parent)
            .FirstOrDefault(f => f is not null);
        // In the platform's separators: on Windows the serializer writes a forward-slash path as it is, and a .sln
        // names projects with backslashes, which SlnText.Insert looks for.
        model.AddProject(relative.Replace('/', Path.DirectorySeparatorChar), projectTypeName: null, folder: siblingFolder);
        var rewritten = await SaveAsync(serializer, model, Path.GetExtension(path), cancellationToken);
        if (!path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return (solution, before, rewritten);
        }

        // The serializer writes its own format (Format Version 12.00) and drops what it does not model
        // (TestCaseManagementSettings, comments): keep the file and insert the lines it wrote for the project.
        if (SlnText.Insert(before, rewritten, relative.Replace('/', '\\')) is { } inserted
            && await ReopensWithAsync(serializer, inserted, relative, model.SolutionProjects.Count, cancellationToken))
        {
            return (solution, before, inserted);
        }

        ReportRewrite(diagnostics, solution, before, rewritten);
        return (solution, before, rewritten);
    }

    /// <summary>The serializer writes files, not streams: a temporary copy, read back.</summary>
    private static async Task<byte[]> SaveAsync(ISolutionSerializer serializer, SolutionModel model, string extension, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "offramp-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await serializer.SaveAsync(temporary, model, cancellationToken);
            return await File.ReadAllBytesAsync(temporary, cancellationToken);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>Whether the edited text still reads as a solution with every project and the new one.</summary>
    private static async Task<bool> ReopensWithAsync(ISolutionSerializer serializer, byte[] text, string relative, int projects, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "offramp-" + Guid.NewGuid().ToString("N") + ".sln");
        try
        {
            await File.WriteAllBytesAsync(temporary, text, cancellationToken);
            var reopened = await serializer.OpenAsync(temporary, cancellationToken);
            return reopened.SolutionProjects.Count == projects
                && reopened.SolutionProjects.Any(p => string.Equals(p.FilePath.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is SolutionException or InvalidDataException or FormatException or IOException)
        {
            return false;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary><c>OFR2115</c> naming the lines of the solution that the serializer's rewrite lost or changed.</summary>
    private static void ReportRewrite(DiagnosticBag? diagnostics, string solution, byte[] before, byte[] after)
    {
        var kept = SlnText.Lines(after).Select(l => l.Trim()).GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var lost = new List<string>();
        foreach (var line in SlnText.Lines(before).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (kept.TryGetValue(line, out var count) && count > 0)
            {
                kept[line] = count - 1;
            }
            else
            {
                lost.Add(line);
            }
        }

        var message = lost.Count == 0
            ? $"{solution} could not be edited in place, so the solution serializer rewrote it in its own format."
            : $"{solution} could not be edited in place, so the solution serializer rewrote it: {lost.Count} line(s) of it are gone or changed, the first \"{lost[0]}\".";
        if (diagnostics is not null && !diagnostics.ToSortedList().Any(d => d.Code == DiagnosticCatalog.OFR2115.Code && d.Message == message))
        {
            diagnostics.Report(DiagnosticCatalog.OFR2115, message, new DiagnosticLocation(null, solution),
                [KeyValuePair.Create<string, JsonNode?>("lost", new JsonArray([.. lost.Take(20).Select(l => (JsonNode?)l)]))]);
        }
    }
}

/// <summary>
/// Adds a project to a <c>.sln</c> file as text, leaving every other line as it was: the lines the solution
/// serializer wrote for the project (its <c>Project</c> block, its configuration lines, its solution folder
/// line) go where Visual Studio puts them. Null when the file has no <c>Global</c> section to insert into.
/// </summary>
internal static class SlnText
{
    public static IReadOnlyList<string> Lines(byte[] bytes) => Decode(bytes, out _).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    public static byte[]? Insert(byte[] before, byte[] rewritten, string projectPath)
    {
        var text = Decode(before, out var bom);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var written = Lines(rewritten);

        // The project's block in the serializer's output, and its GUID (the last quoted value of the Project line).
        var start = written.ToList().FindIndex(l => l.StartsWith("Project(", StringComparison.Ordinal)
            && l.Contains("\"" + projectPath + "\"", StringComparison.OrdinalIgnoreCase));
        var end = start < 0 ? -1 : written.ToList().FindIndex(start, l => l.Trim() == "EndProject");
        if (end < 0)
        {
            return null;
        }

        var header = written[start];
        var guid = header[(header.LastIndexOf("\"{", StringComparison.Ordinal) + 1)..].Trim('"');
        var configurations = SectionLines(written, "ProjectConfigurationPlatforms").Where(l => l.Trim().StartsWith(guid + ".", StringComparison.OrdinalIgnoreCase)).ToList();
        var nesting = SectionLines(written, "NestedProjects").Where(l => l.Trim().StartsWith(guid + " ", StringComparison.OrdinalIgnoreCase)).ToList();

        var global = lines.FindIndex(l => l.Trim() == "Global");
        if (global < 0 || lines.FindIndex(global, l => l.Trim() == "EndGlobal") < 0)
        {
            return null;
        }

        // Last to first, so earlier indexes stay valid: folder line, configuration lines, then the project block.
        if (nesting.Count > 0)
        {
            AppendToSection(lines, "NestedProjects", "preSolution", nesting);
        }

        if (configurations.Count > 0)
        {
            AppendToSection(lines, "ProjectConfigurationPlatforms", "postSolution", configurations);
        }

        var lastProject = lines.FindLastIndex(global, l => l.Trim() == "EndProject");
        lines.InsertRange(lastProject < 0 ? global : lastProject + 1, written.Skip(start).Take(end - start + 1));
        return [.. bom, .. new UTF8Encoding(false).GetBytes(string.Join(newline, lines))];
    }

    /// <summary>Adds lines at the end of a global section, creating the section before <c>EndGlobal</c> when there is none.</summary>
    private static void AppendToSection(List<string> lines, string name, string order, IReadOnlyList<string> added)
    {
        var section = lines.FindIndex(l => l.Trim().StartsWith($"GlobalSection({name})", StringComparison.Ordinal));
        if (section >= 0)
        {
            lines.InsertRange(lines.FindIndex(section, l => l.Trim() == "EndGlobalSection"), added);
            return;
        }

        var endGlobal = lines.FindIndex(l => l.Trim() == "EndGlobal");
        lines.InsertRange(endGlobal, [$"\tGlobalSection({name}) = {order}", .. added, "\tEndGlobalSection"]);
    }

    private static IEnumerable<string> SectionLines(IReadOnlyList<string> lines, string name)
    {
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith($"GlobalSection({name})", StringComparison.Ordinal))
            {
                inside = true;
            }
            else if (inside && trimmed == "EndGlobalSection")
            {
                yield break;
            }
            else if (inside)
            {
                yield return line;
            }
        }
    }

    private static string Decode(byte[] bytes, out byte[] bom)
    {
        var utf8 = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        bom = utf8 ? [0xEF, 0xBB, 0xBF] : [];
        return Encoding.UTF8.GetString(bytes, bom.Length, bytes.Length - bom.Length);
    }
}
