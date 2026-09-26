using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.TestCode;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Model;
using DiagnosticDescriptor = Offramp.Core.Diagnostics.DiagnosticDescriptor;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Offramp.Refactoring.Moves;

public sealed record MoveTestsRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    /// <summary>The source project, resolved.</summary>
    public required string Source { get; init; }

    /// <summary>The destination given with <c>--to</c>, resolved, or null.</summary>
    public string? To { get; init; }

    public bool Create { get; init; }

    /// <summary>The lowest helper confidence that moves (<c>--include-helpers</c>); null moves no helpers.</summary>
    public TestConfidence? IncludeHelpers { get; init; } = TestConfidence.High;

    public bool PrunePackages { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>The plan's data and the change set that carries it out; null change set when nothing can be planned.</summary>
public sealed record MoveTestsPlan(MoveTestsResult Result, ChangeSet? ChangeSet);

/// <summary>
/// Plans <c>move tests</c> (docs/spec/commands/move.md#move-tests): finds test code, chooses
/// the destination, proves each file compiles there by trial compilation and that the source
/// still compiles without it, and lists the project edits. Moved files are never edited.
/// </summary>
public static class TestMovePlanner
{
    private static readonly string[] CopiedProperties = ["LangVersion", "Nullable", "ImplicitUsings"];

    private static readonly Dictionary<TestConfidence, int> Rank = new()
    {
        [TestConfidence.Certain] = 0, [TestConfidence.High] = 1, [TestConfidence.Medium] = 2, [TestConfidence.Low] = 3,
    };

    public static async Task<MoveTestsPlan?> PlanAsync(MoveTestsRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model;
        var source = model.Projects.Single(p => p.Id == request.Source);
        var bag = request.Diagnostics;
        if (source.Language != "csharp")
        {
            bag.Report(DiagnosticCatalog.OFR2205, $"{source.Id} is a {source.Language} project; move tests analyzes C# only.", new DiagnosticLocation(source.Id));
            return null;
        }

        var tests = request.Config.Move.Tests;
        var target = TestTargets.Select(model, source, request.To, request.Create, tests.TargetSuffix);
        if (target.Project == source.Id)
        {
            bag.Report(DiagnosticCatalog.OFR2002, $"The destination is the source project itself ({source.Id}).", new DiagnosticLocation(source.Id));
            return null;
        }

        if (target.Ambiguous.Count > 0)
        {
            bag.Report(DiagnosticCatalog.OFR2202, $"Several projects could take the tests of {source.Name}: {string.Join(", ", target.Ambiguous)}. Choose one with --to.",
                new DiagnosticLocation(source.Id), [KeyValuePair.Create<string, JsonNode?>("candidates", new JsonArray([.. target.Ambiguous.Select(a => (JsonNode?)a)]))]);
            return new MoveTestsPlan(Empty(source.Id), null);
        }

        if (target.Project is null)
        {
            bag.Report(DiagnosticCatalog.OFR2203,
                $"No project is named {source.Name}{tests.TargetSuffix}. Name one with --to, or pass --create to create it next to {source.Id}.", new DiagnosticLocation(source.Id));
            return new MoveTestsPlan(Empty(source.Id), null);
        }

        using var loader = new CompilationLoader(request.RepositoryRoot);
        var tfm = CompilationLoader.PreferredTarget(source);
        var sourceCompilation = tfm is null ? null : loader.LoadForProject(source, tfm) as CSharpCompilation;
        if (sourceCompilation is null)
        {
            bag.Report(DiagnosticCatalog.OFR0004, $"The compiler log has no compilation for {source.Id}; run `offramp scan` again.", new DiagnosticLocation(source.Id));
            return null;
        }

        var destination = target.Create ? null : model.Projects.Single(p => p.Id == target.Project);
        var destinationCompilation = destination is null ? null
            : (loader.LoadForProject(destination, tfm!) ?? loader.LoadForProject(destination)) as CSharpCompilation;
        var consumers = Dependents(model, source.Id)
            .Select(id => model.Projects.Single(p => p.Id == id))
            .Select(p => (Project: p, Compilation: loader.LoadForProject(p, tfm!) ?? loader.LoadForProject(p)))
            .Where(c => c.Compilation is not null)
            .Select(c => new ConsumerCompilation(c.Project.Id, c.Compilation!))
            .ToList();

        var files = source.Compile.ToDictionary(f => f, f => RepoPaths.ToAbsolute(request.RepositoryRoot, f), StringComparer.Ordinal);
        var classified = TestCodeClassifier.Classify(sourceCompilation, files, consumers, target.Project);
        var context = new Context(request, source, target.Project, target.Create, sourceCompilation, destination, destinationCompilation, classified);
        return await BuildAsync(context, cancellationToken);
    }

    private sealed record Context(
        MoveTestsRequest Request, ProjectInfo Source, string Destination, bool Create, CSharpCompilation SourceCompilation,
        ProjectInfo? DestinationProject, CSharpCompilation? DestinationCompilation, IReadOnlyList<ClassifiedFile> Classified)
    {
        public string DestinationName => DestinationProject?.AssemblyName ?? DestinationProject?.Name ?? Path.GetFileNameWithoutExtension(Destination);

        public DiagnosticBag Bag => Request.Diagnostics;

        public string Root => Request.RepositoryRoot;
    }

    private static async Task<MoveTestsPlan> BuildAsync(Context context, CancellationToken cancellationToken)
    {
        var skipped = new List<SkippedFile>();
        var candidates = new List<CandidateFile>();
        var chosen = Choose(context, skipped, candidates);
        var frameworks = context.Classified.Where(c => c.Kind == TestFileKind.Test).SelectMany(c => c.Frameworks)
            .GroupBy(f => f, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
        var framework = frameworks.FirstOrDefault();

        // Map paths, then prove the set compiles in the destination and the source compiles without it.
        var destinations = MapDestinations(context, chosen, skipped);
        var trees = chosen.Where(c => destinations.ContainsKey(c.File))
            .ToDictionary(c => c.File, c => context.SourceCompilation.SyntaxTrees.First(t => Same(t.FilePath, RepoPaths.ToAbsolute(context.Root, c.File))), StringComparer.Ordinal);
        var needsInternals = trees.Keys.Where(f => UsesInternals(context, trees[f], trees.Values)).ToHashSet(StringComparer.Ordinal);
        var ivtBlocked = InternalsBlocked(context);
        foreach (var file in needsInternals.Where(_ => ivtBlocked is not null).ToList())
        {
            Skip(context, skipped, file, DiagnosticCatalog.OFR2103, $"Uses internals of {context.Source.Name}, but {ivtBlocked}.", []);
            trees.Remove(file);
        }

        var references = TrialCompile(context, trees, skipped);
        KeepIfSourceBreaks(context.SourceCompilation, trees, context.Source.Id, context.Bag, skipped);

        var moves = chosen.Where(c => trees.ContainsKey(c.File))
            .Select(c => new MovedFile { File = c.File, To = destinations[c.File], Kind = c.Kind, Confidence = c.Confidence!.Value, Reasons = c.Reasons })
            .OrderBy(m => m.File, StringComparer.Ordinal)
            .ToList();
        var prunable = moves.Count == 0 ? [] : Prunable(context, trees.Values);
        if (prunable.Count > 0)
        {
            context.Bag.Report(DiagnosticCatalog.OFR2210,
                $"Nothing left in {context.Source.Name} uses the test framework; {string.Join(", ", prunable)} can be removed{(context.Request.PrunePackages ? " (removing)" : " with --prune-packages")}.",
                new DiagnosticLocation(context.Source.Id), [KeyValuePair.Create<string, JsonNode?>("packages", new JsonArray([.. prunable.Select(p => (JsonNode?)p)]))]);
        }

        var (changeSet, edits) = moves.Count == 0 ? (null, [])
            : await ChangesAsync(context, moves, framework, references, moves.Any(m => needsInternals.Contains(m.File)), prunable, cancellationToken);
        return new MoveTestsPlan(
            new MoveTestsResult
            {
                Project = context.Source.Id,
                To = context.Destination,
                Created = context.Create && moves.Count > 0,
                Framework = framework,
                Moves = moves,
                Skipped = [.. skipped.OrderBy(s => s.File, StringComparer.Ordinal)],
                Candidates = [.. candidates.OrderBy(c => c.File, StringComparer.Ordinal)],
                ProjectEdits = edits,
                Prunable = prunable,
            },
            changeSet);
    }

    /// <summary>Tests and helpers at or above the confidence asked for, less those production code uses.</summary>
    private static List<ClassifiedFile> Choose(Context context, List<SkippedFile> skipped, List<CandidateFile> candidates)
    {
        var chosen = new List<ClassifiedFile>();
        var threshold = context.Request.IncludeHelpers;
        foreach (var file in context.Classified.Where(c => c.Kind != TestFileKind.Production))
        {
            if (file.ProductionReferrers.Count > 0)
            {
                Skip(context, skipped, file.File, DiagnosticCatalog.OFR2201,
                    $"{(file.Kind == TestFileKind.Test ? "A test" : "A helper")} that {string.Join(", ", file.ProductionReferrers)} use{(file.ProductionReferrers.Count == 1 ? "s" : "")}; it stays.",
                    file.ProductionReferrers);
            }
            else if (file.Kind == TestFileKind.Test || (threshold is { } t && Rank[file.Confidence!.Value] <= Rank[t]))
            {
                chosen.Add(file);
            }
            else if (file.Confidence == TestConfidence.Medium)
            {
                // Worth a review; low candidates (unused, with no sign of test support) are just unused code.
                candidates.Add(new CandidateFile(file.File, file.Confidence.Value, file.Reasons));
            }
        }

        return chosen;
    }

    private static Dictionary<string, string> MapDestinations(Context context, List<ClassifiedFile> chosen, List<SkippedFile> skipped)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in chosen.OrderBy(c => c.File, StringComparer.Ordinal))
        {
            var to = TestTargets.MapPath(file.File, context.Source.Id, context.Destination, context.Request.Config.Move.Tests.StripTestsSegment);
            if (to is null)
            {
                Skip(context, skipped, file.File, DiagnosticCatalog.OFR2206, $"Linked from outside {context.Source.Id}'s folder; it stays.", []);
            }
            else if (File.Exists(RepoPaths.ToAbsolute(context.Root, to)) || !taken.Add(to))
            {
                Skip(context, skipped, file.File, DiagnosticCatalog.OFR2204, $"Would move to {to}, which is already taken; it stays.", [to]);
            }
            else
            {
                result[file.File] = to;
            }
        }

        return result;
    }

    /// <summary>
    /// Compiles the moved files in the destination (with the source minus those files, and the
    /// packages and projects the move would add) until every remaining file compiles; returns the
    /// references the destination needs.
    /// </summary>
    private static List<ReferenceNeed> TrialCompile(Context context, Dictionary<string, SyntaxTree> trees, List<SkippedFile> skipped)
    {
        var needs = Needs(context, trees.Values);
        var failures = Failures(context, trees, needs);
        while (failures.Count > 0)
        {
            foreach (var failure in failures)
            {
                Skip(context, skipped, failure.File, DiagnosticCatalog.OFR2103, $"Does not compile in {context.Destination}.", failure.Errors);
                trees.Remove(failure.File);
            }

            needs = Needs(context, trees.Values);
            failures = Failures(context, trees, needs);
        }

        return needs;
    }

    private sealed record TrialFailure(string File, List<string> Errors);

    /// <summary>The moved files that do not compile in the destination with the given references.</summary>
    private static List<TrialFailure> Failures(Context context, Dictionary<string, SyntaxTree> trees, List<ReferenceNeed> needs)
    {
        var remaining = context.SourceCompilation
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{context.DestinationName}\")]",
                (CSharpParseOptions)context.SourceCompilation.SyntaxTrees.First().Options))
            .RemoveSyntaxTrees(trees.Values);
        var options = DestinationParseOptions(context);
        var moved = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        foreach (var (file, tree) in trees)
        {
            moved[file] = CSharpSyntaxTree.ParseText(tree.GetText(), options, tree.FilePath);
        }

        var trial = Destination(context, remaining, needs).AddSyntaxTrees(moved.Values);
        var failures = new List<TrialFailure>();
        foreach (var (file, tree) in moved)
        {
            var errors = trial.GetSemanticModel(tree).GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                failures.Add(new TrialFailure(file, [.. errors.Take(3).Select(e => $"{e.Id}: {e.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")]));
            }
        }

        return failures;
    }

    private static CSharpParseOptions DestinationParseOptions(Context context)
    {
        var tree = context.DestinationCompilation?.SyntaxTrees.FirstOrDefault();
        return tree?.Options as CSharpParseOptions ?? (CSharpParseOptions)context.SourceCompilation.SyntaxTrees.First().Options;
    }

    /// <summary>The destination's compilation as it would be: its own references, the trimmed source, and what the move adds.</summary>
    private static CSharpCompilation Destination(Context context, CSharpCompilation remaining, List<ReferenceNeed> needs)
    {
        var sourceName = context.SourceCompilation.AssemblyName;
        if (context.DestinationCompilation is { } existing)
        {
            var kept = existing.References.Where(r => Name(existing, r) != sourceName);
            return existing.WithReferences([.. kept, remaining.ToMetadataReference(), .. needs.Where(n => n.Origin != ReferenceOrigin.Framework).Select(n => n.Reference)]);
        }

        // A new project: the source's .NET Framework references (the template copies them) plus what the files need.
        var framework = context.SourceCompilation.References.Where(r => Origin(context, context.SourceCompilation, r).Origin == ReferenceOrigin.Framework);
        return CSharpCompilation.Create(
            context.DestinationName,
            context.SourceCompilation.SyntaxTrees.Where(t => t.FilePath.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)),
            [.. framework, remaining.ToMetadataReference(), .. needs.Where(n => n.Origin != ReferenceOrigin.Framework).Select(n => n.Reference)],
            context.SourceCompilation.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// When the source no longer compiles without the moved files (and cannot reference the
    /// destination, which references it), keeps every file where it is (<c>OFR2104</c>).
    /// </summary>
    internal static void KeepIfSourceBreaks(CSharpCompilation source, Dictionary<string, SyntaxTree> trees, string project, DiagnosticBag bag, List<SkippedFile> skipped)
    {
        if (trees.Count == 0 || SourceErrors(source, trees.Values) is not { Count: > 0 } errors)
        {
            return;
        }

        foreach (var file in trees.Keys.Order(StringComparer.Ordinal))
        {
            Skip(bag, project, skipped, file, DiagnosticCatalog.OFR2104, $"{Path.GetFileNameWithoutExtension(project)} does not compile without the moved files, so nothing moves.", errors);
        }

        trees.Clear();
    }

    /// <summary>Errors in the source's remaining files that were not there before the files left.</summary>
    internal static List<string> SourceErrors(CSharpCompilation source, IEnumerable<SyntaxTree> moved)
    {
        static string Key(RoslynDiagnostic d) => d.Id + " " + d.Location.GetLineSpan() + " " + d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        var before = source.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(Key).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. source.RemoveSyntaxTrees(moved).GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && !before.Contains(Key(d)))
                .Take(3)
                .Select(d => $"{d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)} ({Path.GetFileName(d.Location.SourceTree?.FilePath)})"),
        ];
    }

    private enum ReferenceOrigin
    {
        Framework,
        Package,
        Project,
    }

    /// <summary>A reference the moved files use and the destination lacks, and what supplies it.</summary>
    private sealed record ReferenceNeed(MetadataReference Reference, ReferenceOrigin Origin, string? Package, string? PackageVersion, string? Project);

    private static List<ReferenceNeed> Needs(Context context, IEnumerable<SyntaxTree> trees)
    {
        var source = context.SourceCompilation;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in trees)
        {
            var model = source.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (model.GetSymbolInfo(node).Symbol?.ContainingAssembly?.Name is { } assembly)
                {
                    used.Add(assembly);
                }
            }
        }

        var present = context.DestinationCompilation is { } destination
            ? destination.References.Select(r => Name(destination, r)).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        return
        [
            .. source.References
                .Select(r => (Reference: r, Name: Name(source, r)))
                .Where(r => r.Name is not null && used.Contains(r.Name) && !present.Contains(r.Name) && r.Name != source.AssemblyName)
                .Select(r => Origin(context, source, r.Reference) with { Reference = r.Reference }),
        ];
    }

    private static ReferenceNeed Origin(Context context, Compilation compilation, MetadataReference reference)
    {
        var name = Name(compilation, reference);
        var project = context.Request.Model.Projects.FirstOrDefault(p => string.Equals(p.AssemblyName ?? p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            return new ReferenceNeed(reference, ReferenceOrigin.Project, null, null, project.Id);
        }

        var segments = reference.Display?.Replace('\\', '/').Split('/') ?? [];
        var resolved = context.Source.Resolved.Values.SelectMany(f => f.Packages).ToList();
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            var package = resolved.FirstOrDefault(p => string.Equals(p.Id, segments[i], StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Version, segments[i + 1], StringComparison.OrdinalIgnoreCase));
            if (package is not null)
            {
                return new ReferenceNeed(reference, ReferenceOrigin.Package, package.Id, package.Version, null);
            }
        }

        return new ReferenceNeed(reference, ReferenceOrigin.Framework, null, null, null);
    }

    private static string? Name(Compilation compilation, MetadataReference reference) =>
        (compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol)?.Identity.Name;

    /// <summary>True when the file uses a non-public member or type of the source outside the moved files.</summary>
    private static bool UsesInternals(Context context, SyntaxTree tree, IEnumerable<SyntaxTree> moving)
    {
        var moved = moving.ToHashSet();
        var model = context.SourceCompilation.GetSemanticModel(tree);
        foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is null || !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, context.SourceCompilation.Assembly)
                || symbol.DeclaringSyntaxReferences.All(r => moved.Contains(r.SyntaxTree)))
            {
                continue;
            }

            for (var s = symbol; s is not null && s is not INamespaceSymbol; s = s.ContainingSymbol)
            {
                if (s.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedAndInternal)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Why InternalsVisibleTo cannot be added, or null when it can (or is already there).</summary>
    private static string? InternalsBlocked(Context context)
    {
        if (context.SourceCompilation.Assembly.Identity.IsStrongName)
        {
            return $"{context.Source.Name} is strong-named, and InternalsVisibleTo would need the destination's public key";
        }

        return !context.Source.SdkStyle && AssemblyInfo(context) is null
            ? $"{context.Source.Id} is not SDK-style and has no Properties/AssemblyInfo.cs to add InternalsVisibleTo to"
            : null;
    }

    private static string? AssemblyInfo(Context context) =>
        context.Source.Compile.FirstOrDefault(f => f.EndsWith("/AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase));

    private static bool HasInternalsVisibleTo(Context context) =>
        context.SourceCompilation.Assembly.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "InternalsVisibleToAttribute"
            && a.ConstructorArguments.FirstOrDefault().Value is string target
            && string.Equals(target.Split(',')[0].Trim(), context.DestinationName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The source's test-framework packages, when nothing left in it uses a test framework.</summary>
    private static List<string> Prunable(Context context, IEnumerable<SyntaxTree> moved)
    {
        var remaining = context.SourceCompilation.RemoveSyntaxTrees(moved);
        foreach (var tree in remaining.SyntaxTrees)
        {
            var model = remaining.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (model.GetSymbolInfo(node).Symbol?.ContainingAssembly?.Name is { } assembly
                    && (TestCodeClassifier.TestAssemblies.ContainsKey(assembly) || assembly.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)))
                {
                    return [];
                }
            }
        }

        return [.. context.Source.PackageReferences.Select(p => p.Id)
            .Where(id => ProjectKindDetector.TestPackages.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<(ChangeSet, IReadOnlyList<ProjectEdit>)> ChangesAsync(
        Context context, List<MovedFile> moves, string? framework, List<ReferenceNeed> needs, bool internals, List<string> prunable, CancellationToken cancellationToken)
    {
        var changeSet = new ChangeSet();
        var edits = new List<ProjectEdit>();
        var packages = PackagesToAdd(context, needs);
        var projects = needs.Where(n => n.Origin == ReferenceOrigin.Project && n.Project != context.Source.Id).Select(n => n.Project!).Distinct().Order(StringComparer.Ordinal).ToList();

        if (context.Create)
        {
            var content = TestProjectTemplate.Render(Template(context, framework, packages));
            changeSet.Create(context.Destination, content);
            edits.Add(new ProjectEdit { Project = context.Destination, Kind = ProjectEditKind.CreateProject, Value = framework });
            foreach (var project in projects)
            {
                edits.Add(new ProjectEdit { Project = context.Destination, Kind = ProjectEditKind.AddProjectReference, Value = project });
            }

            if (context.Request.Model.Solution is { } solution)
            {
                foreach (var (path, before, after) in await SolutionEditor.AddProjectAsync(context.Root, solution, context.Destination, cancellationToken))
                {
                    changeSet.Edit(path, before, after);
                    edits.Add(new ProjectEdit { Project = path, Kind = ProjectEditKind.AddToSolution, Value = context.Destination });
                }
            }

            if (projects.Count > 0)
            {
                var created = ProjectFileEditor.Load(changeSet.Creates[0].Content);
                foreach (var project in projects)
                {
                    created.AddProjectReference(Relative(context.Destination, project));
                }

                changeSet.Creates[0] = changeSet.Creates[0] with { Content = created.Save() };
            }
        }
        else
        {
            var destination = context.DestinationProject!;
            var editor = Load(context, destination.Id, out var before);
            if (!destination.ProjectReferences.Contains(context.Source.Id))
            {
                editor.AddProjectReference(Relative(destination.Id, context.Source.Id));
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddProjectReference, Value = context.Source.Id });
            }

            foreach (var project in projects.Where(p => !destination.ProjectReferences.Contains(p)))
            {
                editor.AddProjectReference(Relative(destination.Id, project));
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddProjectReference, Value = project });
            }

            var central = Central(destination);
            foreach (var package in packages)
            {
                editor.AddPackageReference(package.Id, central ? null : package.Version);
                edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddPackageReference, Value = package.Id, Version = package.Version });
            }

            if (!editor.IsSdkStyle)
            {
                foreach (var move in moves)
                {
                    editor.AddCompile(Relative(destination.Id, move.To, directory: false));
                    edits.Add(new ProjectEdit { Project = destination.Id, Kind = ProjectEditKind.AddCompile, Value = move.To });
                }
            }

            changeSet.Edit(destination.Id, before, editor.Save());
        }

        SourceEdits(context, changeSet, edits, moves, internals, prunable);
        foreach (var move in moves)
        {
            changeSet.Rename(context.Root, move.File, move.To);
        }

        return (changeSet, edits);
    }

    private static void SourceEdits(Context context, ChangeSet changeSet, List<ProjectEdit> edits, List<MovedFile> moves, bool internals, List<string> prunable)
    {
        var source = context.Source;
        var editor = Load(context, source.Id, out var before);
        foreach (var move in moves)
        {
            if (editor.RemoveItems("Compile", Relative(source.Id, move.File, directory: false)) > 0)
            {
                edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.RemoveCompile, Value = move.File });
            }
        }

        if (internals && !HasInternalsVisibleTo(context))
        {
            if (editor.IsSdkStyle)
            {
                editor.AddInternalsVisibleTo(context.DestinationName);
            }
            else
            {
                var assemblyInfo = AssemblyInfo(context)!;
                var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(context.Root, assemblyInfo));
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var line = $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{context.DestinationName}\")]";
                var addition = System.Text.Encoding.UTF8.GetBytes((text.EndsWith('\n') ? "" : newline) + line + newline);
                changeSet.Edit(assemblyInfo, bytes, [.. bytes, .. addition]);
            }

            edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.AddInternalsVisibleTo, Value = context.DestinationName });
        }

        if (context.Request.PrunePackages)
        {
            foreach (var package in prunable)
            {
                editor.RemoveItems("PackageReference", package);
                edits.Add(new ProjectEdit { Project = source.Id, Kind = ProjectEditKind.RemovePackageReference, Value = package });
            }
        }

        changeSet.Edit(source.Id, before, editor.Save());
    }

    /// <summary>Packages to add to the destination: the source's direct package that supplies each needed assembly.</summary>
    private static List<TemplatePackage> PackagesToAdd(Context context, List<ReferenceNeed> needs)
    {
        var resolved = context.Source.Resolved.Values.SelectMany(f => f.Packages).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new SortedDictionary<string, TemplatePackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var need in needs.Where(n => n.Origin == ReferenceOrigin.Package))
        {
            var direct = resolved.Where(p => p.Direct && (string.Equals(p.Id, need.Package, StringComparison.OrdinalIgnoreCase) || Brings(p, need.Package!, resolved)))
                .OrderBy(p => string.Equals(p.Id, need.Package, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var package = direct ?? resolved.First(p => string.Equals(p.Id, need.Package, StringComparison.OrdinalIgnoreCase));
            var alreadyThere = context.DestinationProject?.Resolved.Values.SelectMany(f => f.Packages).Any(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)) == true;
            if (!alreadyThere)
            {
                result[package.Id] = new TemplatePackage(package.Id, package.Version);
            }
        }

        return [.. result.Values];
    }

    private static bool Brings(ResolvedPackage from, string id, List<ResolvedPackage> resolved)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(from.Dependencies.Select(d => d.Id));
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (string.Equals(next, id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (seen.Add(next) && resolved.FirstOrDefault(p => string.Equals(p.Id, next, StringComparison.OrdinalIgnoreCase)) is { } package)
            {
                foreach (var dependency in package.Dependencies)
                {
                    pending.Push(dependency.Id);
                }
            }
        }

        return false;
    }

    private static TestProjectSpec Template(Context context, string? framework, List<TemplatePackage> extra)
    {
        var source = context.Source;
        var chosen = framework ?? "xunit";
        var versions = source.PackageReferences.Where(p => p.Version is not null).ToDictionary(p => p.Id, p => p.Version!, StringComparer.OrdinalIgnoreCase);
        var packages = TestProjectTemplate.Packages(chosen, versions);
        foreach (var package in extra.Where(e => !packages.Any(p => string.Equals(p.Id, e.Id, StringComparison.OrdinalIgnoreCase))))
        {
            packages.Add(package);
        }

        var properties = CopiedProperties
            .Where(source.Properties.ContainsKey)
            .Select(k => KeyValuePair.Create(k, source.Properties[k]))
            .ToList();
        return new TestProjectSpec
        {
            Framework = chosen,
            TargetFrameworks = source.TargetFrameworks,
            SourceReference = Relative(context.Destination, source.Id),
            Packages = packages,
            FrameworkReferences = [.. source.AssemblyReferences.Where(r => r.Kind == AssemblyReferenceKind.Framework).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase)],
            Properties = properties,
            CentralVersions = Central(source),
        };
    }

    private static bool Central(ProjectInfo project) =>
        project.Properties.TryGetValue("ManagePackageVersionsCentrally", out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static ProjectFileEditor Load(Context context, string project, out byte[] before)
    {
        before = File.ReadAllBytes(RepoPaths.ToAbsolute(context.Root, project));
        return ProjectFileEditor.Load(before);
    }

    /// <summary>A path relative to a project's folder, with backslashes as project files write them.</summary>
    private static string Relative(string fromProject, string to, bool directory = true)
    {
        _ = directory;
        var from = Path.GetDirectoryName(fromProject.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
        var fromParts = from.Length == 0 ? [] : from.Split('/');
        var toParts = to.Split('/');
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length - 1 && string.Equals(fromParts[common], toParts[common], StringComparison.Ordinal))
        {
            common++;
        }

        return string.Join('\\', Enumerable.Repeat("..", fromParts.Length - common).Concat(toParts.Skip(common)));
    }

    private static List<string> Dependents(WorkspaceModel model, string project)
    {
        var dependents = model.Graph.Edges.ToLookup(e => e.To, e => e.From, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(dependents[project]);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (seen.Add(next))
            {
                foreach (var further in dependents[next])
                {
                    pending.Push(further);
                }
            }
        }

        return [.. seen.Order(StringComparer.Ordinal)];
    }

    private static void Skip(Context context, List<SkippedFile> skipped, string file, DiagnosticDescriptor code, string message, IReadOnlyList<string> details) =>
        Skip(context.Bag, context.Source.Id, skipped, file, code, message, details);

    private static void Skip(DiagnosticBag bag, string project, List<SkippedFile> skipped, string file, DiagnosticDescriptor code, string message, IReadOnlyList<string> details)
    {
        skipped.Add(new SkippedFile { File = file, Code = code.Code, Message = message, Details = details });
        bag.Report(code, message, new DiagnosticLocation(project, file),
            details.Count == 0 ? null : [KeyValuePair.Create<string, JsonNode?>("details", new JsonArray([.. details.Select(d => (JsonNode?)d)]))]);
    }

    private static MoveTestsResult Empty(string project) => new()
    {
        Project = project, Moves = [], Skipped = [], Candidates = [], ProjectEdits = [], Prunable = [],
    };

    private static bool Same(string a, string b) =>
        string.Equals(Full(a), Full(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Full(string path) => (Path.IsPathRooted(path) ? Path.GetFullPath(path) : path).Replace('\\', '/');
}
