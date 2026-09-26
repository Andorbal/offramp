using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Core.Paths;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Audits;

/// <summary>A rule match before it becomes a finding: where, which symbol, and what to say.</summary>
public sealed record RawFinding(AuditRule Rule, Location Location, string Symbol, string? Message = null, IReadOnlyDictionary<string, string>? Details = null)
{
    /// <summary>The namespace of the symbol involved (the porting ledger counts by it).</summary>
    public string? Namespace { get; init; }

    /// <summary>For findings outside C# sources (configuration and project files): repository-relative file and 1-based line.</summary>
    public (string File, int Line)? FileLocation { get; init; }
}

/// <summary>What a matcher gets: the project, its compilation, the rules it serves, and the repository.</summary>
public sealed record AuditMatchContext
{
    public required string RepositoryRoot { get; init; }

    public required ProjectInfo Project { get; init; }

    /// <summary>The recorded compilation (the project's .NET Framework target when it has one).</summary>
    public required Compilation Compilation { get; init; }

    /// <summary>The audited source files of <see cref="Compilation"/> (generated files left out).</summary>
    public required IReadOnlyList<SyntaxTree> Trees { get; init; }

    /// <summary>The active rules of this audit.</summary>
    public required IReadOnlyList<AuditRule> Rules { get; init; }

    public required int TargetMajor { get; init; }

    /// <summary><c>audit api</c>: the same sources compiled against the target, or null when it could not be built.</summary>
    public TargetCompilation? Target { get; init; }

    /// <summary>State shared by every project of one run (the types the solution serializes).</summary>
    public AuditRunState Run { get; init; } = new();

    public AuditRule? Rule(string id) => Rules.FirstOrDefault(r => r.Id == id);
}

/// <summary>State one audit run shares between projects.</summary>
public sealed class AuditRunState
{
    /// <summary>Documentation IDs of the types some serializer in the solution receives.</summary>
    public HashSet<string> SerializedTypes { get; } = new(StringComparer.Ordinal);
}

/// <summary>A named matcher (<c>matcher:</c> in a rule pack) over a whole compilation.</summary>
public interface IAuditMatcher
{
    string Name { get; }

    IEnumerable<RawFinding> Run(AuditMatchContext context);
}

/// <summary>
/// Runs a pack's rules over a compilation. Symbol rules match by identity through the
/// semantic model (documentation IDs of the symbol, its containing types, and its
/// namespaces); base-type and attribute rules check declarations and attribute uses;
/// named matchers do the rest. One finding per rule and line.
/// </summary>
public static class AuditEngine
{
    public static IReadOnlyList<RawFinding> Run(AuditMatchContext context, IReadOnlyDictionary<string, IAuditMatcher> matchers)
    {
        var findings = new List<RawFinding>();
        findings.AddRange(SymbolMatches(context));
        foreach (var name in context.Rules.Select(r => r.Matcher).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (matchers.TryGetValue(name, out var matcher))
            {
                findings.AddRange(matcher.Run(context));
            }
        }

        // One per rule and line, the first column kept.
        return [.. findings
            .GroupBy(f => (f.Rule.Id, Line(f)))
            .Select(g => g.OrderBy(f => f.Location.SourceSpan.Start).First())];
    }

    private static (string File, int Line) Line(RawFinding finding) =>
        finding.FileLocation ?? (finding.Location.GetLineSpan().Path, finding.Location.GetLineSpan().StartLinePosition.Line);

    /// <summary>The source files audited: generated files under obj/ and bin/ are left out.</summary>
    public static IReadOnlyList<SyntaxTree> Sources(Compilation compilation) =>
        [.. compilation.SyntaxTrees.Where(t => !IsGenerated(t.FilePath))];

