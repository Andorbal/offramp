using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Offramp.Analysis.Compilations;
using Offramp.Analyzers;
using Offramp.Analyzers.CodeFixes;
using Offramp.Analyzers.Rules;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Catalog = Offramp.Analyzers.Codemods;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Refactoring.Codemods;

/// <summary>What <c>codemod run</c> is asked to do.</summary>
public sealed record CodemodRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary>The C# projects to run over, each with a recorded compilation.</summary>
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }

    /// <summary>The codemods, in catalog order.</summary>
    public required IReadOnlyList<CodemodImplementation> Codemods { get; init; }

    public required CompilationLoader Loader { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary>
    /// <c>build_property.*</c> values that replace what the driver derives: <c>csproj modernize</c>
    /// runs <c>assemblyinfo</c> on a legacy project as the SDK-style project it is becoming.
    /// </summary>
    public IReadOnlyDictionary<string, string> PropertyOverrides { get; init; } = new Dictionary<string, string>();
}

/// <summary>A dry run's result and the change set that applies it.</summary>
public sealed record CodemodPlan(CodemodRunResult Result, ChangeSet ChangeSet);

/// <summary>
/// The codemod driver (docs/spec/commands/codemod.md): runs each chosen codemod's analyzer
/// over a project's recorded compilation, fixes the sites it can with the codemod's fixer
/// (the same code path as the IDE's fix-all and <c>dotnet format</c>), one codemod after
/// another, then adds the packages the rewritten code needs. Nothing is written: the result
/// is a change set with a diff.
/// </summary>
public static class CodemodRunner
{
    private const string FrameworkCondition = "'$(TargetFrameworkIdentifier)' == '.NETFramework'";

    private const string ModernCondition = "'$(TargetFrameworkIdentifier)' != '.NETFramework'";

    public static async Task<CodemodPlan> PlanAsync(CodemodRequest request, CancellationToken cancellationToken)
    {
        var changeSet = new ChangeSet();
        var projectFiles = new ProjectFileEdits(request.RepositoryRoot);
        var results = new List<CodemodProjectResult>();
        using (var phase = request.Progress.BeginPhase("Running codemods", 1, 1))
        {
            var projects = request.Projects.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < projects.Count; i++)
            {
                phase.Report(i, projects.Count, projects[i].Id);
                if (await RunProjectAsync(request, projects[i], changeSet, projectFiles, cancellationToken) is { } project)
                {
                    results.Add(project);
                }
            }
        }

        projectFiles.WriteTo(changeSet);
        var files = results.SelectMany(r => r.Files).Distinct(StringComparer.Ordinal).Count();
        var sites = results.SelectMany(r => r.Sites).ToList();
        var result = new CodemodRunResult
        {
            Mode = CodemodMode.Driver,
            Codemods = [.. request.Codemods.Select(c => c.Codemod.Name)],
            Projects = results,
            Summary = new CodemodSummary(results.Count, files,
                sites.Count(s => s.Outcome == CodemodSiteOutcome.Rewritten),
                sites.Count(s => s.Outcome == CodemodSiteOutcome.Skipped),
                results.Sum(r => r.Packages.Count)),
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new CodemodPlan(result, changeSet);
    }

