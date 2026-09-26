using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.DeadCode;

public sealed record DeadCodeRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary><c>public</c> or <c>all</c>.</summary>
    public string Scope { get; init; } = "all";

    public DeadCodeConfidence MinConfidence { get; init; } = DeadCodeConfidence.Low;

    public bool IncludeTests { get; init; }

    /// <summary>Project ids to report on; empty for every non-test C# project.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary><c>deadCode.externalConsumers</c>: projects or assemblies other repositories consume.</summary>
    public IReadOnlyList<string> ExternalConsumers { get; init; } = [];

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>
/// <c>audit dead-code</c>: types and members nothing in the solution references, with an
/// honest confidence. References are every name the semantic model binds, in every project's
/// recorded compilation, matched by documentation ID (a member's use also counts for the types
/// containing it); uses inside a symbol's own declaration do not count. Implicit uses the
/// compiler makes (<c>foreach</c>, <c>await</c>, collection initializers, deconstruction, query
/// clauses) count too. Overrides, interface implementations, constructors, and operators are
/// never candidates.
/// </summary>
public static class DeadCodeAnalyzer
{
    /// <summary>Calls that register types by convention (Scrutor, Autofac, MediatR, MVC).</summary>
    private static readonly HashSet<string> ConventionCalls = new(StringComparer.Ordinal)
    {
        "Scan", "RegisterAssemblyTypes", "RegisterAssemblyModules", "AddMediatR", "AddControllers", "AddControllersWithViews",
        "AddMvc", "MapControllers", "AddClasses", "FromAssemblyOf", "FromAssemblies", "RegisterTypes",
    };

    /// <summary>Attributes that say nothing about reflection.</summary>
    private static readonly string[] InertAttributePrefixes =
    [
        "System.ObsoleteAttribute", "System.Diagnostics.", "System.ComponentModel.EditorBrowsableAttribute",
        "System.Runtime.CompilerServices.", "System.CLSCompliantAttribute",
    ];

    private static readonly HashSet<string> SerializationAttributes = new(StringComparer.Ordinal)
    {
        "System.SerializableAttribute", "System.Runtime.Serialization.DataContractAttribute", "System.Runtime.Serialization.DataMemberAttribute",
        "System.Xml.Serialization.XmlRootAttribute", "System.Xml.Serialization.XmlTypeAttribute",
    };