    private static bool IsGenerated(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<RawFinding> SymbolMatches(AuditMatchContext context)
    {
        var bySymbol = Index(context.Rules, r => r.Symbols);
        var byBase = Index(context.Rules, r => r.BaseTypes);
        var byAttribute = Index(context.Rules, r => r.Attributes);
        if (bySymbol.Count + byBase.Count + byAttribute.Count == 0)
        {
            yield break;
        }

        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case AttributeSyntax attribute when byAttribute.Count > 0:
                        if (model.GetSymbolInfo(attribute).Symbol?.ContainingType is { } attributeType)
                        {
                            foreach (var rule in Lookup(byAttribute, TypeAndBases(attributeType)))
                            {
                                yield return new RawFinding(rule, attribute.GetLocation(), Name(attributeType)) { Namespace = NamespaceOf(attributeType) };
                            }
                        }

                        break;

                    case BaseTypeDeclarationSyntax declaration when byBase.Count > 0:
                        if (model.GetDeclaredSymbol(declaration) is INamedTypeSymbol declared && declared.BaseType is { } baseType)
                        {
                            foreach (var rule in Lookup(byBase, TypeAndBases(baseType)))
                            {
                                yield return new RawFinding(rule, declaration.Identifier.GetLocation(), Name(baseType), $"{Name(declared)} derives from {Name(baseType)}.") { Namespace = NamespaceOf(baseType) };
                            }
                        }

                        break;

                    case SimpleNameSyntax name when bySymbol.Count > 0 && name is not IdentifierNameSyntax { IsVar: true }:
                        // Namespaces are not uses: the types and members used from them are.
                        if (Bound(model, name) is { } symbol and not INamespaceSymbol)
                        {
                            foreach (var rule in Lookup(bySymbol, Keys(symbol)))
                            {
                                yield return new RawFinding(rule, name.GetLocation(), Name(symbol)) { Namespace = NamespaceOf(symbol) };
                            }
                        }

                        break;
                }
            }
        }
    }

    private static Dictionary<string, List<AuditRule>> Index(IReadOnlyList<AuditRule> rules, Func<AuditRule, IReadOnlyList<string>> patterns)
    {
        var index = new Dictionary<string, List<AuditRule>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            foreach (var pattern in patterns(rule))
            {
                (index.TryGetValue(pattern, out var list) ? list : index[pattern] = []).Add(rule);
            }
        }

        return index;
    }

    private static IEnumerable<AuditRule> Lookup(Dictionary<string, List<AuditRule>> index, IEnumerable<string> keys) =>
        keys.SelectMany(k => index.TryGetValue(k, out var rules) ? rules : []).Distinct();

    /// <summary>
    /// The documentation IDs a symbol answers to: its own (with and without parameters), each
    /// containing type's, and each enclosing namespace's.
    /// </summary>
    public static IEnumerable<string> Keys(ISymbol symbol)
    {
        var definition = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol.OriginalDefinition;
        if (definition.GetDocumentationCommentId() is { } id)
        {
            yield return id;
            var paren = id.IndexOf('(', StringComparison.Ordinal);
            if (paren > 0)
            {
                yield return id[..paren];
            }

            var arity = id.IndexOf("``", StringComparison.Ordinal);
            if (arity > 0)
            {
                yield return id[..arity];
            }
        }

        for (var type = definition as INamedTypeSymbol ?? definition.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.OriginalDefinition.GetDocumentationCommentId() is { } typeId && typeId != definition.GetDocumentationCommentId())
            {
                yield return typeId;
            }
        }

        for (var ns = definition.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            yield return "N:" + ns.ToDisplayString();
        }
    }

    private static IEnumerable<string> TypeAndBases(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.GetDocumentationCommentId() is { } id)
            {
                yield return id;
            }
        }
    }

    /// <summary>The symbol a name binds to, or its only candidate (overload resolution failures still name the API).</summary>
    public static ISymbol? Bound(SemanticModel model, SyntaxNode node)
    {
        var info = model.GetSymbolInfo(node);
        return info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
    }

    /// <summary>The namespace a symbol lives in (a type's own for a type), or null for the global namespace.</summary>
    public static string? NamespaceOf(ISymbol symbol)
    {
        var ns = symbol as INamespaceSymbol ?? symbol.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? null : ns.ToDisplayString();
    }

    /// <summary>A symbol's fully qualified name without <c>global::</c>.</summary>
    public static string Name(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    /// <summary>Repository-relative file, 1-based line and column.</summary>
    public static (string File, int Line, int Column) Position(string repositoryRoot, Location location)
    {
        var span = location.GetLineSpan();
        var path = span.Path;
        var file = Path.IsPathRooted(path) ? RepoPaths.ToRepositoryRelative(repositoryRoot, Path.GetFullPath(path)) : path.Replace('\\', '/');
        return (file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}