    private static async Task<CodemodProjectResult?> RunProjectAsync(CodemodRequest request, ProjectInfo project, ChangeSet changeSet, ProjectFileEdits projectFiles, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var target = CompilationLoader.PreferredTarget(project);
        if (target is null || request.Loader.LoadForProject(project, target) is not { } compilation)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR0001, $"{project.Id} has no recorded compilation; run `offramp scan`.", new DiagnosticLocation(project.Id));
            return null;
        }

        var properties = CodemodWorkspace.Properties(project, compilation);
        foreach (var (name, value) in request.PropertyOverrides)
        {
            properties[name] = value;
        }

        using var workspace = CodemodWorkspace.Create(project.Name, RepoPaths.ToAbsolute(root, project.Id), compilation,
            request.Codemods.Select(c => c.Codemod.Id), properties, request.Loader.LoadGlobalOptions(project, target));

        // Only the project's own compile items are rewritten: not generated code, not files outside the repository.
        var compile = project.Compile.ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<DocumentId, SourceFile>();
        foreach (var document in workspace.Solution.GetProject(workspace.Project)!.Documents)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is not null && CodemodWorkspace.FileOf(root, tree) is { } file && compile.Contains(file))
            {
                files[document.Id] = new SourceFile(file, await document.GetTextAsync(cancellationToken));
            }
        }

        var sites = new List<(CodemodSite Site, Diagnostic Diagnostic)>();
        foreach (var implementation in request.Codemods)
        {
            await RunCodemodAsync(request, workspace, implementation, files, sites, cancellationToken);
        }

        var edited = new List<string>();
        foreach (var (id, file) in files.OrderBy(f => f.Value.Path, StringComparer.Ordinal))
        {
            var document = workspace.Solution.GetDocument(id)!;
            var current = await document.GetTextAsync(cancellationToken);
            if (current.ContentEquals(file.Original))
            {
                continue;
            }

            var original = workspace.Solution.GetDocument(id)!.WithText(file.Original);
            var changes = await document.GetTextChangesAsync(original, cancellationToken);
            var text = file.Original.WithChanges(changes.Select(c => new TextChange(c.Span, Newlines(c.NewText ?? "", file.Newline)))).ToString();
            changeSet.Edit(file.Path, file.Bytes!, Encode(file.Bytes!, text));
            edited.Add(file.Path);
        }

        var result = new CodemodProjectResult
        {
            Project = project.Id,
            TargetFramework = target,
            Files = edited,
            Sites = [.. sites.Select(s => s.Site)
                .OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.Column).ThenBy(s => s.Codemod, StringComparer.Ordinal)],
        };
        result = AddPackages(request, project, result, projectFiles);
        result = AddProperties(project, result, sites, projectFiles);
        foreach (var site in result.Sites.Where(s => s.Outcome == CodemodSiteOutcome.Skipped))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4501, $"{site.Codemod}: {site.Reason}", new DiagnosticLocation(project.Id, site.File, site.Line, site.Column));
        }

        if (result.Sites.Any(s => s.Codemod == Catalog.SqlClient.Name && s.Outcome == CodemodSiteOutcome.Rewritten))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4510,
                $"{project.Id} now uses Microsoft.Data.SqlClient, which encrypts connections by default; servers without a trusted certificate need TrustServerCertificate=True in their connection strings.",
                new DiagnosticLocation(project.Id));
        }

        return result.Sites.Count == 0 ? null : result;
    }

    /// <summary>Analyzes the project as it is now, records every site, and fixes the fixable ones document by document.</summary>
    private static async Task RunCodemodAsync(CodemodRequest request, CodemodWorkspace workspace, CodemodImplementation implementation, Dictionary<DocumentId, SourceFile> files,
        List<(CodemodSite Site, Diagnostic Diagnostic)> sites, CancellationToken cancellationToken)
    {
        var codemod = implementation.Codemod;
        var compilation = await workspace.Solution.GetProject(workspace.Project)!.GetCompilationAsync(cancellationToken);
        var diagnostics = await compilation!.WithAnalyzers([implementation.Analyzer], workspace.Options).GetAnalyzerDiagnosticsAsync(cancellationToken);
        var fixable = new SortedDictionary<string, (DocumentId Document, List<Diagnostic> Diagnostics)>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(d => d.Id == codemod.Id && d.Location.IsInSource))
        {
            if (workspace.Solution.GetDocumentId(diagnostic.Location.SourceTree) is not { } id || !files.TryGetValue(id, out var file))
            {
                continue;
            }

            var reason = diagnostic.Properties.TryGetValue(Catalog.SkipReason, out var skip) ? skip : null;
            if (reason is null && implementation.Fixer is not null)
            {
                reason = file.Check(request.RepositoryRoot, request.Diagnostics);
            }

            var position = file.Map.ToOriginal(diagnostic.Location.SourceSpan.Start);
            var line = file.Original.Lines.GetLinePosition(position);
            var outcome = reason is not null ? CodemodSiteOutcome.Skipped
                : implementation.Fixer is null ? CodemodSiteOutcome.Referenced
                : CodemodSiteOutcome.Rewritten;
            sites.Add((new CodemodSite { Codemod = codemod.Name, File = file.Path, Line = line.Line + 1, Column = line.Character + 1, Outcome = outcome, Reason = reason }, diagnostic));
            if (outcome == CodemodSiteOutcome.Rewritten)
            {
                if (!fixable.TryGetValue(file.Path, out var entry))
                {
                    entry = (id, []);
                    fixable[file.Path] = entry;
                }

                entry.Diagnostics.Add(diagnostic);
            }
        }

        foreach (var (_, (id, list)) in fixable)
        {
            var document = workspace.Solution.GetDocument(id)!;
            var fixedDocument = await implementation.Fixer!.FixDocumentAsync(document, [.. list], cancellationToken);
            files[id].Map.Add(await fixedDocument.GetTextChangesAsync(document, cancellationToken));
            workspace.Solution = fixedDocument.Project.Solution;
        }
    }

    /// <summary>Adds each package a codemod with rewritten (or referenced) sites needs, unless the project has it.</summary>
    private static CodemodProjectResult AddPackages(CodemodRequest request, ProjectInfo project, CodemodProjectResult result, ProjectFileEdits projectFiles)
    {
        var used = result.Sites.Where(s => s.Outcome != CodemodSiteOutcome.Skipped).Select(s => s.Codemod).ToHashSet(StringComparer.Ordinal);
        var edits = new List<CodemodPackageEdit>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var codemod in request.Codemods.Select(c => c.Codemod).Where(c => used.Contains(c.Name)))
        {
            foreach (var package in codemod.Packages.Where(p => added.Add(p.Id)))
            {
                // The model has the references of the evaluated targets; the file also has those conditioned away.
                if (project.PackageReferences.Any(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase))
                    || projectFiles.Editor(project.Id).ReferencesPackage(package.Id))
                {
                    continue;
                }

                // Framework and modern packages are conditioned even when every target matches, so a later retarget keeps them right.
                var condition = package.Targets switch
                {
                    CodemodPackageTargets.Framework => FrameworkCondition,
                    CodemodPackageTargets.Modern => ModernCondition,
                    _ => null,
                };
                if (package.Targets == CodemodPackageTargets.Framework && !project.TargetFrameworks.Any(Workspace.Ingest.Tfm.IsNetFramework))
                {
                    continue;
                }

                if (AddPackage(request, project, codemod, package, condition, projectFiles) is { } edit)
                {
                    edits.Add(edit);
                }
            }
        }

        return result with { Packages = edits };
    }

    private static CodemodPackageEdit? AddPackage(CodemodRequest request, ProjectInfo project, Codemod codemod, CodemodPackage package, string? condition, ProjectFileEdits projectFiles)
    {
        if (project.PackagesConfig)
        {
            NotAdded(request, project, codemod, package, "the project uses packages.config; add it with NuGet, or modernize the project first");
            return null;
        }

        var central = project.Properties.TryGetValue("ManagePackageVersionsCentrally", out var cpm) && string.Equals(cpm, "true", StringComparison.OrdinalIgnoreCase);
        var files = new List<string> { project.Id };
        if (central)
        {
            if (CentralVersionsFile(request.RepositoryRoot, project) is not { } props)
            {
                NotAdded(request, project, codemod, package, "the project manages versions centrally and no Directory.Packages.props was found");
                return null;
            }

            projectFiles.Editor(props).AddPackageVersion(package.Id, package.Version);
            files.Add(props);
        }

        var editor = projectFiles.Editor(project.Id);
        if (condition is null)
        {
            editor.AddPackageReference(package.Id, central ? null : package.Version);
        }
        else
        {
            editor.AddConditionedPackageReference(package.Id, central ? null : package.Version, condition);
        }

        return new CodemodPackageEdit { Codemod = codemod.Name, Id = package.Id, Version = package.Version, Condition = condition, Files = [.. files.Order(StringComparer.Ordinal)] };
    }

    private static void NotAdded(CodemodRequest request, ProjectInfo project, Codemod codemod, CodemodPackage package, string why) =>
        request.Diagnostics.Report(DiagnosticCatalog.OFR4505,
            $"{codemod.Name} needs the package {package.Id} {package.Version} in {project.Id}, but {why}.",
            new DiagnosticLocation(project.Id));

    /// <summary>The central versions file: the recorded <c>DirectoryPackagesPropsPath</c>, else the nearest Directory.Packages.props above the project.</summary>
    private static string? CentralVersionsFile(string root, ProjectInfo project)
    {
        if (project.Properties.TryGetValue("DirectoryPackagesPropsPath", out var recorded) && File.Exists(RepoPaths.ToAbsolute(root, recorded)))
        {
            return recorded;
        }

        for (var folder = Path.GetDirectoryName(project.Id.Replace('\\', '/')); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = folder.Length == 0 ? "Directory.Packages.props" : folder.Replace('\\', '/') + "/Directory.Packages.props";
            if (File.Exists(RepoPaths.ToAbsolute(root, candidate)))
            {
                return candidate;
            }

            if (folder.Length == 0)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>assemblyinfo</c>: a removed attribute whose value differs from the SDK's default keeps
    /// it as a project property (<c>&lt;AssemblyVersion&gt;</c>, <c>&lt;Company&gt;</c>, ...),
    /// unless the project already sets that property.
    /// </summary>
    private static CodemodProjectResult AddProperties(ProjectInfo project, CodemodProjectResult result, List<(CodemodSite Site, Diagnostic Diagnostic)> sites, ProjectFileEdits projectFiles)
    {
        var properties = new List<CodemodPropertyEdit>();
        foreach (var (site, diagnostic) in sites.Where(s => s.Site.Codemod == Catalog.AssemblyInfo.Name && s.Site.Outcome == CodemodSiteOutcome.Rewritten)
            .OrderBy(s => s.Site.File, StringComparer.Ordinal).ThenBy(s => s.Site.Line))
        {
            if (!diagnostic.Properties.TryGetValue(AssemblyInfoAnalyzer.Property, out var name) || string.IsNullOrEmpty(name)
                || !diagnostic.Properties.TryGetValue(AssemblyInfoAnalyzer.Value, out var value) || value is null
                || properties.Any(p => p.Name == name))
            {
                continue;
            }

            var editor = projectFiles.Editor(project.Id);
            if (editor.Property(name) is null)
            {
                editor.SetProperty(name, value);
                properties.Add(new CodemodPropertyEdit(name, value));
            }
        }

        return result with { Properties = properties };
    }

    private static string Newlines(string text, string newline)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return newline == "\n" ? lf : lf.Replace("\n", newline, StringComparison.Ordinal);
    }

    private static byte[] Encode(byte[] original, string text)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        return original.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    /// <summary>A source file the codemods may rewrite: its recorded text and, once checked, its bytes on disk.</summary>
    private sealed class SourceFile(string path, SourceText original)
    {
        private bool _checked;
        private string? _problem;

        public string Path { get; } = path;

        public SourceText Original { get; } = original;

        public PositionMap Map { get; } = new();

        public byte[]? Bytes { get; private set; }

        /// <summary>The file's dominant line ending, which rewritten text uses.</summary>
        public string Newline { get; private set; } = "\n";

        /// <summary>
        /// Null when the file on disk is the recorded text in UTF-8, else why its sites are
        /// skipped. A changed file is reported once (<c>OFR4504</c>).
        /// </summary>
        public string? Check(string root, DiagnosticBag diagnostics)
        {
            if (_checked)
            {
                return _problem;
            }

            _checked = true;
            Bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(root, Path));
            var body = Bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? Bytes[3..] : Bytes;
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
            }
            catch (DecoderFallbackException)
            {
                _problem = "the file is not UTF-8, and codemods write UTF-8 only";
                return _problem;
            }

            if (!Original.ContentEquals(SourceText.From(text)))
            {
                _problem = "the file changed since the last scan";
                diagnostics.Report(DiagnosticCatalog.OFR4504, $"{Path} changed since the last scan; its sites were left alone. Run `offramp scan` and the codemod again.", new DiagnosticLocation(null, Path));
                return _problem;
            }

            var crlf = CountOf(text, "\r\n");
            Newline = crlf > 0 && crlf >= CountOf(text, "\n") - crlf ? "\r\n" : "\n";
            return null;
        }

        private static int CountOf(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>Project files (and central versions files) edited by the run, loaded once and written into the change set at the end.</summary>
    private sealed class ProjectFileEdits(string root)
    {
        private readonly SortedDictionary<string, (byte[] Before, ProjectFiles.ProjectFileEditor Editor)> _editors = new(StringComparer.Ordinal);

        public ProjectFiles.ProjectFileEditor Editor(string path)
        {
            if (!_editors.TryGetValue(path, out var entry))
            {
                var before = File.ReadAllBytes(RepoPaths.ToAbsolute(root, path));
                entry = (before, ProjectFiles.ProjectFileEditor.Load(before));
                _editors[path] = entry;
            }

            return entry.Editor;
        }

        public void WriteTo(ChangeSet changeSet)
        {
            foreach (var (path, (before, editor)) in _editors)
            {
                changeSet.Edit(path, before, editor.Save());
            }
        }
    }
}
