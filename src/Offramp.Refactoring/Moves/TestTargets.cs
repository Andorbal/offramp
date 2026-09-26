using Offramp.Core.Model;

namespace Offramp.Refactoring.Moves;

/// <summary>Where tests go: an existing project, one to create, or a reason there is none.</summary>
public sealed record TestTarget
{
    /// <summary>The destination project (repository-relative), existing or to be created; null when none was found.</summary>
    public string? Project { get; init; }

    public bool Create { get; init; }

    /// <summary>Several projects matched the naming rule (<c>OFR2202</c>).</summary>
    public IReadOnlyList<string> Ambiguous { get; init; } = [];
}

/// <summary>
/// Target selection and path mapping for <c>move tests</c> (docs/spec/commands/move.md#move-tests).
/// </summary>
public static class TestTargets
{
    /// <param name="model">The workspace model.</param>
    /// <param name="source">The source project.</param>
    /// <param name="to">The project given with <c>--to</c>, resolved, or null.</param>
    /// <param name="create">Whether a missing test project may be created.</param>
    /// <param name="suffix"><c>move.tests.targetSuffix</c>.</param>
    public static TestTarget Select(WorkspaceModel model, ProjectInfo source, string? to, bool create, string suffix)
    {
        if (to is not null)
        {
            return new TestTarget { Project = to };
        }

        var name = source.Name + suffix;
        var matches = model.Projects.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        return matches.Count switch
        {
            1 => new TestTarget { Project = matches[0] },
            > 1 => new TestTarget { Ambiguous = matches },
            _ when create => new TestTarget { Project = CreatedPath(source.Id, name), Create = true },
            _ => new TestTarget(),
        };
    }

    /// <summary>A new project next to the source: <c>src/Bar/Bar.csproj</c> gets <c>src/Bar.Tests/Bar.Tests.csproj</c>.</summary>
    public static string CreatedPath(string sourceProject, string name)
    {
        var folder = Folder(sourceProject);
        var parent = Folder(folder);
        return (parent.Length == 0 ? "" : parent + "/") + name + "/" + name + ".csproj";
    }

    /// <summary>
    /// The destination of a source file: its path under the source project's folder, under the
    /// destination's folder, less a <c>Tests</c> folder when <paramref name="stripTests"/>; null
    /// when the file is not under the source project's folder.
    /// </summary>
    public static string? MapPath(string file, string sourceProject, string destinationProject, bool stripTests)
    {
        var sourceFolder = Folder(sourceProject);
        var prefix = sourceFolder.Length == 0 ? "" : sourceFolder + "/";
        if (!file.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var segments = file[prefix.Length..].Split('/').ToList();
        if (stripTests)
        {
            var index = segments.FindIndex(0, segments.Count - 1, s => s.Equals("Tests", StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                segments.RemoveAt(index);
            }
        }

        var destinationFolder = Folder(destinationProject);
        return (destinationFolder.Length == 0 ? "" : destinationFolder + "/") + string.Join('/', segments);
    }

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
