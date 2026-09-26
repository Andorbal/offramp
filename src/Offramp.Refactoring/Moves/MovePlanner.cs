using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NuGet.Frameworks;
using Offramp.Analysis.Compilations;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Model;
using DiagnosticDescriptor = Offramp.Core.Diagnostics.DiagnosticDescriptor;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Offramp.Refactoring.Moves;

public sealed record MovePlanRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required string WorkspaceHash { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>The files asked for (repository-relative); with <see cref="All"/>, ignored.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public bool All { get; init; }

    /// <summary><c>closure</c> or <c>none</c> (<c>move.coMove</c>).</summary>
    public required string CoMove { get; init; }

    /// <summary><c>allow</c>, <c>warn</c>, or <c>block</c> (<c>move.namespaceMismatch</c>).</summary>
    public required string NamespaceMismatch { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>
/// Plans a move of files between projects (docs/spec/commands/move.md#move-plan): partitions what
/// each file uses, co-moves what it needs, proposes references, proves the result compiles in the
/// destination for every target framework and that the source still compiles, and never plans an
/// edit to a moved file.
/// </summary>
public static class MovePlanner
{
    private const string WindowsOnlyRule = "CA1416";

    public static MovePlanResult? Plan(MovePlanRequest request)
    {
        var model = request.Model;
        var bag = request.Diagnostics;
        var source = model.Projects.Single(p => p.Id == request.From);
        var destination = model.Projects.Single(p => p.Id == request.To);
        if (source.Id == destination.Id)
        {
            bag.Report(DiagnosticCatalog.OFR2002, $"The destination is the source project itself ({source.Id}).", new DiagnosticLocation(source.Id));
            return null;
        }

        foreach (var project in new[] { source, destination }.Where(p => Frozen(request.Config, p.Id)))
        {
            bag.Report(DiagnosticCatalog.OFR2003, $"{project.Id} is frozen in offramp.yml; nothing moves into or out of it.", new DiagnosticLocation(project.Id));
            return null;
        }

        if (source.Language != "csharp" || destination.Language != "csharp")
        {
            bag.Report(DiagnosticCatalog.OFR2205, "Moves analyze C# projects only.", new DiagnosticLocation(source.Language != "csharp" ? source.Id : destination.Id));
            return null;
        }

        using var loader = new CompilationLoader(request.RepositoryRoot);
        var sourceTarget = CompilationLoader.PreferredTarget(source);
        var sourceCompilation = sourceTarget is null ? null : loader.LoadForProject(source, sourceTarget) as CSharpCompilation;
        var destinations = destination.CompilerCalls.Keys
            .Select(tfm => (Tfm: tfm, Compilation: loader.LoadForProject(destination, tfm) as CSharpCompilation))
            .Where(d => d.Compilation is not null)
            .ToList();
        if (sourceCompilation is null || destinations.Count == 0)
        {
            bag.Report(DiagnosticCatalog.OFR0004, $"The compiler log has no compilation for {(sourceCompilation is null ? source.Id : destination.Id)}; run `offramp scan` again.");
            return null;
        }

        var requested = request.All ? [.. source.Compile.Where(f => Inside(f, source.Id))] : request.Files;
        var unknown = requested.Where(f => !source.Compile.Contains(f, StringComparer.Ordinal) && !IsResource(request.RepositoryRoot, source, f)).ToList();
        foreach (var file in unknown)
        {
            bag.Report(DiagnosticCatalog.OFR2004, $"{file} is not a file of {source.Id}.", new DiagnosticLocation(source.Id, file));
        }

        if (unknown.Count > 0)
        {
            return null;
        }

        var context = new PlanContext(request, loader, source, destination, sourceCompilation, sourceTarget!, [.. destinations.Select(d => (d.Tfm, d.Compilation!))]);
        return context.Run(requested);
    }

    private sealed class PlanContext(
        MovePlanRequest request, CompilationLoader loader, ProjectInfo source, ProjectInfo destination,
        CSharpCompilation sourceCompilation, string sourceTarget, List<(string Tfm, CSharpCompilation Compilation)> destinations)
    {
        private readonly Dictionary<string, string?> _coMoveOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExcludedMove> _excluded = new(StringComparer.Ordinal);
        private readonly List<PlanCycle> _cycles = [];
        private readonly HashSet<string> _partialNoted = new(StringComparer.Ordinal);

        // Dependents of the source that use moved types, and whether they need their own reference to the destination.
        private readonly SortedDictionary<string, bool> _dependentNeeds = new(StringComparer.Ordinal);
        private List<(ProjectInfo Dependent, SortedSet<string> Files)>? _dependentFiles;
        private Dictionary<string, SyntaxTree> _trees = null!;
        private Dictionary<string, FileUses> _uses = null!;

        private string Root => request.RepositoryRoot;

        private WorkspaceModel Model => request.Model;

        private DiagnosticBag Bag => request.Diagnostics;

        /// <summary>True when the destination already depends on the source: moved code may use the source, but the source may not use moved code.</summary>
        private bool DestinationAboveSource => Reach(Model, destination.Id).Contains(source.Id);

        public MovePlanResult Run(IReadOnlyList<string> requested)
        {
            _trees = source.Compile
                .Select(f => (File: f, Tree: sourceCompilation.SyntaxTrees.FirstOrDefault(t => SamePath(t.FilePath, RepoPaths.ToAbsolute(Root, f)))))
                .Where(t => t.Tree is not null)
                .ToDictionary(t => t.File, t => t.Tree!, StringComparer.Ordinal);
            _uses = _trees.ToDictionary(t => t.Key, t => Uses(t.Value), StringComparer.Ordinal);

            var candidates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in requested)
            {
                Add(candidates, file, null);
            }

            // Static rules, then trial compilation, until nothing more is excluded.
            Dictionary<string, List<ReferenceNeed>> needs;
            while (true)
            {
                while (Partition(candidates))
                {
                }

                needs = NeedsPerTarget(candidates);
                if (!TrialCompile(candidates, needs) && !SourceCheck(candidates, needs) && !DependentsCheck(candidates))
                {
                    break;
                }
            }

            PlatformCheck(candidates, needs);
            NamespaceCheck(candidates);
            return Result(candidates, needs);
        }

        /// <summary>Adds a file with its resource pair and the other files of any partial type it declares.</summary>
        private void Add(SortedSet<string> candidates, string file, string? coMoveOf)
        {
            if (_excluded.ContainsKey(file) || !candidates.Add(file))
            {
                return;
            }

            _coMoveOf.TryAdd(file, coMoveOf);
            foreach (var pair in Pairs(file))
            {
                Add(candidates, pair, file);
            }

            if (_trees.TryGetValue(file, out var tree))
            {
                foreach (var other in PartialSiblings(tree))
                {
                    if (!candidates.Contains(other) && _partialNoted.Add(other))
                    {
                        Bag.Report(DiagnosticCatalog.OFR2110, $"Declares part of a partial type in {file}; they move together.", new DiagnosticLocation(source.Id, other));
                    }

                    Add(candidates, other, file);
                }
            }
        }

        /// <summary>Applies the rules that need no compilation; returns true when the set changed.</summary>
        private bool Partition(SortedSet<string> candidates)
        {
            var changed = false;
            foreach (var file in candidates.ToList())
            {
                if (!candidates.Contains(file) || !_uses.TryGetValue(file, out var uses))
                {
                    continue;
                }

                // Declared elsewhere in the source: co-move, unless the destination can use the source.
                foreach (var needed in uses.Files.Where(f => f != file && !candidates.Contains(f)).Order(StringComparer.Ordinal))
                {
                    if (DestinationAboveSource)
                    {
                        continue;
                    }

                    if (_excluded.ContainsKey(needed) || request.CoMove == "none" || !Inside(needed, source.Id))
                    {
                        changed |= Exclude(candidates, file, DiagnosticCatalog.OFR2101,
                            $"Needs {needed}, which {(request.CoMove == "none" ? "--co-move none keeps in place" : "cannot move")}.", [needed]);
                        break;
                    }

                    Add(candidates, needed, file);
                    changed = true;
                }

                if (!candidates.Contains(file))
                {
                    continue;
                }

                // Declared in another project: the destination must be able to reference it without a cycle.
                foreach (var project in uses.Projects.Where(p => p != destination.Id && p != source.Id).Order(StringComparer.Ordinal))
                {
                    if (destination.ProjectReferences.Contains(project))
                    {
                        continue;
                    }

                    var target = Model.Projects.Single(p => p.Id == project);
                    if (Reach(Model, project).Contains(destination.Id))
                    {
                        var path = CyclePath(project);
                        _cycles.Add(new PlanCycle(file, path));
                        changed |= Exclude(candidates, file, DiagnosticCatalog.OFR2001,
                            $"Needs {project}, which depends on {destination.Id}: {string.Join(" → ", path)}.", path);
                        break;
                    }

                    if (IncompatibleTarget(target) is { } tfm)
                    {
                        changed |= Exclude(candidates, file, DiagnosticCatalog.OFR2103,
                            $"Needs {project}, which has nothing {destination.Id} can reference for {tfm}.", [project]);
                        break;
                    }
                }

                if (!candidates.Contains(file))
                {
                    continue;
                }

                // From a package: it must have assets for every destination target.
                foreach (var package in uses.Packages.Where(p => !DestinationHas(p.Id)).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (destinations.FirstOrDefault(d => PackageAssets(package, d.Tfm) is { Count: 0 }) is { Tfm: not null } missing)
                    {
                        changed |= Exclude(candidates, file, DiagnosticCatalog.OFR2102,
                            $"Needs package {package.Id} {package.Version}, which has no assets for {missing.Tfm}.", [package.Id]);
                        break;
                    }
                }

                // The destination may exclude the path.
                if (candidates.Contains(file) && DestinationExcludes(Destination(file)) is { } pattern)
                {
                    changed |= Exclude(candidates, file, DiagnosticCatalog.OFR2111, $"{destination.Id} removes {pattern} from its Compile items.", [pattern]);
                }
            }

            // When the destination uses the source, code that stays in the source cannot use moved code.
            if (DestinationAboveSource)
            {
                foreach (var file in candidates.ToList())
                {
                    var users = _uses.Where(u => !candidates.Contains(u.Key) && u.Value.Files.Contains(file)).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
                    if (users.Count > 0 && candidates.Contains(file))
                    {
                        changed |= Exclude(candidates, file, DiagnosticCatalog.OFR2104,
                            $"{string.Join(", ", users)} stay{(users.Count == 1 ? "s" : "")} in {source.Id} and use{(users.Count == 1 ? "s" : "")} it, and {source.Id} cannot reference {destination.Id}, which depends on it.", users);
                    }
                }
            }

            return changed;
        }

        /// <summary>Compiles the moved files in every destination target; returns true when a file had to be excluded.</summary>
        private bool TrialCompile(SortedSet<string> candidates, Dictionary<string, List<ReferenceNeed>> needs)
        {
            var failing = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (tfm, compilation) in destinations)
            {
                var trial = DestinationTrial(tfm, compilation, candidates, needs[tfm], out var moved);
                foreach (var (file, tree) in moved)
                {
                    var errors = trial.GetSemanticModel(tree).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                    if (errors.Count > 0 && !failing.ContainsKey(file))
                    {
                        failing[file] = [.. errors.Take(3).Select(e => $"{tfm}: {e.Id}: {e.GetMessage(CultureInfo.InvariantCulture)}")];
                    }
                }
            }

            foreach (var (file, errors) in failing)
            {
                Exclude(candidates, file, DiagnosticCatalog.OFR2103, $"Does not compile in {destination.Id}.", errors);
            }

            return failing.Count > 0;
        }

        /// <summary>
        /// The source without the moved files (and, when it can, referencing the destination with them)
        /// must compile with no new errors; returns true when files had to be excluded.
        /// </summary>
        private bool SourceCheck(SortedSet<string> candidates, Dictionary<string, List<ReferenceNeed>> needs)
        {
            var moved = candidates.Where(_trees.ContainsKey).Select(f => _trees[f]).ToList();
            if (moved.Count == 0)
            {
                return false;
            }

            var remaining = sourceCompilation.RemoveSyntaxTrees(moved);
            if (!DestinationAboveSource && SourceUsesMoved(candidates))
            {
                var (tfm, compilation) = NearestDestination(sourceTarget);
                var trial = DestinationTrial(tfm, compilation, candidates, needs[tfm], out _)
                    .AddSyntaxTrees(CSharpSyntaxTree.ParseText($"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{sourceCompilation.AssemblyName}\")]", Options(compilation)));
                remaining = remaining.AddReferences(trial.ToMetadataReference());
            }

            static string Key(RoslynDiagnostic d) => d.Id + " " + d.Location.GetLineSpan() + " " + d.GetMessage(CultureInfo.InvariantCulture);
            var before = sourceCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Key).ToHashSet(StringComparer.Ordinal);
            var errors = remaining.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !before.Contains(Key(d))).Take(3)
                .Select(d => $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)} ({Path.GetFileName(d.Location.SourceTree?.FilePath)})")
                .ToList();
            if (errors.Count == 0)
            {
                return false;
            }

            foreach (var file in candidates.ToList())
            {
                Exclude(candidates, file, DiagnosticCatalog.OFR2104, $"{source.Id} does not compile without the moved files, so nothing moves.", errors);
            }

            return true;
        }

        /// <summary>CA1416 (Windows-only APIs) from the destination's own analyzers, for each modern non-Windows target.</summary>
        private void PlatformCheck(SortedSet<string> candidates, Dictionary<string, List<ReferenceNeed>> needs)
        {
            foreach (var (tfm, compilation) in destinations.Where(d => IsModernNonWindows(d.Tfm)))
            {
                if (loader.LoadAnalyzers(destination, tfm) is not { } recorded)
                {
                    continue;
                }

                var analyzers = recorded.Analyzers.Where(a => a.SupportedDiagnostics.Any(d => d.Id == WindowsOnlyRule)).ToImmutableArray();
                if (analyzers.IsEmpty)
                {
                    continue;
                }

                var trial = DestinationTrial(tfm, compilation, candidates, needs[tfm], out var moved);
                var byTree = moved.ToDictionary(m => m.Value, m => m.Key);
                var diagnostics = trial.WithAnalyzers(analyzers, recorded.Options).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
                foreach (var group in diagnostics.Where(d => d.Id == WindowsOnlyRule && d.Location.SourceTree is { } t && byTree.ContainsKey(t))
                    .GroupBy(d => byTree[d.Location.SourceTree!]).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    Bag.Report(DiagnosticCatalog.OFR2105, $"Uses Windows-only APIs, which {destination.Id} ({tfm}) does not guard.",
                        new DiagnosticLocation(source.Id, group.Key),
                        [KeyValuePair.Create<string, JsonNode?>("details", new JsonArray([.. group.Take(3).Select(d => (JsonNode?)d.GetMessage(CultureInfo.InvariantCulture))]))]);
                }
            }
        }

        /// <summary>
        /// Projects referencing the source that use moved types must still see them: through the
        /// source's new reference to the destination (SDK-style projects see references
        /// transitively) or through their own. A dependent that cannot keeps the files it uses
        /// where they are: a cycle (<c>OFR2001</c>) or no compatible target (<c>OFR2104</c>).
        /// Returns true when files had to be excluded.
        /// </summary>
        private bool DependentsCheck(SortedSet<string> candidates)
        {
            _dependentNeeds.Clear();
            var excluded = false;
            foreach (var (dependent, files) in DependentFiles())
            {
                var used = files.Where(candidates.Contains).ToList();
                if (used.Count == 0 || Reach(Model, dependent.Id).Contains(destination.Id))
                {
                    continue;
                }

                List<string>? cycle = DestinationAboveSource && Reach(Model, destination.Id).Contains(dependent.Id)
                    ? [dependent.Id, .. PathBetween(destination.Id, dependent.Id)]
                    : null;
                var direct = DestinationAboveSource || !dependent.SdkStyle;
                var incompatible = cycle is not null ? null : IncompatibleWith(dependent) ?? (direct ? null : IncompatibleWith(source));
                foreach (var file in used)
                {
                    if (cycle is not null)
                    {
                        _cycles.Add(new PlanCycle(file, cycle));
                        excluded |= Exclude(candidates, file, DiagnosticCatalog.OFR2001,
                            $"{dependent.Id} uses it and cannot reference {destination.Id}, which depends on it: {string.Join(" → ", cycle)}.", cycle);
                    }
                    else if (incompatible is not null)
                    {
                        excluded |= Exclude(candidates, file, DiagnosticCatalog.OFR2104,
                            $"{dependent.Id} uses it and cannot reference {destination.Id} for {incompatible}.", [dependent.Id]);
                    }
                }

                if (cycle is null && incompatible is null)
                {
                    _dependentNeeds[dependent.Id] = direct;
                }
            }

            return excluded;
        }

        /// <summary>For each project referencing the source (except the destination), the source files declaring types it uses.</summary>
        private List<(ProjectInfo Dependent, SortedSet<string> Files)> DependentFiles()
        {
            if (_dependentFiles is not null)
            {
                return _dependentFiles;
            }

            var declared = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (file, tree) in _trees)
            {
                var model = sourceCompilation.GetSemanticModel(tree);
                foreach (var declaration in tree.GetRoot().DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                {
                    if (model.GetDeclaredSymbol(declaration)?.GetDocumentationCommentId() is { } id)
                    {
                        (declared.TryGetValue(id, out var files) ? files : declared[id] = []).Add(file);
                    }
                }
            }

            _dependentFiles = [];
            foreach (var dependent in Model.Projects.Where(p => p.ProjectReferences.Contains(source.Id) && p.Id != destination.Id).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var files = new SortedSet<string>(StringComparer.Ordinal);
                if (CompilationLoader.PreferredTarget(dependent) is { } tfm && loader.LoadForProject(dependent, tfm) is { } compilation)
                {
                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        var model = compilation.GetSemanticModel(tree);
                        foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                        {
                            var info = model.GetSymbolInfo(node);
                            foreach (var symbol in info.Symbol is { } found ? [found] : info.CandidateSymbols)
                            {
                                if ((symbol as INamedTypeSymbol ?? symbol.ContainingType) is { } type
                                    && type.ContainingAssembly?.Identity.Name == sourceCompilation.AssemblyName
                                    && type.OriginalDefinition.GetDocumentationCommentId() is { } id && declared.TryGetValue(id, out var declaring))
                                {
                                    files.UnionWith(declaring);
                                }
                            }
                        }
                    }
                }

                _dependentFiles.Add((dependent, files));
            }

            return _dependentFiles;
        }

        /// <summary>The first target of a project that no destination target can serve, or null.</summary>
        private string? IncompatibleWith(ProjectInfo project) =>
            project.CompilerCalls.Keys.Concat(project.TargetFrameworks).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .FirstOrDefault(tfm => Nearest(destinations.Select(d => d.Tfm), tfm) is null);

        private void NamespaceCheck(SortedSet<string> candidates)
        {
            if (request.NamespaceMismatch == "allow")
            {
                return;
            }

            var root = destination.RootNamespace ?? destination.Name;
            foreach (var file in candidates.Where(_trees.ContainsKey).ToList())
            {
                var namespaces = _trees[file].GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Distinct().ToList();
                var outside = namespaces.Where(n => n != root && !n.StartsWith(root + ".", StringComparison.Ordinal)).ToList();
                if (outside.Count == 0)
                {
                    continue;
                }

                var message = $"Declares {string.Join(", ", outside)}, outside {destination.Id}'s root namespace {root}.";
                if (request.NamespaceMismatch == "block")
                {
                    Exclude(candidates, file, DiagnosticCatalog.OFR2120, message, outside);
                }
                else
                {
                    Bag.Report(DiagnosticCatalog.OFR2120, message, new DiagnosticLocation(source.Id, file));
                }
            }
        }

        private MovePlanResult Result(SortedSet<string> candidates, Dictionary<string, List<ReferenceNeed>> needs)
        {
            var moves = candidates
                .Select(f => new PlannedMove
                {
                    File = f, To = Destination(f), CoMoveOf = _coMoveOf.GetValueOrDefault(f), Sha256 = ContentHash.Sha256File(RepoPaths.ToAbsolute(Root, f)),
                    Needs = [.. MovedNeeds(f, candidates)],
                })
                .ToList();
            var edits = Edits(candidates, needs);
            var document = new MovePlanDocument
            {
                From = source.Id,
                To = destination.Id,
                Target = request.Config.TargetFramework,
                WorkspaceHash = request.WorkspaceHash,
                Moves = moves,
                ProjectEdits = edits,
                Excluded = [.. _excluded.Values.OrderBy(e => e.File, StringComparer.Ordinal)],
                Cycles = [.. _cycles.OrderBy(c => c.File, StringComparer.Ordinal)],
                Verify = request.Config.Move.Verify,
            };
            var changeSet = MoveChangeSet.Build(Root, document, new HashSet<string>(StringComparer.Ordinal), out _, Model);
            return new MovePlanResult { Plan = document, Preview = changeSet.Preview() };
        }

        /// <summary>
        /// The other moved files that must move no later than this one, and its resource pair.
        /// Normally those it uses (the destination cannot see the source); when the destination
        /// already depends on the source, those that use it (the source cannot see the destination).
        /// </summary>
        private IEnumerable<string> MovedNeeds(string file, SortedSet<string> candidates)
        {
            IEnumerable<string> needed = DestinationAboveSource
                ? _uses.Where(u => u.Value.Files.Contains(file)).Select(u => u.Key)
                : _uses.TryGetValue(file, out var uses) ? uses.Files : [];
            return needed.Concat(Pairs(file)).Where(f => f != file && candidates.Contains(f)).Distinct().Order(StringComparer.Ordinal);
        }

        private List<ProjectEdit> Edits(SortedSet<string> candidates, Dictionary<string, List<ReferenceNeed>> needs)
        {
            var edits = new List<ProjectEdit>();
            if (candidates.Count == 0)
            {
                return edits;
            }

            var all = needs.Values.SelectMany(n => n).ToList();
            foreach (var project in all.Where(n => n.Project is not null && n.Project != source.Id).Select(n => n.Project!).Distinct().Order(StringComparer.Ordinal))
            {
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddProjectReference, Value = project });
            }

            foreach (var package in all.Where(n => n.Package is not null).GroupBy(n => n.Package!, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddPackageReference, Value = package.Key, Version = package.First().Version });
            }

            if (!DestinationAboveSource && (SourceUsesMoved(candidates) || _dependentNeeds.Count > 0) && !source.ProjectReferences.Contains(destination.Id))
            {
                edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.AddProjectReference, Value = destination.Id });
            }

            foreach (var (dependent, _) in _dependentNeeds.Where(d => d.Value))
            {
                edits.Add(new ProjectEdit { Project = dependent, Kind = ProjectEditKind.AddProjectReference, Value = destination.Id });
            }

            if (!DestinationAboveSource && SourceUsesMovedInternals(candidates))
            {
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddInternalsVisibleTo, Value = sourceCompilation.AssemblyName });
            }

            if (DestinationAboveSource && MovedUsesSourceInternals(candidates))
            {
                edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.AddInternalsVisibleTo, Value = destinations[0].Compilation.AssemblyName });
            }

            foreach (var file in candidates.Where(f => f.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)))
            {
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.KeepResourceName, Value = Destination(file), Version = ManifestName(file) });
            }

            return edits;
        }

        // ----- what files use -----

        private sealed record FileUses(HashSet<string> Files, HashSet<string> Projects, List<PackageUse> Packages, HashSet<ISymbol> Internals);

        private sealed record PackageUse(string Id, string Version, string? Folder);

        private FileUses Uses(SyntaxTree tree)
        {
            var model = sourceCompilation.GetSemanticModel(tree);
            var files = new HashSet<string>(StringComparer.Ordinal);
            var projects = new HashSet<string>(StringComparer.Ordinal);
            var packages = new List<PackageUse>();
            var internals = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var assemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var info = model.GetSymbolInfo(node);
                foreach (var symbol in info.Symbol is { } s ? [s] : info.CandidateSymbols)
                {
                    var definition = symbol.OriginalDefinition is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol.OriginalDefinition;
                    if (definition.Kind is not (SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event))
                    {
                        continue;
                    }

                    if (SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, sourceCompilation.Assembly))
                    {
                        foreach (var reference in definition.DeclaringSyntaxReferences)
                        {
                            if (FileOf(reference.SyntaxTree) is { } file)
                            {
                                files.Add(file);
                            }
                        }

                        if (IsInternal(definition))
                        {
                            internals.Add(definition);
                        }
                    }
                    else if (definition.ContainingAssembly is { } assembly)
                    {
                        assemblies.Add(assembly);
                    }
                }
            }

            foreach (var assembly in assemblies)
            {
                var project = Model.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName ?? p.Name, assembly.Identity.Name, StringComparison.OrdinalIgnoreCase));
                if (project is not null)
                {
                    projects.Add(project.Id);
                }
                else if (Package(assembly) is { } package)
                {
                    packages.Add(package);
                }
            }

            return new FileUses(files, projects, packages, internals);
        }

        private PackageIndex? _packages;

        private PackageUse? Package(IAssemblySymbol assembly)
        {
            _packages ??= PackageIndex.For(Root, source);
            return _packages.Find(assembly.Identity.Name) is { } package ? new PackageUse(package.Id, package.Version, package.Folder) : null;
        }

        private sealed record ReferenceNeed(string? Project, string? Package, string? Version, MetadataReference Reference);

        /// <summary>For each destination target, the references the moved files need that the destination lacks.</summary>
        private Dictionary<string, List<ReferenceNeed>> NeedsPerTarget(SortedSet<string> candidates)
        {
            var uses = candidates.Where(_uses.ContainsKey).Select(f => _uses[f]).ToList();
            var projects = uses.SelectMany(u => u.Projects).Where(p => p != destination.Id && p != source.Id && !destination.ProjectReferences.Contains(p)).Distinct().Order(StringComparer.Ordinal).ToList();
            var packages = uses.SelectMany(u => u.Packages).Where(p => !DestinationHas(p.Id)).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new Dictionary<string, List<ReferenceNeed>>(StringComparer.Ordinal);
            foreach (var (tfm, _) in destinations)
            {
                var needs = new List<ReferenceNeed>();
                foreach (var project in projects)
                {
                    var target = Model.Projects.Single(p => p.Id == project);
                    var nearest = Nearest(target.CompilerCalls.Keys, tfm);
                    if (nearest is not null && loader.LoadForProject(target, nearest) is { } compilation)
                    {
                        needs.Add(new ReferenceNeed(project, null, null, compilation.ToMetadataReference()));
                    }
                }

                foreach (var package in packages)
                {
                    foreach (var asset in PackageAssets(package, tfm) ?? [])
                    {
                        needs.Add(new ReferenceNeed(null, package.Id, package.Version, MetadataReference.CreateFromFile(asset)));
                    }
                }

                result[tfm] = needs;
            }

            return result;
        }

        /// <summary>The destination's compilation for a target as it would be after the move.</summary>
        private CSharpCompilation DestinationTrial(string tfm, CSharpCompilation compilation, SortedSet<string> candidates, List<ReferenceNeed> needs, out Dictionary<string, SyntaxTree> moved)
        {
            var options = Options(compilation);
            moved = candidates.Where(_trees.ContainsKey).ToDictionary(f => f, f => CSharpSyntaxTree.ParseText(_trees[f].GetText(), options, RepoPaths.ToAbsolute(Root, Destination(f))), StringComparer.Ordinal);
            var references = compilation.References.ToList();
            if (DestinationAboveSource)
            {
                var trimmed = sourceCompilation.RemoveSyntaxTrees(candidates.Where(_trees.ContainsKey).Select(f => _trees[f]))
                    .AddSyntaxTrees(CSharpSyntaxTree.ParseText($"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{compilation.AssemblyName}\")]", Options(sourceCompilation)));
                references = [.. references.Where(r => (compilation.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol)?.Identity.Name != sourceCompilation.AssemblyName), trimmed.ToMetadataReference()];
            }

            return compilation.WithReferences([.. references, .. needs.Select(n => n.Reference)]).AddSyntaxTrees(moved.Values);
        }

        // ----- helpers -----

        private IEnumerable<string> Pairs(string file)
        {
            var folder = Folder(file);
            var name = file[(file.LastIndexOf('/') + 1)..];
            var stem = name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ? name[..^".Designer.cs".Length]
                : name.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) ? name[..name.IndexOf('.')]
                : null;
            if (stem is null)
            {
                return [];
            }

            var directory = RepoPaths.ToAbsolute(Root, folder.Length == 0 ? "." : folder);
            var siblings = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>() : [];
            return siblings
                .Where(s => s.Equals(stem + ".Designer.cs", StringComparison.OrdinalIgnoreCase)
                    || (s.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && s.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)))
                .Select(s => (folder.Length == 0 ? "" : folder + "/") + s)
                .Where(s => s != file)
                .Order(StringComparer.Ordinal);
        }

        private IEnumerable<string> PartialSiblings(SyntaxTree tree)
        {
            var model = sourceCompilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                if (model.GetDeclaredSymbol(declaration) is not { } type)
                {
                    continue;
                }

                foreach (var reference in type.DeclaringSyntaxReferences)
                {
                    if (FileOf(reference.SyntaxTree) is { } file && reference.SyntaxTree != tree)
                    {
                        yield return file;
                    }
                }
            }
        }

        private bool Exclude(SortedSet<string> candidates, string file, DiagnosticDescriptor code, string message, List<string> details)
        {
            if (!candidates.Remove(file))
            {
                return false;
            }

            _excluded[file] = new ExcludedMove { File = file, Code = code.Code, Message = message, Details = details };
            Bag.Report(code, message, new DiagnosticLocation(source.Id, file),
                details.Count == 0 ? null : [KeyValuePair.Create<string, JsonNode?>("details", new JsonArray([.. details.Select(d => (JsonNode?)d)]))]);

            // Its pair and partial siblings stay with it; files co-moved only for it stay too.
            foreach (var partner in Pairs(file).Concat(_trees.TryGetValue(file, out var tree) ? PartialSiblings(tree) : []).Where(candidates.Contains).ToList())
            {
                Exclude(candidates, partner, code, $"Moves only with {file}, which stays.", [file]);
            }

            foreach (var dependent in _coMoveOf.Where(c => c.Value == file && candidates.Contains(c.Key)).Select(c => c.Key).ToList())
            {
                candidates.Remove(dependent);
                _coMoveOf.Remove(dependent);
            }

            return true;
        }

        private bool SourceUsesMoved(SortedSet<string> candidates) =>
            _uses.Any(u => !candidates.Contains(u.Key) && u.Value.Files.Any(candidates.Contains));

        private bool SourceUsesMovedInternals(SortedSet<string> candidates) =>
            _uses.Where(u => !candidates.Contains(u.Key))
                .SelectMany(u => u.Value.Internals)
                .Any(s => s.DeclaringSyntaxReferences.Any(r => FileOf(r.SyntaxTree) is { } f && candidates.Contains(f)));

        private bool MovedUsesSourceInternals(SortedSet<string> candidates) =>
            candidates.Where(_uses.ContainsKey)
                .SelectMany(f => _uses[f].Internals)
                .Any(s => s.DeclaringSyntaxReferences.Any(r => FileOf(r.SyntaxTree) is { } f && !candidates.Contains(f)));

        private static bool IsInternal(ISymbol symbol)
        {
            for (var s = symbol; s is not null && s is not INamespaceSymbol; s = s.ContainingSymbol)
            {
                if (s.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedAndInternal)
                {
                    return true;
                }
            }

            return false;
        }

        private string? FileOf(SyntaxTree tree) =>
            _trees.FirstOrDefault(t => ReferenceEquals(t.Value, tree)).Key;

        private bool DestinationHas(string package) =>
            destination.Resolved.Values.SelectMany(f => f.Packages).Any(p => string.Equals(p.Id, package, StringComparison.OrdinalIgnoreCase));

        /// <summary>Compile-time assets of a package for a target (ref/ over lib/); null when the package cannot be found locally, empty when it has none that fit.</summary>
        private static List<string>? PackageAssets(PackageUse package, string tfm)
        {
            var folder = package.Folder ?? GlobalPackageFolder(package);
            if (folder is null)
            {
                return null;
            }

            var framework = NuGetFramework.Parse(tfm);
            foreach (var kind in new[] { "ref", "lib" })
            {
                var root = Path.Combine(folder, kind);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                var folders = Directory.EnumerateDirectories(root).Select(d => new Candidate<string>(d, NuGetFramework.ParseFolder(Path.GetFileName(d)))).ToList();
                var nearest = NuGetFrameworkUtility.GetNearest(folders, framework, f => f.Framework);
                if (nearest is not null)
                {
                    return [.. Directory.EnumerateFiles(nearest.Value, "*.dll").Order(StringComparer.Ordinal)];
                }
            }

            return [];
        }

        private static string? GlobalPackageFolder(PackageUse package)
        {
            var settings = NuGet.Configuration.Settings.LoadDefaultSettings(null);
            var folder = Path.Combine(NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings), package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
            return Directory.Exists(folder) ? folder : null;
        }

        private string? IncompatibleTarget(ProjectInfo target) =>
            destinations.Select(d => d.Tfm).FirstOrDefault(tfm => Nearest(target.CompilerCalls.Keys.Concat(target.TargetFrameworks).Distinct(), tfm) is null);

        private static string? Nearest(IEnumerable<string> available, string tfm)
        {
            var framework = NuGetFramework.Parse(tfm);
            var candidates = available.Select(a => new Candidate<string>(a, NuGetFramework.Parse(a))).ToList();
            return NuGetFrameworkUtility.GetNearest(candidates, framework, c => c.Framework)?.Value;
        }

        private (string Tfm, CSharpCompilation Compilation) NearestDestination(string tfm)
        {
            var candidates = destinations.Select(d => new Candidate<(string Tfm, CSharpCompilation Compilation)>(d, NuGetFramework.Parse(d.Tfm))).ToList();
            return NuGetFrameworkUtility.GetNearest(candidates, NuGetFramework.Parse(tfm), c => c.Framework)?.Value ?? destinations[0];
        }

        private static bool IsModernNonWindows(string tfm)
        {
            var framework = NuGetFramework.Parse(tfm);
            return framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp && framework.Version.Major >= 5
                && !string.Equals(framework.Platform, "windows", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The shortest dependency path from one project to another, both included.</summary>
        private List<string> PathBetween(string from, string to)
        {
            var dependencies = Model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
            var previous = new Dictionary<string, string>(StringComparer.Ordinal);
            var queue = new Queue<string>([from]);
            var seen = new HashSet<string>(StringComparer.Ordinal) { from };
            while (queue.Count > 0 && !seen.Contains(to))
            {
                var next = queue.Dequeue();
                foreach (var further in dependencies[next].Order(StringComparer.Ordinal))
                {
                    if (seen.Add(further))
                    {
                        previous[further] = next;
                        queue.Enqueue(further);
                    }
                }
            }

            var path = new List<string> { to };
            for (var node = to; node != from && previous.TryGetValue(node, out var back); node = back)
            {
                path.Insert(0, back);
            }

            return path;
        }

        private List<string> CyclePath(string project)
        {
            // destination → project → … → destination, along the shortest path back.
            var dependencies = Model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
            var previous = new Dictionary<string, string>(StringComparer.Ordinal);
            var queue = new Queue<string>([project]);
            var seen = new HashSet<string>(StringComparer.Ordinal) { project };
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (next == destination.Id)
                {
                    break;
                }

                foreach (var further in dependencies[next].Order(StringComparer.Ordinal))
                {
                    if (seen.Add(further))
                    {
                        previous[further] = next;
                        queue.Enqueue(further);
                    }
                }
            }

            var path = new List<string> { destination.Id };
            for (var node = destination.Id; previous.TryGetValue(node, out var back); node = back)
            {
                path.Insert(0, back);
            }

            path.Insert(0, destination.Id);
            return path;
        }

        private string? DestinationExcludes(string moved)
        {
            var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(Root, destination.Id));
            var relative = moved[(Folder(destination.Id).Length == 0 ? 0 : Folder(destination.Id).Length + 1)..];
            return ProjectFileEditor.Load(bytes).RemovePatterns("Compile").FirstOrDefault(p => new PathGlobs([p]).Matches(relative));
        }

        /// <summary>The destination path: the path under the source's folder, under the destination's folder.</summary>
        private string Destination(string file)
        {
            var from = Folder(source.Id);
            var relative = from.Length == 0 ? file : file[(from.Length + 1)..];
            var to = Folder(destination.Id);
            return to.Length == 0 ? relative : to + "/" + relative;
        }

        /// <summary>The manifest resource name MSBuild gives a .resx in the source: root namespace plus folders.</summary>
        private string ManifestName(string file)
        {
            var from = Folder(source.Id);
            var relative = from.Length == 0 ? file : file[(from.Length + 1)..];
            var withoutExtension = relative[..relative.LastIndexOf('.')];
            return (source.RootNamespace ?? source.Name) + "." + withoutExtension.Replace('/', '.') + ".resources";
        }

        private static CSharpParseOptions Options(CSharpCompilation compilation) =>
            (CSharpParseOptions?)compilation.SyntaxTrees.FirstOrDefault()?.Options ?? CSharpParseOptions.Default;
    }

    /// <summary>A value with the framework NuGet compares it by (GetNearest needs a reference type).</summary>
    private sealed record Candidate<T>(T Value, NuGetFramework Framework);

    internal static HashSet<string> Reach(WorkspaceModel model, string project)
    {
        var dependencies = model.Graph.Edges.ToLookup(e => e.From, e => e.To, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(dependencies[project]);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (seen.Add(next))
            {
                foreach (var further in dependencies[next])
                {
                    pending.Push(further);
                }
            }
        }

        return seen;
    }

    private static bool Frozen(OfframpConfig config, string project) =>
        config.Projects.Any(p => p.Frozen && new PathGlobs([p.Path]).Matches(project));

    private static bool Inside(string file, string project) =>
        Folder(project).Length == 0 || file.StartsWith(Folder(project) + "/", StringComparison.Ordinal);

    private static bool IsResource(string root, ProjectInfo project, string file) =>
        file.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) && Inside(file, project.Id) && File.Exists(RepoPaths.ToAbsolute(root, file));

    private static string Folder(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Full(a), Full(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Full(string path) => (Path.IsPathRooted(path) ? Path.GetFullPath(path) : path).Replace('\\', '/');
}
