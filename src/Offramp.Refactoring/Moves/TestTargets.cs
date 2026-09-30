using Offramp.Core.Model;

namespace Offramp.Refactoring.Moves;

/// <summary>Where tests go: an existing project, one to create, or a reason there is none.</summary>
public sealed record TestTarget
{
    /// <summary>The destination project (repository-relative), existing or to be created; null when none was found.</summary>
    public string? Project { get; init; }

    public bool Create { get; init; }

    /// <summary>Several projects matched the naming rule, or several test projects reference the source (<c>OFR2202</c>).</summary>
    public IReadOnlyList<string> Ambiguous { get; init; } = [];

    /// <summary>
    /// The C# test projects that reference the source, when no project is named after it: the
    /// destination was chosen from them (<see cref="ByReference"/>, <c>OFR2207</c>), they are the
    /// ambiguous ones, or, with <c>--create</c>, they could have taken the tests (<c>OFR2208</c>).
    /// </summary>
    public IReadOnlyList<string> Referencing { get; init; } = [];

    /// <summary>The destination, or the ambiguity, comes from the test projects that reference the source.</summary>
    public bool ByReference { get; init; }
}

/// <summary>
/// Target selection and path mapping for <c>move tests</c> (docs/spec/commands/move.md#move-tests).
/// </summary>
public static class TestTargets
{
    /// <summary>The names a test project conventionally has after the project it tests.</summary>
    private static readonly string[] ConventionalSuffixes = [".Test", ".Tests", ".UnitTest", ".UnitTests"];

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
        if (matches.Count > 0)
        {
            return matches.Count == 1 ? new TestTarget { Project = matches[0] } : new TestTarget { Ambiguous = matches };
        }

        var referencing = Referencing(model, source);
        if (create)
        {
            return new TestTarget { Project = CreatedPath(source.Id, name), Create = true, Referencing = [.. referencing.Select(p => p.Id)] };
        }

        return ChooseReferencing(source, referencing);
    }

    /// <summary>
    /// The only C# test project that references the source, or the only one of several named
    /// after it (<c>NHibernate.Test</c> among <c>NHibernate.Test</c> and
    /// <c>NHibernate.TestDatabaseSetup</c>); several otherwise, which is ambiguous.
    /// </summary>
    private static TestTarget ChooseReferencing(ProjectInfo source, List<ProjectInfo> referencing)
    {
        var ids = referencing.Select(p => p.Id).ToList();
        if (referencing.Count == 0)
        {
            return new TestTarget();
        }

        var named = referencing.Count == 1 ? referencing
            : referencing.Where(p => ConventionalSuffixes.Any(s => string.Equals(p.Name, source.Name + s, StringComparison.OrdinalIgnoreCase))).ToList();
        return named.Count == 1
            ? new TestTarget { Project = named[0].Id, Referencing = ids, ByReference = true }
            : new TestTarget { Ambiguous = ids, Referencing = ids, ByReference = true };
    }

    /// <summary>The C# test projects that reference the source directly, by id.</summary>
    private static List<ProjectInfo> Referencing(WorkspaceModel model, ProjectInfo source) =>
    [
        .. model.Projects
            .Where(p => p.Id != source.Id && p.Language == "csharp" && (p.IsTestProject || p.Kind == ProjectKind.Test)
                && p.ProjectReferences.Contains(source.Id, StringComparer.Ordinal))
            .OrderBy(p => p.Id, StringComparer.Ordinal),
    ];

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
