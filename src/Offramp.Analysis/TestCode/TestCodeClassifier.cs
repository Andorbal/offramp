using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Core.Json;

namespace Offramp.Analysis.TestCode;

[JsonConverter(typeof(CamelCaseEnumConverter<TestFileKind>))]
public enum TestFileKind
{
    Test,
    Helper,
    Production,
}

/// <summary>How sure the classification is; <c>move tests --include-helpers</c> takes helpers down to a level.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<TestConfidence>))]
public enum TestConfidence
{
    Certain,
    High,
    Medium,
    Low,
}

/// <summary>A project whose compilation may use the source project's code.</summary>
public sealed record ConsumerCompilation(string Project, Compilation Compilation);

/// <summary>One source file of the project and what it is.</summary>
public sealed record ClassifiedFile
{
    public required string File { get; init; }

    public required TestFileKind Kind { get; init; }

    /// <summary>For tests and helper candidates; null for production files.</summary>
    public TestConfidence? Confidence { get; init; }

    /// <summary>Test frameworks the file's tests use (<c>xunit</c>, <c>xunit.v3</c>, <c>nunit</c>, <c>mstest</c>, <c>tunit</c>).</summary>
    public IReadOnlyList<string> Frameworks { get; init; } = [];

    /// <summary>Why: the evidence behind the kind and confidence.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Production files, and projects other than the destination, that use code in this file; they keep it where it is.</summary>
    public IReadOnlyList<string> ProductionReferrers { get; init; } = [];
}

