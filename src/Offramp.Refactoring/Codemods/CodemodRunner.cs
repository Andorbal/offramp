using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Offramp.Analysis.Compilations;
using Offramp.Analyzers;
using Offramp.Analyzers.CodeFixes;
using Offramp.Analyzers.Rules;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;
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

    /// <summary>
    /// Tells which files git ignores, which <c>assemblyinfo</c> leaves alone (<see cref="AssemblyInfoGuard"/>);
    /// null when there is no git to ask.
    /// </summary>
    public IGitService? Git { get; init; }

    /// <summary>
    /// Whether Offramp runs on Windows. Elsewhere a legacy (non-SDK) project cannot use a
    /// <c>PackageReference</c>'s assemblies, so a codemod that needs a package leaves its sites alone
    /// there (<c>OFR4512</c>).
    /// </summary>
    public bool OnWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>
    /// Where the packages codemods add are inspected, to check that they support the project's
    /// target frameworks (<c>OFR4511</c>); null skips the check.
    /// </summary>
    public IPackageFeeds? Feeds { get; init; }

    /// <summary>The inspection cache for <see cref="Feeds"/>.</summary>
    public ICache Cache { get; init; } = NullCache.Instance;
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
        var inspections = request.Feeds is null ? null : new PackageInspections(request.Feeds, request.Cache);
        var results = new List<CodemodProjectResult>();
        using (var phase = request.Progress.BeginPhase("Running codemods", 1, 1))
        {
            var projects = request.Projects.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            for (var i = 0; i < projects.Count; i++)
            {
                phase.Report(i, projects.Count, projects[i].Id);
                if (await RunProjectAsync(request, projects[i], changeSet, projectFiles, inspections, cancellationToken) is { } project)
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

    private static async Task<CodemodProjectResult?> RunProjectAsync(
        CodemodRequest request, ProjectInfo project, ChangeSet changeSet, ProjectFileEdits projectFiles, PackageInspections? inspections, CancellationToken cancellationToken)
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
        var shared = new SortedDictionary<string, SharedFile>(StringComparer.Ordinal);
        var blocked = await PackageProblemsAsync(request, project, projectFiles, inspections, cancellationToken);
        foreach (var implementation in request.Codemods)
        {
            await RunCodemodAsync(request, project, workspace, implementation, files, sites, shared, blocked.GetValueOrDefault(implementation.Codemod.Name)?.Reason, cancellationToken);
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
        result = KeepSharedFiles(request, project, result, shared, projectFiles);
        ReportSkipped(request.Diagnostics, project.Id, result.Sites
            .Where(s => !(s.Codemod == Catalog.AssemblyInfo.Name && shared.ContainsKey(s.File)))
            .Where(s => !(blocked.TryGetValue(s.Codemod, out var problem) && s.Reason == problem.Reason)));
        ReportPackageProblems(request, project, blocked, result.Sites);

        if (result.Sites.Any(s => s.Codemod == Catalog.SqlClient.Name && s.Outcome == CodemodSiteOutcome.Rewritten))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4510,
                $"{project.Id} now uses Microsoft.Data.SqlClient, which encrypts connections by default; servers without a trusted certificate need TrustServerCertificate=True in their connection strings.",
                new DiagnosticLocation(project.Id));
        }

        return result.Sites.Count == 0 ? null : result;
    }

    /// <summary>
    /// One <c>OFR4501</c> per codemod and reason in a project, at its first site: a reason about the
    /// project (it does not reference ASP.NET Core) would otherwise repeat at every site. The result
    /// lists each site.
    /// </summary>
    internal static void ReportSkipped(DiagnosticBag diagnostics, string project, IEnumerable<CodemodSite> sites)
    {
        foreach (var group in sites.Where(s => s.Outcome == CodemodSiteOutcome.Skipped).GroupBy(s => (s.Codemod, s.Reason)))
        {
            var site = group.First();
            var count = group.Count();
            diagnostics.Report(DiagnosticCatalog.OFR4501,
                count == 1 ? $"{site.Codemod}: {site.Reason}" : string.Create(CultureInfo.InvariantCulture, $"{site.Codemod}: {site.Reason} ({count} sites in {project}; the result lists each.)"),
                new DiagnosticLocation(project, site.File, site.Line, site.Column),
                count == 1 ? null : [KeyValuePair.Create<string, JsonNode?>("sites", count)]);
        }
    }

    /// <summary>Analyzes the project as it is now, records every site, and fixes the fixable ones document by document.</summary>
    private static async Task RunCodemodAsync(CodemodRequest request, ProjectInfo project, CodemodWorkspace workspace, CodemodImplementation implementation,
        Dictionary<DocumentId, SourceFile> files, List<(CodemodSite Site, Diagnostic Diagnostic)> sites, SortedDictionary<string, SharedFile> shared,
        string? blocked, CancellationToken cancellationToken)
    {
        var codemod = implementation.Codemod;
        var compilation = await workspace.Solution.GetProject(workspace.Project)!.GetCompilationAsync(cancellationToken);
        var diagnostics = await compilation!.WithAnalyzers([implementation.Analyzer], workspace.Options).GetAnalyzerDiagnosticsAsync(cancellationToken);
        var generatedCode = new HashSet<SyntaxTree>();
        if (codemod.Id == Catalog.AssemblyInfo.Id)
        {
            diagnostics = diagnostics.AddRange(await GeneratedCodeSitesAsync(workspace, compilation!, diagnostics, generatedCode, cancellationToken));
        }

        var found = new List<(Diagnostic Diagnostic, DocumentId Id, SourceFile File)>();
        var elsewhere = new List<Diagnostic>();
        foreach (var diagnostic in diagnostics.Where(d => d.Id == codemod.Id && d.Location.IsInSource))
        {
            if (workspace.Solution.GetDocumentId(diagnostic.Location.SourceTree) is { } id && files.TryGetValue(id, out var file))
            {
                found.Add((diagnostic, id, file));
            }
            else
            {
                elsewhere.Add(diagnostic);
            }
        }

        if (codemod.Id == Catalog.AssemblyInfo.Id)
        {
            var generated = generatedCode.Select(t => CodemodWorkspace.FileOf(request.RepositoryRoot, t)).OfType<string>();
            await GuardAssemblyInfoAsync(request, project, found.Select(f => (f.Diagnostic, f.File.Path)), elsewhere, generated, sites, shared, cancellationToken);
        }

        var fixable = new SortedDictionary<string, (DocumentId Document, List<Diagnostic> Diagnostics)>(StringComparer.Ordinal);
        foreach (var (diagnostic, id, file) in found)
        {
            var reason = diagnostic.Properties.TryGetValue(Catalog.SkipReason, out var skip) ? skip : null;
            if (reason is null && codemod.Id == Catalog.AssemblyInfo.Id && shared.TryGetValue(file.Path, out var why))
            {
                reason = Kept(why);
            }

            reason ??= blocked;
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

    /// <summary>
    /// <c>assemblyinfo</c> edits only the project's own AssemblyInfo files (OFR4306). <paramref name="shared"/>
    /// gets the compile items it must leave alone, and the files the compilation has but the project's
    /// compile items do not (a version file a build target writes, a file outside the repository), each
    /// with the attributes it declares. Sites in the latter are recorded here, as skipped.
    /// </summary>
    private static async Task GuardAssemblyInfoAsync(CodemodRequest request, ProjectInfo project, IEnumerable<(Diagnostic Diagnostic, string Path)> compiled,
        List<Diagnostic> elsewhere, IEnumerable<string> generatedCode, List<(CodemodSite Site, Diagnostic Diagnostic)> sites, SortedDictionary<string, SharedFile> shared,
        CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var own = compiled.ToList();
        var added = elsewhere.Select(d => (Diagnostic: d, Path: CodemodWorkspace.FileOf(root, d.Location.SourceTree!))).ToList();
        var inside = added.Where(a => a.Path is not null).Select(a => (a.Diagnostic, Path: a.Path!)).ToList();
        var found = await AssemblyInfoGuard.FindAsync(root, request.Model, project, own.Select(o => o.Path), inside.Select(a => a.Path), generatedCode, request.Git, cancellationToken);
        foreach (var (path, why) in found)
        {
            shared[path] = why with { Switches = Switches(own.Concat(inside).Where(a => a.Path == path).Select(a => a.Diagnostic)) };
        }

        foreach (var file in added.Where(a => a.Path is null).GroupBy(a => a.Diagnostic.Location.SourceTree!.FilePath, StringComparer.Ordinal))
        {
            shared[file.Key] = new SharedFile
            {
                File = Path.GetFileName(file.Key.Replace('\\', '/')),
                InRepository = false,
                Switches = Switches(file.Select(a => a.Diagnostic)),
            };
        }

        foreach (var (diagnostic, path) in inside)
        {
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            sites.Add((new CodemodSite
            {
                Codemod = Catalog.AssemblyInfo.Name,
                File = path,
                Line = position.Line + 1,
                Column = position.Character + 1,
                Outcome = CodemodSiteOutcome.Skipped,
                Reason = Kept(shared[path]),
            }, diagnostic));
        }
    }

    /// <summary>
    /// <c>assemblyinfo</c> sites in generated code (an <c>&lt;auto-generated&gt;</c> header, a <c>.g.cs</c>
    /// name), which analyzers do not look at: a version file a build tool writes declares the attributes
    /// the SDK would generate again. The SDK's own generated AssemblyInfo file is not one of them.
    /// </summary>
    private static async Task<List<Diagnostic>> GeneratedCodeSitesAsync(CodemodWorkspace workspace, Compilation compilation, IEnumerable<Diagnostic> analyzed,
        HashSet<SyntaxTree> generatedCode, CancellationToken cancellationToken)
    {
        var result = new List<Diagnostic>();
        if (!AssemblyInfoAnalyzer.Applies(workspace.Options.AnalyzerConfigOptionsProvider.GlobalOptions))
        {
            return result;
        }

        var seen = analyzed.Select(d => d.Location.SourceTree).OfType<SyntaxTree>().ToHashSet();
        foreach (var tree in compilation.SyntaxTrees.Where(t => !seen.Contains(t) && !CodemodWorkspace.GeneratedAssemblyInfo(t)))
        {
            if (await tree.GetRootAsync(cancellationToken) is not CompilationUnitSyntax { AttributeLists.Count: > 0 } unit)
            {
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            foreach (var attribute in unit.AttributeLists.SelectMany(l => l.Attributes))
            {
                if (AssemblyInfoAnalyzer.Site(attribute, model, cancellationToken) is { } site)
                {
                    result.Add(site);
                    generatedCode.Add(tree);
                }
            }
        }

        return result;
    }

    /// <summary>The <c>GenerateAssembly&lt;Name&gt;Attribute</c> property of each <c>assemblyinfo</c> site, in file order.</summary>
    private static List<string> Switches(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d => d.Properties.TryGetValue(AssemblyInfoAnalyzer.Switch, out var name) ? name : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

    private static string Kept(SharedFile why) =>
        $"the file is shared or generated ({why.Describe()}): it stays as is, and the SDK's attribute is turned off instead";

    /// <summary>Why a codemod's sites in a project are left alone because of a package it needs, and the diagnostic that says so.</summary>
    private sealed record PackageProblem(string Reason, Offramp.Core.Diagnostics.DiagnosticDescriptor Code, string Message);

    /// <summary>
    /// The codemods whose rewrite needs a package this project cannot use: the package does not
    /// support one of the project's target frameworks it would be added for (OFR4511; Microsoft.Data.SqlClient
    /// 7.1.0 starts at .NET Framework 4.6.2), or, outside Windows, the project is a legacy (non-SDK)
    /// project that does not use packages.config and so gets no assemblies from a
    /// <c>PackageReference</c> (OFR4512). Their sites are skipped rather than rewritten into code
    /// that cannot compile.
    /// </summary>
    private static async Task<Dictionary<string, PackageProblem>> PackageProblemsAsync(
        CodemodRequest request, ProjectInfo project, ProjectFileEdits projectFiles, PackageInspections? inspections, CancellationToken cancellationToken)
    {
        var problems = new Dictionary<string, PackageProblem>(StringComparer.Ordinal);
        foreach (var codemod in request.Codemods.Select(c => c.Codemod))
        {
            var needed = NeededPackages(project, codemod, projectFiles);
            if (needed.Count == 0)
            {
                continue;
            }

            if (await UnsupportedAsync(project, needed, inspections, cancellationToken) is { } unsupported)
            {
                problems[codemod.Name] = new PackageProblem(
                    $"{unsupported.Package} does not support {unsupported.Frameworks}",
                    DiagnosticCatalog.OFR4511,
                    $"{codemod.Name} needs {unsupported.Package}, which supports {unsupported.Supported} but not {unsupported.Frameworks} ({project.Id})");
                continue;
            }

            var packages = string.Join(", ", needed.Select(p => $"{p.Id} {p.Version}"));
            if (!project.SdkStyle && !project.PackagesConfig && !request.OnWindows)
            {
                problems[codemod.Name] = new PackageProblem(
                    $"the project is a legacy (non-SDK) project, which gets no assemblies from {packages} outside Windows",
                    DiagnosticCatalog.OFR4512,
                    $"{codemod.Name} needs {packages}, but {project.Id} is a legacy (non-SDK) project, and outside Windows the .NET SDK gives the compiler none of a PackageReference's assemblies in such a project");
            }
        }

        return problems;
    }

    /// <summary>
    /// The first needed package that does not support every target framework it would be added
    /// for (all of the project's, or its .NET Framework or modern ones for a conditioned package),
    /// by inspecting the package; null when all do, or when the package cannot be inspected.
    /// </summary>
    private static async Task<(string Package, string Frameworks, string Supported)?> UnsupportedAsync(
        ProjectInfo project, List<CodemodPackage> needed, PackageInspections? inspections, CancellationToken cancellationToken)
    {
        if (inspections is null)
        {
            return null;
        }

        foreach (var package in needed)
        {
            if (!NuGetVersion.TryParse(package.Version, out var version) || await inspections.GetAsync(package.Id, version, cancellationToken) is not { } inspection)
            {
                continue;
            }

            var frameworks = project.TargetFrameworks.Where(t => package.Targets switch
            {
                CodemodPackageTargets.Framework => Workspace.Ingest.Tfm.IsNetFramework(t),
                CodemodPackageTargets.Modern => !Workspace.Ingest.Tfm.IsNetFramework(t),
                _ => true,
            });
            var unsupported = frameworks.Where(t => !TargetSupport.Supports(inspection, NuGetFramework.Parse(t))).ToList();
            if (unsupported.Count == 0)
            {
                continue;
            }

            var lib = inspection.Assemblies.Where(a => a.Path.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)).Select(a => a.Framework).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var supported = lib.Count > 0 ? lib : [.. inspection.AssetFrameworks];
            return ($"{package.Id} {package.Version}", string.Join(", ", unsupported), supported.Count == 0 ? "no framework" : string.Join(", ", supported));
        }

        return null;
    }

    /// <summary>The packages a codemod adds that the project needs and does not reference yet.</summary>
    private static List<CodemodPackage> NeededPackages(ProjectInfo project, Codemod codemod, ProjectFileEdits projectFiles) =>
        [.. codemod.Packages.Where(p =>
            (p.Targets != CodemodPackageTargets.Framework || project.TargetFrameworks.Any(Workspace.Ingest.Tfm.IsNetFramework))
            && !project.PackageReferences.Any(r => string.Equals(r.Id, p.Id, StringComparison.OrdinalIgnoreCase))
            && !projectFiles.Editor(project.Id).ReferencesPackage(p.Id))];

    /// <summary>One diagnostic per codemod left alone in a project for its package, with the number of sites.</summary>
    private static void ReportPackageProblems(CodemodRequest request, ProjectInfo project, Dictionary<string, PackageProblem> problems, IReadOnlyList<CodemodSite> sites)
    {
        foreach (var (name, problem) in problems.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var count = sites.Count(s => s.Codemod == name && s.Reason == problem.Reason);
            if (count == 0)
            {
                continue;
            }

            var remedy = problem.Code == DiagnosticCatalog.OFR4512
                ? "Convert the project with `offramp csproj modernize` first, or run the codemod on Windows."
                : "Retarget the project to a framework the package supports first.";
            request.Diagnostics.Report(problem.Code,
                string.Create(CultureInfo.InvariantCulture, $"{problem.Message}; its {count} site{(count == 1 ? " was" : "s were")} left alone. {remedy}").TrimEnd(),
                new DiagnosticLocation(project.Id),
                [KeyValuePair.Create<string, JsonNode?>("codemod", name), KeyValuePair.Create<string, JsonNode?>("sites", count)]);
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

    /// <summary>
    /// <c>assemblyinfo</c> in a shared or generated file (<see cref="AssemblyInfoGuard"/>): the attributes
    /// stay, and the project turns the SDK's own off (<c>GenerateAssemblyVersionAttribute</c> and the
    /// like set to false) unless it already sets that property. One <c>OFR4306</c> per file.
    /// </summary>
    private static CodemodProjectResult KeepSharedFiles(CodemodRequest request, ProjectInfo project, CodemodProjectResult result,
        SortedDictionary<string, SharedFile> shared, ProjectFileEdits projectFiles)
    {
        var properties = result.Properties.ToList();
        foreach (var why in shared.Values.Where(w => w.Switches.Count > 0))
        {
            var editor = projectFiles.Editor(project.Id);
            var set = new List<string>();
            foreach (var name in why.Switches.Where(n => editor.Property(n) is null && !properties.Any(p => p.Name == n)))
            {
                editor.SetProperty(name, "false");
                properties.Add(new CodemodPropertyEdit(name, "false"));
                set.Add(name);
            }

            var attributes = string.Join(", ", why.Switches.Select(s => s["Generate".Length..^"Attribute".Length]));
            request.Diagnostics.Report(DiagnosticCatalog.OFR4306,
                $"{why.File} is shared or generated ({why.Describe()}), so it is left as is: its {attributes} attributes stay there"
                + (set.Count > 0
                    ? $", and {project.Id} sets {string.Join(", ", set)} to false so the SDK does not generate them again."
                    : $", and {project.Id} already turns the SDK's off."),
                new DiagnosticLocation(project.Id, why.InRepository ? why.File : null),
                [
                    KeyValuePair.Create<string, JsonNode?>("outsideProject", why.OutsideProject),
                    KeyValuePair.Create<string, JsonNode?>("sharedWith", new JsonArray([.. why.SharedWith.Select(p => (JsonNode?)p)])),
                    KeyValuePair.Create<string, JsonNode?>("ignoredByGit", why.IgnoredByGit),
                    KeyValuePair.Create<string, JsonNode?>("addedByBuild", why.AddedByBuild),
                    KeyValuePair.Create<string, JsonNode?>("generatedCode", why.GeneratedCode),
                    KeyValuePair.Create<string, JsonNode?>("properties", new JsonArray([.. why.Switches.Select(n => (JsonNode?)n)])),
                ]);
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
