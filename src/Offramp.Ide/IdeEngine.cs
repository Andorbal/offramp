using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Processes;
using Offramp.Core.Progress;
using Offramp.Refactoring.Moves;
using Offramp.Workspace.Store;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Ide;

/// <summary>What an engine is made from.</summary>
public sealed record IdeEngineOptions
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary>Absolute path of the model file (its hash is the plans' <c>workspaceHash</c>).</summary>
    public required string WorkspacePath { get; init; }

    public required OfframpConfig Config { get; init; }

    public IdeSettings Settings { get; init; } = new();

    public required IGitService Git { get; init; }

    public required IProcessRunner Processes { get; init; }

    /// <summary>Resolves the target's reference assemblies for the findings; without it, OFR3001/OFR3002 are not reported.</summary>
    public TargetReferenceResolver? References { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The editor integration's engine (docs/spec/commands/ide.md): file reports for <c>ide check</c>
/// and the language server, and the moves the editor starts. One per repository and workspace
/// model; the server replaces it after a scan.
/// </summary>
public sealed class IdeEngine : IDisposable
{
    private readonly IdeEngineOptions _options;
    private readonly TargetCompilationBuilder _targets;
    private readonly Dictionary<string, TargetCompilation?> _targetCache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<AuditRule> _rules;
    private readonly Dictionary<string, ProjectCounterparts> _counterparts;

    private IdeEngine(IdeEngineOptions options, NewCode newCode, IReadOnlyList<ProjectCounterparts> counterparts, DiagnosticBag diagnostics)
    {
        _options = options;
        NewCode = newCode;
        Counterparts = counterparts;
        Diagnostics = diagnostics;
        Workspace = new LiveWorkspace(options.RepositoryRoot, options.Model);
        _counterparts = counterparts.ToDictionary(c => c.Project, StringComparer.Ordinal);
        _targets = new TargetCompilationBuilder(options.RepositoryRoot, options.Model, options.Config.Target, options.References, diagnostics, Workspace);
        _rules = AuditRunner.ActiveRules(new AuditRequest
        {
            RepositoryRoot = options.RepositoryRoot,
            Model = options.Model,
            Audit = AuditKind.Api,
            TargetMajor = options.Config.Target,
            DisabledPacks = options.Config.Rules.Packs.Disable,
            Overrides = options.Config.SeverityOverrides(),
            Diagnostics = diagnostics,
        });
    }

    public string RepositoryRoot => _options.RepositoryRoot;

    public OfframpConfig Config => _options.Config;

    public IdeSettings Settings => _options.Settings;

    public LiveWorkspace Workspace { get; }

    public NewCode NewCode { get; private set; }

    public IReadOnlyList<ProjectCounterparts> Counterparts { get; }

    /// <summary>What the engine found about the repository rather than a file: the project map, the new-code base, target references.</summary>
    public DiagnosticBag Diagnostics { get; }

    public IdeEnablement Enablement => Ide.Enablement.Resolve(RepositoryRoot, Settings.Enabled);

    public static async Task<IdeEngine> CreateAsync(IdeEngineOptions options, CancellationToken cancellationToken)
    {
        var diagnostics = new DiagnosticBag(options.Config.SeverityOverrides());
        var newCode = await NewCode.CreateAsync(options.RepositoryRoot, options.Git,
            options.Settings.NewCode.Base ?? options.Config.Ide.NewCode.Base,
            options.Settings.NewCode.Scope ?? options.Config.Ide.NewCode.Scope,
            diagnostics, cancellationToken);
        var counterparts = Ide.Counterparts.Resolve(options.Model, options.Config, options.Settings, diagnostics);
        return new IdeEngine(options, newCode, counterparts, diagnostics);
    }

    /// <summary>Resolves the new-code base again (after a commit, a checkout, or a fetch).</summary>
    public async Task RefreshNewCodeAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new DiagnosticBag();
        NewCode = await NewCode.CreateAsync(RepositoryRoot, _options.Git,
            Settings.NewCode.Base ?? Config.Ide.NewCode.Base, Settings.NewCode.Scope ?? Config.Ide.NewCode.Scope, diagnostics, cancellationToken);
    }

    /// <summary>The counterparts of a project (empty for a project the editor does not report on).</summary>
    public IReadOnlyList<string> CounterpartsOf(string project) =>
        _counterparts.TryGetValue(project, out var found) ? found.Counterparts : [];

    /// <summary>What an editor shows in a file (repository-relative), with its current text.</summary>
    public async Task<IdeFileReport> ReportAsync(string file, CancellationToken cancellationToken)
    {
        var text = Workspace.CurrentText(file);
        var projects = Workspace.ProjectsOf(file);
        var project = projects.FirstOrDefault(Ide.Counterparts.Applies) ?? projects.FirstOrDefault();
        var report = new IdeFileReport { File = file, Project = project?.Id, Applies = false, Scan = ScanNeedFor(file, project) };
        if (text is null || project is null || !Ide.Counterparts.Applies(project) || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || LiveWorkspace.IsGenerated("/" + file))
        {
            return report;
        }

        var target = CompilationLoader.PreferredTarget(project);
        var compilation = target is null ? null : Workspace.LoadForProject(project, target) as CSharpCompilation;
        var path = RepoPaths.ToAbsolute(RepositoryRoot, file);
        var tree = compilation?.SyntaxTrees.FirstOrDefault(t => SamePath(t.FilePath, path));
        if (compilation is null || tree is null)
        {
            return report;
        }

        var newLines = await NewCode.NewLinesAsync(file, text, cancellationToken);
        var fresh = newLines.ToHashSet();
        var types = Types(compilation, tree, fresh);
        var findings = await FindingsAsync(project, compilation, tree, fresh, cancellationToken);
        var moves = types.Count == 0 || HasTopLevelStatements(tree)
            ? []
            : CounterpartsOf(project.Id)
                .Select(to => MovePlanner.Assess(new MoveAssessRequest
                {
                    RepositoryRoot = RepositoryRoot,
                    Model = Workspace.Model,
                    Config = Config,
                    From = project.Id,
                    To = to,
                    File = file,
                    Compilations = Workspace,
                }))
                .OfType<MoveAssessment>()
                .ToList();
        return report with
        {
            Applies = true,
            NewLines = NewCode.Ranges(newLines),
            Findings = findings,
            Types = types,
            Moves = moves,
        };
    }

    /// <summary>
    /// Plans the move of one file to a counterpart the way the editor does it: saved file, fresh
    /// model, <c>--co-move none</c>, and nothing but the file moving. Returns the plan, or null with
    /// the reason reported.
    /// </summary>
    public MovePlanDocument? PlanMove(string file, string to, DiagnosticBag diagnostics)
    {
        if (Workspace.OpenText(file) is { } open && !string.Equals(open, Workspace.CurrentTextOnDisk(file), StringComparison.Ordinal))
        {
            diagnostics.Report(DiagnosticCatalog.OFR6008, $"{file} has unsaved changes; save it and move it again.", new DiagnosticLocation(null, file));
            return null;
        }

        if (Staleness() is { } stale)
        {
            diagnostics.Report(DiagnosticCatalog.OFR0002, $"The workspace model is stale: {stale}. Run `offramp scan`, then move the file.");
            return null;
        }

        var project = Workspace.ProjectsOf(file).FirstOrDefault(Ide.Counterparts.Applies);
        if (project is null || Workspace.Model.Projects.All(p => p.Id != to))
        {
            diagnostics.Report(DiagnosticCatalog.OFR6006, $"{file} is not in a .NET Framework project of the workspace model, or {to} is not a project of it.", new DiagnosticLocation(null, file));
            return null;
        }

        var plan = MovePlanner.Plan(new MovePlanRequest
        {
            RepositoryRoot = RepositoryRoot,
            Model = Workspace.Model,
            Config = Config,
            WorkspaceHash = WorkspaceHash(),
            From = project.Id,
            To = to,
            Files = [file],
            CoMove = "none",
            NamespaceMismatch = Config.Move.NamespaceMismatch,
            Diagnostics = diagnostics,
            Compilations = Workspace,
        })?.Plan;
        if (plan is null)
        {
            return null;
        }

        if (plan.Excluded.Count > 0 || plan.Moves.Count != 1 || plan.Moves[0].File != file)
        {
            // The reasons are in the diagnostics (move plan reports each exclusion).
            return null;
        }

        return plan;
    }

    /// <summary>Applies a plan from <see cref="PlanMove"/> with <c>move apply</c> and lays it over the live model.</summary>
    public async Task<MoveApplyOutcome> ApplyMoveAsync(MovePlanDocument plan, DiagnosticBag diagnostics, IProgressSink progress, CancellationToken cancellationToken)
    {
        var now = _options.Time.GetUtcNow();
        var planPath = ".offramp/plans/ide-move-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        var absolute = RepoPaths.ToAbsolute(RepositoryRoot, planPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, Offramp.Core.Json.OfframpJson.Serialize(plan, Offramp.Refactoring.RefactoringJsonContext.Default.MovePlanDocument), new System.Text.UTF8Encoding(false), cancellationToken);
        var outcome = await MoveApplier.ApplyAsync(new MoveApplyRequest
        {
            RepositoryRoot = RepositoryRoot,
            Model = Workspace.Model,
            Config = Config,
            Plan = plan,
            PlanPath = planPath,
            WorkspaceHash = WorkspaceHash(),
            VerifyPolicy = plan.Verify,
            OnFailure = Config.Verify.OnFailure,
            Git = _options.Git,
            Processes = _options.Processes,
            Diagnostics = diagnostics,
            Progress = progress,
            Now = now,
        }, cancellationToken);
        if (outcome.Result.Applied && !outcome.Result.RolledBack && outcome.Result.Moved.Count > 0)
        {
            Workspace.ApplyMove(plan, outcome.Result.Edited);
        }

        return outcome;
    }

    /// <summary>What makes the model stale, ignoring project files this engine's own moves wrote; null when fresh.</summary>
    public string? Staleness()
    {
        var state = WorkspaceStore.StateDirectory(RepositoryRoot, Config);
        var staleness = WorkspaceInputs.Compare(Workspace.Recorded, RepositoryRoot, state);
        var changed = staleness.Changed
            .Where(c => !(Workspace.OwnEdits.TryGetValue(c, out var hash) && File.Exists(RepoPaths.ToAbsolute(RepositoryRoot, c))
                && ContentHash.Sha256File(RepoPaths.ToAbsolute(RepositoryRoot, c)) == hash))
            .ToList();
        var remaining = staleness with { Changed = changed };
        _changedProjects = ChangedProjects(remaining);
        return remaining.IsStale ? remaining.Describe() : null;
    }

    private Dictionary<string, string>? _changedProjects;

    /// <summary>
    /// Why a file cannot be checked as it should until the next scan (docs/spec/commands/ide.md#projects-that-need-a-scan):
    /// its project was added after the scan, the scan recorded no compilation for it, or its project
    /// file (or a Directory.*.props/targets above it) changed since. Null when nothing is missing, and
    /// for projects the editor does not report on.
    /// </summary>
    private IdeScanNeed? ScanNeedFor(string file, ProjectInfo? project)
    {
        if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || LiveWorkspace.IsGenerated("/" + file))
        {
            return null;
        }

        if (project is null)
        {
            return NearestProjectFile(file) is { } added && Workspace.Model.Projects.All(p => p.Id != added)
                ? new IdeScanNeed { Reason = IdeScanNeed.NewProject, Project = added, Message = $"{added} was added after the last scan (or is not in the scanned solution), so Offramp cannot check it yet." }
                : null;
        }

        if (!Ide.Counterparts.Applies(project))
        {
            return null;
        }

        if (project.CompilerCalls.Count == 0)
        {
            return new IdeScanNeed
            {
                Reason = IdeScanNeed.NoCompilation,
                Project = project.Id,
                Message = $"The last scan recorded no compilation for {project.Id} (its build failed or did not run), so Offramp cannot check it.",
            };
        }

        if (_changedProjects is null)
        {
            Staleness();
        }

        return _changedProjects!.TryGetValue(project.Id, out var input)
            ? new IdeScanNeed
            {
                Reason = IdeScanNeed.ProjectChanged,
                Project = project.Id,
                Message = $"{input} changed after the last scan, so what Offramp shows for {project.Id} may be out of date, and moves from it wait for a scan.",
            }
            : null;
    }

    /// <summary>Each project the changed or added inputs affect, with the first such input: its project file, packages.config, or a Directory.*.props/targets above it.</summary>
    private Dictionary<string, string> ChangedProjects(Staleness staleness)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in staleness.Changed.Concat(staleness.Added).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(input);
            var folder = Folder(input);
            IEnumerable<string> affected = name.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                ? [input]
                : name.Equals("packages.config", StringComparison.OrdinalIgnoreCase)
                    ? Workspace.Recorded.Projects.Where(p => Folder(p.Id) == folder).Select(p => p.Id)
                    : name.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase)
                        ? Workspace.Recorded.Projects.Where(p => folder.Length == 0 || p.Id.StartsWith(folder + "/", StringComparison.Ordinal)).Select(p => p.Id)
                        : [];
            foreach (var project in affected)
            {
                result.TryAdd(project, input);
            }
        }

        return result;
    }

    /// <summary>The nearest C# project file in the file's folder or a folder above it, inside the repository.</summary>
    private string? NearestProjectFile(string file)
    {
        for (var folder = Folder(file); ; folder = Folder(folder))
        {
            var directory = RepoPaths.ToAbsolute(RepositoryRoot, folder.Length == 0 ? "." : folder);
            if (Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, "*.csproj").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault() is { } project)
            {
                return folder.Length == 0 ? project : folder + "/" + project;
            }

            if (folder.Length == 0)
            {
                return null;
            }
        }
    }

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public void Dispose() => Workspace.Dispose();

    private string WorkspaceHash() =>
        File.Exists(_options.WorkspacePath) ? "sha256:" + ContentHash.Sha256File(_options.WorkspacePath) : "";

    private async Task<IReadOnlyList<IdeFinding>> FindingsAsync(ProjectInfo project, CSharpCompilation compilation, SyntaxTree tree, HashSet<int> fresh, CancellationToken cancellationToken)
    {
        if (fresh.Count == 0 || _rules.Count == 0)
        {
            return [];
        }

        TargetCompilation? target = null;
        if (_rules.Any(r => r.Matcher == "target-compilation"))
        {
            if (!_targetCache.TryGetValue(project.Id, out target))
            {
                target = await _targets.BuildAsync(project, [], cancellationToken);
            }

            target = target?.WithSources(compilation);
            _targetCache[project.Id] = target;
        }

        var context = new AuditMatchContext
        {
            RepositoryRoot = RepositoryRoot,
            Project = project,
            Compilation = compilation,
            Trees = [tree],
            Rules = _rules,
            TargetMajor = Config.Target,
            Target = target,
        };
        var overrides = Config.SeverityOverrides();
        var findings = new List<IdeFinding>();
        foreach (var raw in AuditEngine.Run(context, AuditRunner.NamedMatchers))
        {
            if (raw.FileLocation is not null || raw.Location.SourceTree != tree)
            {
                continue;
            }

            var span = raw.Location.GetLineSpan();
            if (!fresh.Contains(span.StartLinePosition.Line + 1))
            {
                continue;
            }

            var severity = raw.Rule.SeverityFor(Config.Target);
            if (overrides.TryGetValue(raw.Rule.Id, out var o))
            {
                if (o.Severity is null)
                {
                    continue;
                }

                severity = o.Severity.Value;
            }

            findings.Add(new IdeFinding
            {
                Code = raw.Rule.Id,
                Severity = severity,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
                Symbol = raw.Symbol,
                Message = raw.Message ?? $"{Capitalize(raw.Rule.Title)}: {raw.Symbol}.",
                Recommendation = raw.Rule.Recommendation,
                Details = raw.Details is null ? new SortedDictionary<string, string>(StringComparer.Ordinal) : new SortedDictionary<string, string>(raw.Details.ToDictionary(), StringComparer.Ordinal),
            });
        }

        return [.. findings.OrderBy(f => f.Line).ThenBy(f => f.Column).ThenBy(f => f.Code, StringComparer.Ordinal).ThenBy(f => f.Symbol, StringComparer.Ordinal)];
    }

    /// <summary>The top-level types a file declares (not nested ones), by position.</summary>
    private static IReadOnlyList<IdeType> Types(CSharpCompilation compilation, SyntaxTree tree, HashSet<int> fresh)
    {
        var model = compilation.GetSemanticModel(tree);
        var types = new List<IdeType>();
        foreach (var node in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<MemberDeclarationSyntax>())
        {
            var (identifier, kind) = node switch
            {
                RecordDeclarationSyntax r => (r.Identifier, "record"),
                ClassDeclarationSyntax c => (c.Identifier, "class"),
                StructDeclarationSyntax s => (s.Identifier, "struct"),
                InterfaceDeclarationSyntax i => (i.Identifier, "interface"),
                EnumDeclarationSyntax e => (e.Identifier, "enum"),
                DelegateDeclarationSyntax d => (d.Identifier, "delegate"),
                _ => (default(SyntaxToken), ""),
            };
            if (kind.Length == 0 || model.GetDeclaredSymbol(node) is not { } symbol)
            {
                continue;
            }

            var span = identifier.GetLocation().GetLineSpan();
            types.Add(new IdeType
            {
                Name = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Kind = kind,
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
                New = fresh.Contains(span.StartLinePosition.Line + 1),
            });
        }

        return [.. types.OrderBy(t => t.Line).ThenBy(t => t.Column)];
    }

    private static bool HasTopLevelStatements(SyntaxTree tree) =>
        tree.GetRoot() is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any();

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