/// <summary>
/// Finds test code in a production project (docs/spec/commands/move.md#move-tests): test
/// files by test-framework attributes and base types, then helpers by a fixpoint over which
/// files use which (a helper is used only by tests and other helpers, and reached from a test).
/// Everything comes from the semantic model; nothing is matched by text.
/// </summary>
public static class TestCodeClassifier
{
    /// <summary>Assemblies that declare test attributes and base types, and the framework each means.</summary>
    public static readonly IReadOnlyDictionary<string, string> TestAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["xunit.core"] = "xunit",
        ["xunit.v3.core"] = "xunit.v3",
        ["nunit.framework"] = "nunit",
        ["Microsoft.VisualStudio.TestPlatform.TestFramework"] = "mstest",
        ["TUnit.Core"] = "tunit",
    };

    private static readonly string[] HelperNameHints = ["Builder", "Fake", "Stub", "Mock", "Fixture", "TestData", "Harness"];

    private static readonly string[] MockingAssemblies = ["Moq", "NSubstitute", "FakeItEasy", "AutoFixture", "Bogus", "FluentAssertions", "Shouldly"];

    /// <summary>Assertion and mocking libraries whose use marks a file as test support.</summary>
    private static readonly string[] TestSupportAssemblies =
        ["xunit.assert", "xunit.v3.assert", "xunit.abstractions", .. MockingAssemblies];

    private static readonly string[] TestFolders = ["Tests", "Test", "Testing", "TestSupport", "TestData", "TestHelpers", "Fakes", "Mocks"];

    /// <param name="source">The source project's compilation.</param>
    /// <param name="files">The source project's own files (repository-relative) with their absolute paths; generated files are left out.</param>
    /// <param name="consumers">Other projects' compilations that may use the source project's code.</param>
    /// <param name="destination">The project tests will move to; its uses of the source are expected.</param>
    public static IReadOnlyList<ClassifiedFile> Classify(
        Compilation source, IReadOnlyDictionary<string, string> files, IReadOnlyList<ConsumerCompilation> consumers, string? destination)
    {
        var byPath = files.ToDictionary(f => Normalize(f.Value), f => f.Key, StringComparer.OrdinalIgnoreCase);
        var trees = source.SyntaxTrees
            .Select(t => (Tree: t, File: byPath.GetValueOrDefault(Normalize(t.FilePath))))
            .Where(t => t.File is not null)
            .ToDictionary(t => t.File!, t => t.Tree, StringComparer.Ordinal);

        var fileOf = trees.ToDictionary(t => t.Value, t => t.Key);
        var frameworks = trees.ToDictionary(t => t.Key, t => TestFrameworks(source, t.Value), StringComparer.Ordinal);
        var tests = frameworks.Where(f => f.Value.Count > 0).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        // referrers[file] = files of the project, and other projects, that use something the file declares.
        var referrers = trees.Keys.ToDictionary(f => f, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (file, tree) in trees)
        {
            foreach (var declaring in UsedFiles(source, tree, fileOf))
            {
                if (declaring != file)
                {
                    referrers[declaring].Add(file);
                }
            }
        }

        var outside = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in consumers)
        {
            foreach (var declaring in ConsumerUses(source, consumer.Compilation, fileOf))
            {
                var label = "project " + consumer.Project;
                referrers[declaring].Add(label);
                if (consumer.Project != destination)
                {
                    outside.Add(label);
                }
            }
        }

        var evidence = trees.ToDictionary(t => t.Key, t => Signals(t.Key, t.Value, source), StringComparer.Ordinal);
        var helpers = Fixpoint(trees.Keys.Where(f => !tests.Contains(f) && evidence[f].Count > 0), tests, referrers, outside);
        var reflected = StringMentions(source, consumers);
        return
        [
            .. trees.Keys.Order(StringComparer.Ordinal).Select(file => Describe(
                file, trees[file], source, tests, helpers, referrers[file], outside, frameworks[file], reflected, evidence[file])),
        ];
    }

    /// <summary>
    /// The largest set of candidates (non-test files with test-support evidence) whose every
    /// user is a test or another member, narrowed to those reachable from a test through such uses.
    /// </summary>
    private static HashSet<string> Fixpoint(IEnumerable<string> candidates, HashSet<string> tests, Dictionary<string, HashSet<string>> referrers, HashSet<string> outside)
    {
        var helpers = candidates.ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var file in helpers.ToList())
            {
                var users = referrers[file];
                if (users.Count == 0 || users.Any(u => outside.Contains(u) || (!u.StartsWith("project ", StringComparison.Ordinal) && !tests.Contains(u) && !helpers.Contains(u))))
                {
                    helpers.Remove(file);
                    changed = true;
                }
            }
        }
        while (changed);

        // Keep only helpers some test actually reaches (a dead cluster using only itself is not a helper).
        var uses = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (declaring, users) in referrers)
        {
            foreach (var user in users)
            {
                if (!uses.TryGetValue(user, out var list))
                {
                    uses[user] = list = [];
                }

                list.Add(declaring);
            }
        }

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(tests);
        while (pending.Count > 0)
        {
            foreach (var next in uses.GetValueOrDefault(pending.Pop()) ?? [])
            {
                if (helpers.Contains(next) && reached.Add(next))
                {
                    pending.Push(next);
                }
            }
        }

        // Helpers used by a project other than the destination count as test support too: that project is a test.
        foreach (var file in helpers.Where(h => referrers[h].Any(u => u.StartsWith("project ", StringComparison.Ordinal))))
        {
            reached.Add(file);
        }

        return reached;
    }

    private static ClassifiedFile Describe(
        string file, SyntaxTree tree, Compilation source, HashSet<string> tests, HashSet<string> helpers, HashSet<string> users,
        HashSet<string> outside, IReadOnlyList<string> frameworks, HashSet<string> reflected, List<string> evidence)
    {
        var production = users
            .Where(u => outside.Contains(u) || (!u.StartsWith("project ", StringComparison.Ordinal) && !tests.Contains(u) && !helpers.Contains(u)))
            .Select(u => u.StartsWith("project ", StringComparison.Ordinal) ? u["project ".Length..] : u)
            .Order(StringComparer.Ordinal)
            .ToList();
        var declared = DeclaredTypes(source, tree).ToList();
        var mentioned = declared.Where(t => reflected.Contains(t.Name) || reflected.Contains(t.ToDisplayString())).Select(t => t.Name).ToList();

        if (tests.Contains(file))
        {
            return new ClassifiedFile
            {
                File = file,
                Kind = TestFileKind.Test,
                Confidence = TestConfidence.Certain,
                Frameworks = frameworks,
                Reasons = ["declares tests (" + string.Join(", ", frameworks) + ")"],
                ProductionReferrers = production,
            };
        }

        if (helpers.Contains(file))
        {
            List<string> reasons = ["used only by tests and other helpers", .. evidence];
            if (mentioned.Count > 0)
            {
                reasons.Add("named in a string (" + string.Join(", ", mentioned) + "); reflection may use it");
            }

            return new ClassifiedFile
            {
                File = file,
                Kind = TestFileKind.Helper,
                Confidence = mentioned.Count > 0 ? TestConfidence.Medium : TestConfidence.High,
                Reasons = reasons,
            };
        }

        if (users.Count == 0)
        {
            // Used by nobody: dead, or reached by reflection. Test-support evidence makes it worth a review.
            return new ClassifiedFile
            {
                File = file,
                Kind = TestFileKind.Helper,
                Confidence = evidence.Count > 0 ? TestConfidence.Medium : TestConfidence.Low,
                Reasons = ["used by nothing in the project or its consumers", .. evidence],
            };
        }

        return new ClassifiedFile
        {
            File = file,
            Kind = TestFileKind.Production,
            ProductionReferrers = production,
            Reasons = production.Count == 0 ? ["used only by tests, without test-support evidence: treated as the code under test"] : [],
        };
    }

    /// <summary>Evidence that a file is test support: what it uses, what its types are called, where it lives.</summary>
    private static List<string> Signals(string file, SyntaxTree tree, Compilation source)
    {
        var declared = DeclaredTypes(source, tree).ToList();
        var signals = new List<string>();
        var model = source.GetSemanticModel(tree);
        var testing = tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>()
            .SelectMany(n => Symbols(model, n))
            .Select(s => s.ContainingAssembly?.Name)
            .Where(a => a is not null && (TestAssemblies.ContainsKey(a) || TestSupportAssemblies.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (testing.Count > 0)
        {
            signals.Add("uses " + string.Join(", ", testing));
        }

        var hinted = declared.Where(t => HelperNameHints.Any(h => t.Name.Contains(h, StringComparison.Ordinal))).Select(t => t.Name).ToList();
        if (hinted.Count > 0)
        {
            signals.Add("name suggests test support (" + string.Join(", ", hinted) + ")");
        }

        var folder = file.Split('/').SkipLast(1).FirstOrDefault(s => TestFolders.Contains(s, StringComparer.OrdinalIgnoreCase));
        if (folder is not null)
        {
            signals.Add($"in a {folder} folder");
        }

        var mocking = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name is null ? null : model.GetSymbolInfo(u.Name).Symbol)
            .OfType<INamespaceSymbol>()
            .SelectMany(ns => ns.ConstituentNamespaces)
            .Select(ns => ns.ContainingAssembly?.Name)
            .Where(a => a is not null && MockingAssemblies.Contains(a, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mocking.Count > 0 && testing.Count == 0)
        {
            signals.Add("imports " + string.Join(", ", mocking));
        }

        return signals;
    }

    /// <summary>Test frameworks whose attributes or base types the file's types use.</summary>
    private static List<string> TestFrameworks(Compilation source, SyntaxTree tree)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in DeclaredTypes(source, tree))
        {
            var attributes = type.GetAttributes().Concat(type.GetMembers().SelectMany(m => m.GetAttributes()));
            foreach (var attribute in attributes)
            {
                if (Framework(attribute.AttributeClass?.ContainingAssembly) is { } framework)
                {
                    found.Add(framework);
                }
            }

            for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (Framework(baseType.ContainingAssembly) is { } framework)
                {
                    found.Add(framework);
                }
            }
        }

        return [.. found];
    }

    private static string? Framework(IAssemblySymbol? assembly) =>
        assembly is null ? null : TestAssemblies.GetValueOrDefault(assembly.Name);

    private static IEnumerable<INamedTypeSymbol> DeclaredTypes(Compilation source, SyntaxTree tree)
    {
        var model = source.GetSemanticModel(tree);
        return tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(d => model.GetDeclaredSymbol(d))
            .OfType<INamedTypeSymbol>();
    }

    /// <summary>The project's files declaring the symbols a tree uses.</summary>
    private static HashSet<string> UsedFiles(Compilation source, SyntaxTree tree, Dictionary<SyntaxTree, string> trees)
    {
        var model = source.GetSemanticModel(tree);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            foreach (var symbol in Symbols(model, node))
            {
                foreach (var reference in symbol.DeclaringSyntaxReferences)
                {
                    if (FileOf(reference.SyntaxTree, trees) is { } file)
                    {
                        files.Add(file);
                    }
                }
            }
        }

        return files;
    }

    /// <summary>The source project's files declaring what another project's code uses from it.</summary>
    private static HashSet<string> ConsumerUses(Compilation source, Compilation consumer, Dictionary<SyntaxTree, string> trees)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        var assembly = source.AssemblyName;
        foreach (var tree in consumer.SyntaxTrees)
        {
            var model = consumer.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                foreach (var symbol in Symbols(model, node).Where(s => s.ContainingAssembly?.Name == assembly))
                {
                    var id = symbol.GetDocumentationCommentId();
                    var original = id is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(id, source);
                    foreach (var reference in original?.DeclaringSyntaxReferences ?? [])
                    {
                        if (FileOf(reference.SyntaxTree, trees) is { } file)
                        {
                            files.Add(file);
                        }
                    }
                }
            }
        }

        return files;
    }

    private static IEnumerable<ISymbol> Symbols(SemanticModel model, SimpleNameSyntax node)
    {
        var info = model.GetSymbolInfo(node);
        var symbols = info.Symbol is { } symbol ? [symbol] : info.CandidateSymbols;
        foreach (var candidate in symbols)
        {
            var definition = candidate.OriginalDefinition;
            if (definition is IMethodSymbol { ReducedFrom: { } reduced })
            {
                definition = reduced;
            }

            if (definition.Kind is SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event)
            {
                yield return definition;
                if (definition.ContainingType is { } containing)
                {
                    yield return containing.OriginalDefinition;
                }
            }
        }
    }

    /// <summary>Every string literal in the project and its consumers, to spot types reached by name.</summary>
    private static HashSet<string> StringMentions(Compilation source, IReadOnlyList<ConsumerCompilation> consumers)
    {
        var mentions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in source.SyntaxTrees.Concat(consumers.SelectMany(c => c.Compilation.SyntaxTrees)))
        {
            foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                var text = literal.Token.ValueText;
                mentions.Add(text);
                var comma = text.IndexOf(',', StringComparison.Ordinal);
                var typeName = comma > 0 ? text[..comma].Trim() : text;
                mentions.Add(typeName);
                mentions.Add(typeName[(typeName.LastIndexOf('.') + 1)..]);
            }
        }

        return mentions;
    }

    private static string? FileOf(SyntaxTree tree, Dictionary<SyntaxTree, string> files) => files.GetValueOrDefault(tree);

    /// <summary>A full path with forward slashes; compiler logs record linked files as <c>Foo/../Common/X.cs</c>.</summary>
    private static string Normalize(string path) => (Path.IsPathRooted(path) ? Path.GetFullPath(path) : path).Replace('\\', '/');
}
