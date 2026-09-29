using Offramp.Core.Git;
using Offramp.Core.Model;

namespace Offramp.Refactoring.Codemods;

/// <summary>A file whose assembly attributes the <c>assemblyinfo</c> codemod leaves alone, and why (<c>OFR4306</c>).</summary>
public sealed record SharedFile
{
    /// <summary>Repository-relative; the file name for a file outside the repository.</summary>
    public required string File { get; init; }

    public bool InRepository { get; init; } = true;

    public bool OutsideProject { get; init; }

    /// <summary>Other projects of the workspace model that compile it, sorted.</summary>
    public IReadOnlyList<string> SharedWith { get; init; } = [];

    /// <summary>Git ignores it: a build output, which the build writes again.</summary>
    public bool IgnoredByGit { get; init; }

    /// <summary>Not one of the project's compile items: a build target adds it to the compilation.</summary>
    public bool AddedByBuild { get; init; }

    /// <summary>Generated code (an <c>&lt;auto-generated&gt;</c> header, a <c>.g.cs</c> name): a tool writes it.</summary>
    public bool GeneratedCode { get; init; }

    /// <summary>The <c>GenerateAssembly&lt;Name&gt;Attribute</c> properties for the attributes it declares, in file order.</summary>
    public IReadOnlyList<string> Switches { get; init; } = [];

    /// <summary>The reasons, joined: "outside the project's folder; also compiled by A and B; ignored by git".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (!InRepository)
        {
            parts.Add("outside the repository");
        }

        if (OutsideProject)
        {
            parts.Add("outside the project's folder");
        }

        if (SharedWith.Count > 0)
        {
            var names = SharedWith.Take(3).Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
            var more = SharedWith.Count > 3 ? $" and {SharedWith.Count - 3} more" : "";
            parts.Add($"also compiled by {string.Join(", ", names)}{more}");
        }

        if (IgnoredByGit)
        {
            parts.Add("ignored by git, so the build writes it");
        }

        if (AddedByBuild)
        {
            parts.Add("added to the compilation by a build target, not by the project file");
        }

        if (GeneratedCode)
        {
            parts.Add("generated code");
        }

        return string.Join("; ", parts);
    }
}

/// <summary>
/// The <c>assemblyinfo</c> codemod's guard (docs/decisions/0039-shared-assembly-info.md): an
/// AssemblyInfo file outside the project's folder, compiled by another project, ignored by git,
/// added to the compilation by the build, or generated code is shared or generated. Editing it would change every
/// project that compiles it, or be undone by the next build. Its attributes stay, and the project
/// turns the SDK's matching attributes off instead.
/// </summary>
public static class AssemblyInfoGuard
{
    /// <param name="root">The repository root.</param>
    /// <param name="model">The workspace model, for the other projects' compile items.</param>
    /// <param name="project">The project the codemod runs over.</param>
    /// <param name="compiled">Repository-relative compile items of the project with <c>assemblyinfo</c> sites.</param>
    /// <param name="addedByBuild">Repository-relative files with sites that the compilation has and the project's compile items do not.</param>
    /// <param name="generatedCode">Repository-relative files among those that are generated code.</param>
    /// <param name="git">Tells ignored files; null when there is no git to ask.</param>
    /// <param name="cancellationToken">Cancels the git query.</param>
    /// <returns>The files the codemod must not edit, by path, without their <see cref="SharedFile.Switches"/>.</returns>
    public static async Task<SortedDictionary<string, SharedFile>> FindAsync(string root, WorkspaceModel model, ProjectInfo project,
        IEnumerable<string> compiled, IEnumerable<string> addedByBuild, IEnumerable<string> generatedCode, IGitService? git, CancellationToken cancellationToken)
    {
        var added = addedByBuild.ToHashSet(StringComparer.Ordinal);
        var generated = generatedCode.ToHashSet(StringComparer.Ordinal);
        var candidates = compiled.Concat(added).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var ignored = git is null || candidates.Count == 0
            ? []
            : (await git.IgnoredAsync(root, candidates, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var folder = Path.GetDirectoryName(project.Id)?.Replace('\\', '/') ?? "";
        var result = new SortedDictionary<string, SharedFile>(StringComparer.Ordinal);
        foreach (var file in candidates)
        {
            var shared = new SharedFile
            {
                File = file,
                OutsideProject = folder.Length > 0 && !file.StartsWith(folder + "/", StringComparison.Ordinal),
                SharedWith = [.. model.Projects
                    .Where(p => p.Id != project.Id && p.Compile.Contains(file, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p.Id)
                    .Order(StringComparer.Ordinal)],
                IgnoredByGit = ignored.Contains(file),
                AddedByBuild = added.Contains(file),
                GeneratedCode = generated.Contains(file),
            };
            if (shared.OutsideProject || shared.SharedWith.Count > 0 || shared.IgnoredByGit || shared.AddedByBuild || shared.GeneratedCode)
            {
                result[file] = shared;
            }
        }

        return result;
    }
}