    public static DeadCodeResult Analyze(DeadCodeRequest request)
    {
        using var loader = new CompilationLoader(request.RepositoryRoot);
        var skipped = new List<string>();
        var compilations = Load(request, loader, skipped);
        var solutionAssemblies = compilations.Select(c => c.Compilation.AssemblyName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Index index;
        using (var phase = request.Progress.BeginPhase("dead code: references", 1, 2))
        {
            index = Index.Build(request.RepositoryRoot, compilations, phase);
        }

        var strings = Strings(request, compilations);
        var projects = new List<DeadCodeProject>();
        using (var phase = request.Progress.BeginPhase("dead code: candidates", 2, 2))
        {
            var inScope = compilations.Where(c => !c.Project.IsTestProject && (request.Projects.Count == 0 || request.Projects.Contains(c.Project.Id, StringComparer.Ordinal))).ToList();
            for (var i = 0; i < inScope.Count; i++)
            {
                phase.Report(i, inScope.Count, inScope[i].Project.Id);
                if (Project(request, inScope[i], index, strings, solutionAssemblies) is { } project)
                {
                    projects.Add(project);
                }
            }
        }

        Report(request, projects);
        var candidates = projects.SelectMany(p => p.Candidates).ToList();
        return new DeadCodeResult
        {
            Scope = request.Scope,
            MinConfidence = request.MinConfidence,
            IncludeTests = request.IncludeTests,
            Projects = projects,
            Summary = new DeadCodeSummary
            {
                Candidates = candidates.Count,
                High = candidates.Count(c => c.Confidence == DeadCodeConfidence.High),
                Medium = candidates.Count(c => c.Confidence == DeadCodeConfidence.Medium),
                Low = candidates.Count(c => c.Confidence == DeadCodeConfidence.Low),
                TestOnly = projects.Sum(p => p.TestOnly.Count),
                RemovableLoc = candidates.Where(c => c.Confidence == DeadCodeConfidence.High).Sum(c => c.Loc),
            },
            Skipped = [.. skipped.Order(StringComparer.Ordinal)],
        };
    }

    private sealed record Loaded(ProjectInfo Project, Compilation Compilation);

    private static List<Loaded> Load(DeadCodeRequest request, CompilationLoader loader, List<string> skipped)
    {
        var loaded = new List<Loaded>();
        foreach (var project in request.Model.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (project.Config.Excluded)
            {
                continue;
            }

            if (project.Language != "csharp")
            {
                skipped.Add($"{project.Id}: dead-code analysis reads C# only; its references to C# projects are not seen.");
                continue;
            }

            if (loader.LoadForProject(project) is not { } compilation)
            {
                skipped.Add($"{project.Id}: no compiler call was recorded for it (run `offramp scan`).");
                continue;
            }

            loaded.Add(new Loaded(project, compilation));
        }

        return loaded;
    }

    /// <summary>Where the solution uses each symbol (by documentation ID): project, file, and position.</summary>
    private sealed class Index
    {
        private readonly Dictionary<string, List<(string Project, bool Test, string File, int Position)>> _uses = new(StringComparer.Ordinal);

        public List<(string Project, bool Test, string File, int Position)> UsesOf(ISymbol symbol) =>
            symbol.OriginalDefinition.GetDocumentationCommentId() is { } id && _uses.TryGetValue(id, out var uses) ? uses : [];

        /// <summary>The convention registration calls found, as "Type.Method at file:line".</summary>
        public List<string> ConventionRegistrations { get; } = [];

        public static Index Build(string root, List<Loaded> compilations, IProgressPhase phase)
        {
            var index = new Index();
            var trees = compilations.SelectMany(c => c.Compilation.SyntaxTrees.Select(t => (c.Project, c.Compilation, Tree: t))).ToList();
            var seen = new HashSet<(string, string)>();
            for (var i = 0; i < trees.Count; i++)
            {
                var (project, compilation, tree) = trees[i];
                var file = FileOf(root, tree);
                if (!seen.Add((project.Id, tree.FilePath)))
                {
                    continue;
                }

                phase.Report(i, trees.Count, file);
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes())
                {
                    foreach (var symbol in Referenced(model, node))
                    {
                        index.Add(symbol, project, file, node.SpanStart);
                    }

                    if (node is InvocationExpressionSyntax invocation && AuditEngine.Bound(model, invocation) is IMethodSymbol called && ConventionCalls.Contains(called.Name))
                    {
                        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        index.ConventionRegistrations.Add($"{called.ContainingType?.Name}.{called.Name} at {file}:{line}");
                    }
                }
            }

            index.ConventionRegistrations.Sort(StringComparer.Ordinal);
            return index;
        }

        private void Add(ISymbol symbol, ProjectInfo project, string file, int position)
        {
            // A member's use is also a use of the types that contain it (extension methods are
            // called without naming their class).
            for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
            {
                if (current.OriginalDefinition.GetDocumentationCommentId() is not { } id)
                {
                    continue;
                }

                var uses = _uses.TryGetValue(id, out var list) ? list : _uses[id] = [];
                uses.Add((project.Id, project.IsTestProject, file, position));
            }
        }

        /// <summary>The symbols a node uses, explicitly or through the compiler's pattern lookups.</summary>
        private static IEnumerable<ISymbol> Referenced(SemanticModel model, SyntaxNode node)
        {
            switch (node)
            {
                case SimpleNameSyntax or ElementAccessExpressionSyntax or BaseObjectCreationExpressionSyntax or AttributeSyntax or ConstructorInitializerSyntax:
                    var info = model.GetSymbolInfo(node);
                    foreach (var symbol in info.Symbol is { } bound ? [bound] : info.CandidateSymbols)
                    {
                        yield return Normalize(symbol);
                    }

                    break;
                case CommonForEachStatementSyntax forEach:
                    var loop = model.GetForEachStatementInfo(forEach);
                    foreach (var symbol in new ISymbol?[] { loop.GetEnumeratorMethod, loop.MoveNextMethod, loop.CurrentProperty }.OfType<ISymbol>())
                    {
                        yield return symbol;
                    }

                    break;
                case AwaitExpressionSyntax await:
                    var awaited = model.GetAwaitExpressionInfo(await);
                    foreach (var symbol in new ISymbol?[] { awaited.GetAwaiterMethod, awaited.GetResultMethod, awaited.IsCompletedProperty }.OfType<ISymbol>())
                    {
                        yield return symbol;
                    }

                    break;
                case InitializerExpressionSyntax initializer when initializer.IsKind(SyntaxKind.CollectionInitializerExpression):
                    foreach (var element in initializer.Expressions)
                    {
                        if (model.GetCollectionInitializerSymbolInfo(element).Symbol is { } add)
                        {
                            yield return Normalize(add);
                        }
                    }

                    break;
                case AssignmentExpressionSyntax { Left: TupleExpressionSyntax or DeclarationExpressionSyntax } deconstruction:
                    if (model.GetDeconstructionInfo(deconstruction).Method is { } deconstruct)
                    {
                        yield return Normalize(deconstruct);
                    }

                    break;
                case QueryClauseSyntax or SelectOrGroupClauseSyntax:
                    if (model.GetSymbolInfo(node).Symbol is { } clause)
                    {
                        yield return Normalize(clause);
                    }

                    break;
            }
        }

        /// <summary>Accessors count for their property or event, constructors for their type, reduced extensions for their definition.</summary>
        private static ISymbol Normalize(ISymbol symbol) => symbol switch
        {
            IMethodSymbol { ReducedFrom: { } reduced } => reduced,
            IMethodSymbol { AssociatedSymbol: { } associated } => associated,
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } constructor => constructor.ContainingType,
            _ => symbol,
        };
    }

    /// <summary>String literals and resource or configuration text, with where each is.</summary>
    private static List<(string Text, string Where)> Strings(DeadCodeRequest request, List<Loaded> compilations)
    {
        var strings = new List<(string, string)>();
        foreach (var tree in compilations.SelectMany(c => c.Compilation.SyntaxTrees).DistinctBy(t => t.FilePath))
        {
            var file = FileOf(request.RepositoryRoot, tree);
            foreach (var token in tree.GetRoot().DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                {
                    strings.Add((token.ValueText, $"{file}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}"));
                }
            }
        }

        foreach (var project in compilations.Select(c => c.Project))
        {
            var directory = Path.GetDirectoryName(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id))!;
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, path);
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is ".resx" or ".config" or ".xaml" or ".xml" or ".json" && !relative.Contains("/obj/", StringComparison.Ordinal) && !relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    strings.Add((System.IO.File.ReadAllText(path), relative));
                }
            }
        }

        return strings;
    }

    private sealed record Declared(ISymbol Symbol, IReadOnlyList<SyntaxNode> Declarations);

    private static DeadCodeProject? Project(DeadCodeRequest request, Loaded loaded, Index index, List<(string Text, string Where)> strings, HashSet<string> solutionAssemblies)
    {
        var (project, compilation) = loaded;
        var sources = AuditEngine.Sources(compilation).ToHashSet();
        var declared = Declarations(compilation, sources);
        var candidates = new List<DeadCodeCandidate>();
        var testOnly = new List<TestOnlySymbol>();
        var unusedTypes = new List<ISymbol>();

        foreach (var item in declared)
        {
            var symbol = item.Symbol;
            if (unusedTypes.Any(t => Contains(t, symbol)) || !Eligible(symbol) || (request.Scope == "public" && Accessibility(symbol) != "public"))
            {
                continue;
            }

            var outside = index.UsesOf(symbol).Where(u => !InsideDeclarations(item, u.File, u.Position, request.RepositoryRoot)).ToList();
            var (file, line, loc) = Where(request.RepositoryRoot, item);
            if (outside.Count > 0)
            {
                if (request.IncludeTests && outside.All(u => u.Test))
                {
                    testOnly.Add(new TestOnlySymbol
                    {
                        Symbol = AuditEngine.Name(symbol),
                        Kind = Kind(symbol),
                        File = file,
                        Line = line,
                        Loc = loc,
                        Tests = [.. outside.Select(u => u.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    });
                    if (symbol is INamedTypeSymbol)
                    {
                        unusedTypes.Add(symbol);
                    }
                }

                continue;
            }

            if (symbol is INamedTypeSymbol)
            {
                unusedTypes.Add(symbol);
            }

            var (confidence, evidence) = Confidence(request, project, symbol, index, strings, solutionAssemblies);
            if (confidence < request.MinConfidence)
            {
                continue;
            }

            candidates.Add(new DeadCodeCandidate
            {
                Symbol = AuditEngine.Name(symbol),
                Kind = Kind(symbol),
                Accessibility = Accessibility(symbol),
                Confidence = confidence,
                Evidence = evidence,
                File = file,
                Line = line,
                Loc = loc,
            });
        }

        if (candidates.Count == 0 && testOnly.Count == 0)
        {
            return null;
        }

        var ordered = candidates.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Line).ThenBy(c => c.Symbol, StringComparer.Ordinal).ToList();
        return new DeadCodeProject
        {
            Project = project.Id,
            Candidates = ordered,
            TestOnly = [.. testOnly.OrderBy(t => t.File, StringComparer.Ordinal).ThenBy(t => t.Line).ThenBy(t => t.Symbol, StringComparer.Ordinal)],
            Loc = new DeadCodeLoc(
                ordered.Where(c => c.Confidence == DeadCodeConfidence.High).Sum(c => c.Loc),
                ordered.Where(c => c.Confidence == DeadCodeConfidence.Medium).Sum(c => c.Loc),
                ordered.Where(c => c.Confidence == DeadCodeConfidence.Low).Sum(c => c.Loc)),
        };
    }

    /// <summary>Declared types and members in source order, outer before inner, one entry per symbol (partial declarations together).</summary>
    private static List<Declared> Declarations(Compilation compilation, HashSet<SyntaxTree> sources)
    {
        var bySymbol = new Dictionary<ISymbol, List<SyntaxNode>>(SymbolEqualityComparer.Default);
        var order = new List<ISymbol>();
        foreach (var tree in compilation.SyntaxTrees.Where(sources.Contains))
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax))
            {
                var symbols = node switch
                {
                    BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or BasePropertyDeclarationSyntax or EventDeclarationSyntax
                        => model.GetDeclaredSymbol(node) is { } one ? [one] : [],
                    BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v)).OfType<ISymbol>().ToList(),
                    _ => (IReadOnlyList<ISymbol>)[],
                };
                foreach (var symbol in symbols)
                {
                    if (!bySymbol.TryGetValue(symbol, out var nodes))
                    {
                        bySymbol[symbol] = nodes = [];
                        order.Add(symbol);
                    }

                    nodes.Add(node);
                }
            }
        }

        return [.. order.Select(s => new Declared(s, bySymbol[s]))];
    }

    /// <summary>Whether a symbol can be dead code at all (not an override, implementation, constructor, operator, or interface member).</summary>
    private static bool Eligible(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || symbol.IsOverride || symbol.IsAbstract || symbol.IsVirtual || symbol.ContainingType?.TypeKind is TypeKind.Interface or TypeKind.Enum)
        {
            return false;
        }

        if (symbol is IMethodSymbol method && (method.MethodKind != MethodKind.Ordinary || method.ExplicitInterfaceImplementations.Length > 0 || method.PartialDefinitionPart is not null))
        {
            return false;
        }

        if (symbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
        {
            return false;
        }

        return symbol is INamedTypeSymbol || !ImplementsInterface(symbol);
    }

    private static bool ImplementsInterface(ISymbol symbol) =>
        symbol.ContainingType is { } type
        && type.AllInterfaces.SelectMany(i => i.GetMembers()).Any(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol));

    private static bool Contains(ISymbol type, ISymbol symbol)
    {
        for (var current = symbol.ContainingType; current is not null; current = current.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, type))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InsideDeclarations(Declared declared, string file, int position, string root) =>
        declared.Declarations.Any(d => FileOf(root, d.SyntaxTree) == file && d.FullSpan.Contains(position));

    private static (DeadCodeConfidence Confidence, List<string> Evidence) Confidence(
        DeadCodeRequest request, ProjectInfo project, ISymbol symbol, Index index, List<(string Text, string Where)> strings, HashSet<string> solutionAssemblies)
    {
        var evidence = new List<string>();
        DeadCodeConfidence confidence;
        var accessibility = Accessibility(symbol);
        if (accessibility == "public")
        {
            var external = request.ExternalConsumers.Any(c => string.Equals(c, project.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c, project.AssemblyName, StringComparison.OrdinalIgnoreCase) || string.Equals(c, project.Id, StringComparison.Ordinal));
            var packable = project.Properties.TryGetValue("IsPackable", out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            (confidence, var why) = external ? (DeadCodeConfidence.Medium, "public, and the assembly is listed in deadCode.externalConsumers")
                : packable ? (DeadCodeConfidence.Medium, "public in a packable assembly (IsPackable): other repositories may use it")
                : (DeadCodeConfidence.High, "public, and the assembly is not packed");
            evidence.Add(why);
        }
        else
        {
            var friends = project.InternalsVisibleTo.Where(f => !solutionAssemblies.Contains(f.Split(',')[0].Trim())).Order(StringComparer.Ordinal).ToList();
            (confidence, var why) = accessibility == "internal" && friends.Count > 0
                ? (DeadCodeConfidence.Medium, $"internal, and visible to {string.Join(", ", friends)} outside the solution")
                : (DeadCodeConfidence.High, accessibility);
            evidence.Add(why);
        }

        var low = LowEvidence(symbol, index, strings).ToList();
        if (low.Count > 0)
        {
            confidence = DeadCodeConfidence.Low;
            evidence.AddRange(low);
        }

        return (confidence, evidence);
    }

    /// <summary>What static analysis cannot see: strings, conventions, serializers, entry points, reflection-driven attributes.</summary>
    private static IEnumerable<string> LowEvidence(ISymbol symbol, Index index, List<(string Text, string Where)> strings)
    {
        var name = symbol.Name;
        var mention = strings.FirstOrDefault(s => MentionsWord(s.Text, name));
        if (mention.Where is not null)
        {
            yield return $"the name appears in a string or resource at {mention.Where}";
        }

        if (symbol is INamedTypeSymbol type)
        {
            if (Convention(type, index) is { } convention)
            {
                yield return convention;
            }

            if (type.GetMembers().OfType<IMethodSymbol>().Any(m => m.IsStatic && m.Name == "Main") || type.Name == "Program")
            {
                yield return "entry point";
            }
        }

        if (symbol is IMethodSymbol { IsStatic: true, Name: "Main" })
        {
            yield return "entry point";
        }

        foreach (var attribute in symbol.GetAttributes().Select(a => a.AttributeClass?.ToDisplayString()).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (SerializationAttributes.Contains(attribute))
            {
                yield return $"[{Short(attribute)}]: serializers use it by reflection";
            }
            else if (!InertAttributePrefixes.Any(p => attribute.StartsWith(p, StringComparison.Ordinal)))
            {
                yield return $"[{Short(attribute)}]: frameworks find attributed code by reflection";
            }
        }

        if (symbol is IPropertySymbol or IFieldSymbol && Accessibility(symbol) == "public")
        {
            yield return "public data member: serializers, ORMs, and data binding use them by reflection";
        }
    }

    /// <summary>Why a type may be created by convention, or null.</summary>
    private static string? Convention(INamedTypeSymbol type, Index index)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
        {
            return null;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name is "Controller" or "ControllerBase" or "ApiController" or "Hub")
            {
                return $"derives from {current.Name}: the framework discovers it";
            }
        }

        if (type.AllInterfaces.Any(i => i.Name is "IRequestHandler" or "INotificationHandler" or "IConsumer" or "IHostedService"))
        {
            return "implements a handler interface that containers discover";
        }

        var solutionInterface = type.AllInterfaces.FirstOrDefault(i => i.Locations.Any(l => l.IsInSource));
        return solutionInterface is not null && index.ConventionRegistrations.Count > 0
            ? $"implements {AuditEngine.Name(solutionInterface)}, and the solution registers types by convention ({index.ConventionRegistrations[0]})"
            : null;
    }

    private static bool MentionsWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !IsIdentifierChar(text[at - 1]);
            var after = at + word.Length == text.Length || !IsIdentifierChar(text[at + word.Length]);
            if (before && after)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Short(string attribute)
    {
        var name = attribute[(attribute.LastIndexOf('.') + 1)..];
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    /// <summary><c>public</c> when visible outside the assembly (public all the way out), else the most restrictive level on the way.</summary>
    private static string Accessibility(ISymbol symbol)
    {
        var result = "public";
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            switch (current.DeclaredAccessibility)
            {
                case Microsoft.CodeAnalysis.Accessibility.Private:
                    return "private";
                case Microsoft.CodeAnalysis.Accessibility.Internal or Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal:
                    result = "internal";
                    break;
            }
        }

        return result;
    }

    private static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Delegate } => "delegate",
        INamedTypeSymbol => "class",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "member",
    };

    /// <summary>The first declaration's file and line, and the lines of every declaration with its attributes and documentation comment.</summary>
    private static (string File, int Line, int Loc) Where(string root, Declared declared)
    {
        var first = declared.Declarations[0];
        var loc = 0;
        foreach (var declaration in declared.Declarations)
        {
            var node = declaration;
            var start = Documentation(node);
            var text = node.SyntaxTree.GetText();
            loc += text.Lines.GetLineFromPosition(node.Span.End).LineNumber - text.Lines.GetLineFromPosition(start).LineNumber + 1;
        }

        var line = first.SyntaxTree.GetText().Lines.GetLineFromPosition(first is MemberDeclarationSyntax member ? Identifier(member).SpanStart : first.SpanStart).LineNumber + 1;
        return (FileOf(root, first.SyntaxTree), line, loc);
    }

    private static SyntaxToken Identifier(MemberDeclarationSyntax member) => member switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier,
        MethodDeclarationSyntax method => method.Identifier,
        PropertyDeclarationSyntax property => property.Identifier,
        EventDeclarationSyntax @event => @event.Identifier,
        BaseFieldDeclarationSyntax field => field.Declaration.Variables[0].Identifier,
        _ => member.GetFirstToken(),
    };

    /// <summary>
    /// Where a declaration starts counting its documentation comment. Projects that do not
    /// generate documentation parse <c>///</c> as ordinary comments, so both forms count.
    /// </summary>
    private static int Documentation(SyntaxNode node)
    {
        var doc = node.GetLeadingTrivia().FirstOrDefault(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            || (t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().StartsWith("///", StringComparison.Ordinal)));
        return doc == default ? node.Span.Start : doc.SpanStart;
    }

    private static string FileOf(string root, SyntaxTree tree) =>
        Path.IsPathRooted(tree.FilePath) ? RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath)) : tree.FilePath.Replace('\\', '/');

    private static void Report(DeadCodeRequest request, List<DeadCodeProject> projects)
    {
        foreach (var project in projects)
        {
            if (project.Candidates.Count > 0)
            {
                var first = project.Candidates[0];
                var high = project.Candidates.Count(c => c.Confidence == DeadCodeConfidence.High);
                request.Diagnostics.Report(DiagnosticCatalog.OFR3401,
                    $"{project.Candidates.Count} dead-code candidate{(project.Candidates.Count == 1 ? "" : "s")}, {high} at high confidence ({project.Loc.High} lines removable).",
                    new DiagnosticLocation(project.Project, first.File, first.Line),
                    [KeyValuePair.Create<string, JsonNode?>("candidates", project.Candidates.Count), KeyValuePair.Create<string, JsonNode?>("removableLoc", project.Loc.High)]);
            }

            foreach (var symbol in project.TestOnly)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3402,
                    $"{symbol.Symbol} is used only by {string.Join(", ", symbol.Tests)}: move it to the tests or delete it with them.",
                    new DiagnosticLocation(project.Project, symbol.File, symbol.Line));
            }
        }
    }
}
