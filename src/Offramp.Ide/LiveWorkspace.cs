using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Offramp.Analysis.Compilations;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.Moves;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Model;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Ide;

/// <summary>
/// The workspace model and the recorded compilations with the current text laid over them: what
/// the editor has open, else what is on disk. A recorded file that changed is parsed again with
/// its recorded options, one that is gone is dropped, and a new <c>.cs</c> file inside an
/// SDK-style project's folder (not removed by its <c>Compile Remove</c> patterns) is added. So
/// source edits never need a new scan; project-file edits do (the model is stale), except the
/// ones this workspace's own moves made (<see cref="ApplyMove"/>). References stay as recorded.
/// Not thread-safe: the server serializes its calls.
/// </summary>
public sealed class LiveWorkspace : ICompilationSource, IDisposable
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

    private readonly string _root;
    private readonly CompilationLoader _loader;
    private readonly Dictionary<string, string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Project, string Tfm), CSharpCompilation?> _recorded = [];
    private readonly Dictionary<(string Project, string Tfm), CSharpCompilation?> _live = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _compile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ownEdits = new(StringComparer.Ordinal);

    public LiveWorkspace(string repositoryRoot, WorkspaceModel model)
    {
        _root = repositoryRoot;
        _loader = new CompilationLoader(repositoryRoot);
        Recorded = model;
        Model = model;
    }

    public string RepositoryRoot => _root;

    /// <summary>The model as the last scan wrote it.</summary>
    public WorkspaceModel Recorded { get; }

    /// <summary>The model with each loaded project's compile items as they are now, and this workspace's moves applied.</summary>
    public WorkspaceModel Model { get; private set; }

    /// <summary>Project files this workspace's moves wrote, with the SHA-256 they had afterwards.</summary>
    public IReadOnlyDictionary<string, string> OwnEdits => _ownEdits;

    /// <summary>The editor's text for a file (repository-relative), which wins over the disk until closed.</summary>
    public void SetOpenDocument(string file, string text)
    {
        if (_open.TryGetValue(file, out var existing) && string.Equals(existing, text, StringComparison.Ordinal))
        {
            return;
        }

        _open[file] = text;
        Changed(file);
    }

    public void CloseDocument(string file)
    {
        if (_open.Remove(file))
        {
            Changed(file);
        }
    }

    /// <summary>The editor's text for a file, or null when it is not open.</summary>
    public string? OpenText(string file) => _open.GetValueOrDefault(file);

    /// <summary>A file changed on disk (created, edited, deleted).</summary>
    public void FileChanged(string file) => Changed(file);

    /// <summary>Forgets every live compilation (after a scan the caller creates a new workspace instead).</summary>
    public void Invalidate()
    {
        _live.Clear();
        _compile.Clear();
        Model = Patched(Model);
    }

    /// <summary>The text of a file now: the editor's, else the disk's, else null.</summary>
    public string? CurrentText(string file)
    {
        if (_open.TryGetValue(file, out var text))
        {
            return text;
        }

        var path = RepoPaths.ToAbsolute(_root, file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The text of a file on disk, or null.</summary>
    public string? CurrentTextOnDisk(string file)
    {
        var path = RepoPaths.ToAbsolute(_root, file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The projects that compile a file: those listing it, else the SDK-style project whose folder holds it (the deepest).</summary>
    public IReadOnlyList<ProjectInfo> ProjectsOf(string file)
    {
        _index ??= Index();
        if (_index.TryGetValue(file, out var listing))
        {
            return listing;
        }

        var folder = Model.Projects
            .Where(p => !p.CompileExplicit && Inside(file, Folder(p.Id)) && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Folder(p.Id).Length)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return folder is null ? [] : [folder];
    }

    public Compilation? LoadForProject(ProjectInfo project, string targetFramework)
    {
        var key = (project.Id, targetFramework);
        if (_live.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var live = Build(project, targetFramework);
        _live[key] = live;
        return live;
    }

    public (ImmutableArray<DiagnosticAnalyzer> Analyzers, AnalyzerOptions Options)? LoadAnalyzers(ProjectInfo project, string targetFramework) =>
        _loader.LoadAnalyzers(project, targetFramework);

    /// <summary>
    /// Lays an applied move over the model: the files leave the source's compile items and join
    /// the destination's, and added project references join the graph. The project files the
    /// move wrote are remembered (<see cref="OwnEdits"/>) so they do not make the model stale.
    /// </summary>
    public void ApplyMove(MovePlanDocument plan, IEnumerable<string> editedFiles)
    {
        var moved = plan.Moves.ToDictionary(m => m.File, m => m.To, StringComparer.Ordinal);
        var references = plan.ProjectEdits.Where(e => e.Kind == ProjectEditKind.AddProjectReference && e.Value is not null).ToList();
        var projects = Model.Projects.Select(p =>
        {
            var compile = LiveCompile(p);
            if (p.Id == plan.From)
            {
                compile = [.. compile.Where(f => !moved.ContainsKey(f))];
            }
            else if (p.Id == plan.To)
            {
                compile = [.. compile.Concat(moved.Values).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            }

            _compile[p.Id] = compile;
            var added = references.Where(r => r.Project == p.Id).Select(r => r.Value!).ToList();
            return p with
            {
                Compile = compile,
                ProjectReferences = [.. p.ProjectReferences.Concat(added).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            };
        }).ToList();
        var edges = Model.Graph.Edges
            .Concat(references.Select(r => new GraphEdge(r.Project, r.Value!, GraphEdgeKind.Project)))
            .Distinct()
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();
        Model = Model with { Projects = projects, Graph = Model.Graph with { Edges = edges } };
        _index = null;
        foreach (var file in editedFiles)
        {
            var path = RepoPaths.ToAbsolute(_root, file);
            if (File.Exists(path))
            {
                _ownEdits[file] = Offramp.Core.Caching.ContentHash.Sha256File(path);
            }
        }

        _live.Clear();
    }

    public void Dispose() => _loader.Dispose();

    private Dictionary<string, IReadOnlyList<ProjectInfo>>? _index;

    private void Changed(string file)
    {
        foreach (var project in ProjectsOf(file))
        {
            _compile.Remove(project.Id);
            foreach (var key in _live.Keys.Where(k => k.Project == project.Id).ToList())
            {
                _live.Remove(key);
            }
        }
    }

    /// <summary>Which projects list each file, from the model's compile items.</summary>
    private Dictionary<string, IReadOnlyList<ProjectInfo>> Index()
    {
        var index = new Dictionary<string, List<ProjectInfo>>(StringComparer.Ordinal);
        foreach (var project in Model.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            foreach (var file in project.Compile)
            {
                (index.TryGetValue(file, out var list) ? list : index[file] = []).Add(project);
            }
        }

        return index.ToDictionary(e => e.Key, e => (IReadOnlyList<ProjectInfo>)e.Value, StringComparer.Ordinal);
    }

    /// <summary>The model with the compile items computed so far.</summary>
    private WorkspaceModel Patched(WorkspaceModel model)
    {
        _index = null;
        return model with { Projects = [.. model.Projects.Select(p => _compile.TryGetValue(p.Id, out var compile) ? p with { Compile = compile } : p)] };
    }

    /// <summary>A project's compile items now: its recorded ones that still exist, plus new files for SDK-style projects.</summary>
    private IReadOnlyList<string> LiveCompile(ProjectInfo project)
    {
        if (_compile.TryGetValue(project.Id, out var known))
        {
            return known;
        }

        var baseline = Model.Projects.FirstOrDefault(p => p.Id == project.Id)?.Compile ?? project.Compile;
        var compile = new SortedSet<string>(baseline.Where(f => _open.ContainsKey(f) || File.Exists(RepoPaths.ToAbsolute(_root, f)) || !Inside(f, "")), StringComparer.Ordinal);
        if (!project.CompileExplicit)
        {
            foreach (var file in NewFiles(project))
            {
                compile.Add(file);
            }
        }

        IReadOnlyList<string> result = [.. compile];
        _compile[project.Id] = result;
        Model = Patched(Model);
        return result;
    }

    /// <summary>C# files in an SDK-style project's folder (not under bin/obj or another project's folder, not removed by its patterns).</summary>
    private IEnumerable<string> NewFiles(ProjectInfo project)
    {
        var folder = Folder(project.Id);
        var directory = RepoPaths.ToAbsolute(_root, folder.Length == 0 ? "." : folder);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var projectFile = RepoPaths.ToAbsolute(_root, project.Id);
        var removed = File.Exists(projectFile) ? new PathGlobs(ProjectFileEditor.Load(File.ReadAllBytes(projectFile)).RemovePatterns("Compile")) : new PathGlobs([]);
        var otherProjects = Model.Projects.Where(p => p.Id != project.Id).Select(p => Folder(p.Id)).Where(f => f.Length > folder.Length && Inside(f + "/x", folder)).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*.cs"))
            {
                var relative = RepoPaths.ToRepositoryRelative(_root, file);
                var inProject = folder.Length == 0 ? relative : relative[(folder.Length + 1)..];
                if (!removed.Matches(inProject))
                {
                    yield return relative;
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(sub);
                var relative = RepoPaths.ToRepositoryRelative(_root, sub);
                if (!name.StartsWith('.') && !SkippedDirectories.Contains(name) && !otherProjects.Contains(relative))
                {
                    pending.Push(sub);
                }
            }
        }
    }

    private CSharpCompilation? RecordedCompilation(ProjectInfo project, string targetFramework)
    {
        var key = (project.Id, targetFramework);
        if (!_recorded.TryGetValue(key, out var recorded))
        {
            recorded = _loader.LoadForProject(project, targetFramework) as CSharpCompilation;
            _recorded[key] = recorded;
        }

        return recorded;
    }

    private CSharpCompilation? Build(ProjectInfo project, string targetFramework)
    {
        if (RecordedCompilation(project, targetFramework) is not { } compilation)
        {
            return null;
        }

        var current = Model.Projects.FirstOrDefault(p => p.Id == project.Id) ?? project;
        var compile = LiveCompile(current).ToHashSet(StringComparer.Ordinal);
        var options = compilation.SyntaxTrees.FirstOrDefault(t => !IsGenerated(t.FilePath))?.Options as CSharpParseOptions
            ?? compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? CSharpParseOptions.Default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = compilation;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsGenerated(tree.FilePath) || !Path.IsPathRooted(tree.FilePath))
            {
                continue;
            }

            var file = RepoPaths.ToRepositoryRelative(_root, tree.FilePath);
            if (!Inside(file, ""))
            {
                continue; // outside the repository: keep as recorded
            }

            seen.Add(file);
            var text = CurrentText(file);
            if (text is null)
            {
                result = result.RemoveSyntaxTrees(tree);
                continue;
            }

            if (!string.Equals(Normalize(tree.GetText().ToString()), Normalize(text), StringComparison.Ordinal))
            {
                result = result.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(SourceText.From(text, tree.Encoding ?? System.Text.Encoding.UTF8), (CSharpParseOptions)tree.Options, tree.FilePath));
            }
        }

        var added = compile.Where(f => !seen.Contains(f) && f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            .Select(f => (File: f, Text: CurrentText(f)))
            .Where(f => f.Text is not null)
            .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(f.Text!, System.Text.Encoding.UTF8), options, RepoPaths.ToAbsolute(_root, f.File)))
            .ToList();
        return added.Count == 0 ? result : result.AddSyntaxTrees(added);
    }

    private static string Normalize(string text) => text.TrimStart('﻿');

    /// <summary>Generated sources (under obj/ or bin/, or *.g.cs), which are never laid over.</summary>
    public static bool IsGenerated(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Inside(string file, string folder) =>
        !file.StartsWith("../", StringComparison.Ordinal) && file != ".." && (folder.Length == 0 || file.StartsWith(folder + "/", StringComparison.Ordinal));

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
