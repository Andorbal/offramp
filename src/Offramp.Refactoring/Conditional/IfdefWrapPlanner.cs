using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Offramp.Analysis.Compilations;
using Offramp.Analysis.Conditional;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;

namespace Offramp.Refactoring.Conditional;

/// <summary>An audit finding as <c>ifdef wrap</c> reads it.</summary>
public sealed record WrapFinding(string Rule, string File, int Line, int Column, string Symbol)
{
    /// <summary>
    /// The findings of an <c>audit</c> result: the <c>--format json</c> document, or the
    /// <c>--json</c> envelope around it.
    /// </summary>
    public static IReadOnlyList<WrapFinding> Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
            ?? throw new JsonException("The findings file is empty.");
        var result = root["result"] as JsonObject ?? root as JsonObject ?? throw new JsonException("The findings file is not an audit result.");
        if (result["findings"] is not JsonArray findings)
        {
            throw new JsonException("The findings file has no findings array; pass the output of `offramp audit api --format json`.");
        }

        return [.. findings.OfType<JsonObject>().Select(f => new WrapFinding(
            f["rule"]?.GetValue<string>() ?? "",
            f["file"]?.GetValue<string>() ?? "",
            f["line"]?.GetValue<int>() ?? 0,
            f["column"]?.GetValue<int>() ?? 1,
            f["symbol"]?.GetValue<string>() ?? ""))];
    }
}

public sealed record IfdefWrapRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required IReadOnlyList<WrapFinding> Findings { get; init; }

    /// <summary>The condition written after <c>#if</c>: <c>NETFRAMEWORK</c>, or <c>!NET10_0_OR_GREATER</c>.</summary>
    public string Condition { get; init; } = "NETFRAMEWORK";

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>A range wrapped in <c>#if</c>: the original lines (1-based, inclusive) and the findings it covers.</summary>
public sealed record IfdefWrap(string File, int StartLine, int EndLine, string Kind, string Target, IReadOnlyList<string> Rules, int Findings);

/// <summary>A finding left unwrapped, with the code that says why (OFR3601, OFR3602).</summary>
public sealed record IfdefNotWrapped(string File, int Line, string Rule, string Symbol, string Code, string Reason);

/// <summary>The <c>result</c> of <c>offramp ifdef wrap</c> (<c>schemas/v1/ifdef-wrap.json</c>).</summary>
public sealed record IfdefWrapResult
{
    public required string Condition { get; init; }

    /// <summary>C# findings read from the file.</summary>
    public required int Findings { get; init; }

    /// <summary>Findings already inside a region with the same condition.</summary>
    public required int AlreadyGuarded { get; init; }

    public required IReadOnlyList<IfdefWrap> Wraps { get; init; }

    public required IReadOnlyList<IfdefNotWrapped> NotWrapped { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record IfdefWrapPlan(IfdefWrapResult Result, ChangeSet? ChangeSet);

/// <summary>
/// <c>ifdef wrap</c>: wraps the code behind audit findings in <c>#if CONDITION</c> …
/// <c>#endif</c>, inserting whole lines and changing nothing else.
/// <list type="bullet">
/// <item>The smallest enclosing statement is wrapped when removing it cannot break the method
/// on the other targets: it sits in a block on lines of its own, declares no local used after
/// it, and has no <c>return</c>, <c>throw</c>, <c>break</c>, <c>continue</c>, <c>goto</c>, or
/// <c>yield</c> of its own.</item>
/// <item>Otherwise the enclosing member (or type) is wrapped, with its attributes and
/// documentation comment, unless code that compiles on every target needs it: a reference
/// from outside the wrapped ranges and existing regions with the same condition (in any
/// project), an override, or an interface implementation (OFR3601). Members that only other
/// wrapped code uses are wrapped together.</item>
/// </list>
/// </summary>
public static class IfdefWrapPlanner
{
    public static IfdefWrapPlan Plan(IfdefWrapRequest request)
    {
        var sources = new Dictionary<string, (SourceLines Lines, SyntaxTree Tree, IReadOnlyList<DirectiveChain> Chains)>(StringComparer.Ordinal);
        var candidates = new List<Candidate>();
        var notWrapped = new List<IfdefNotWrapped>();
        var findings = request.Findings.Where(f => f.File.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();
        var guarded = 0;

        foreach (var finding in findings)
        {
            var path = RepoPaths.ToAbsolute(request.RepositoryRoot, finding.File);
            if (!File.Exists(path))
            {
                Stale(request, notWrapped, finding, "the file no longer exists");
                continue;
            }

            if (!sources.TryGetValue(finding.File, out var source))
            {
                var lines = SourceLines.Read(path);
                source = (lines, CSharpSyntaxTree.ParseText(SourceText.From(lines.Text), Options(request.Model, finding.File), path), ConditionalDirectives.Chains(lines.Text));
                sources[finding.File] = source;
            }

            if (Guarded(source.Chains, finding.Line - 1, request.Condition))
            {
                guarded++;
                continue;
            }

            var text = source.Tree.GetText();
            if (finding.Line < 1 || finding.Line > text.Lines.Count)
            {
                Stale(request, notWrapped, finding, "the line is past the end of the file");
                continue;
            }

            var position = Math.Min(text.Lines[finding.Line - 1].Start + Math.Max(0, finding.Column - 1), text.Lines[finding.Line - 1].End);
            var token = source.Tree.GetRoot().FindToken(position);
            if (token.ValueText != SimpleName(finding.Symbol))
            {
                Stale(request, notWrapped, finding, $"'{token.ValueText}' is there, not '{SimpleName(finding.Symbol)}'");
                continue;
            }

            var candidate = Target(source.Lines, token, finding);
            if (candidate.Problem is { } problem)
            {
                NotWrappable(request, notWrapped, finding, problem);
                continue;
            }

            candidates.Add(candidate);
        }

        var accepted = Accept(request, sources, candidates, notWrapped);
        var (wraps, changeSet) = Edit(request, sources, accepted);
        var result = new IfdefWrapResult
        {
            Condition = request.Condition,
            Findings = findings.Count,
            AlreadyGuarded = guarded,
            Wraps = wraps,
            NotWrapped = [.. notWrapped.OrderBy(n => n.File, StringComparer.Ordinal).ThenBy(n => n.Line).ThenBy(n => n.Rule, StringComparer.Ordinal)],
            Files = [.. changeSet.Edits.Select(e => e.Path).Order(StringComparer.Ordinal)],
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new IfdefWrapPlan(result, changeSet.IsEmpty ? null : changeSet);
    }

    /// <summary>
    /// Parse options with the preprocessor symbols of the build the audit read (the owning
    /// project's .NET Framework target), so the code a finding names is code, not disabled text.
    /// </summary>
    private static CSharpParseOptions Options(WorkspaceModel model, string file)
    {
        var project = model.Projects.Where(p => p.Compile.Contains(file, StringComparer.Ordinal)).OrderBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
        var target = project is null ? null : CompilationLoader.PreferredTarget(project) ?? project.TargetFrameworks.FirstOrDefault();
        var symbols = project is not null && target is not null && project.DefineConstants.TryGetValue(target, out var defined) ? defined : [];
        return new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols);
    }

    /// <summary>What a finding would wrap: a statement or a member (0-based line range), or why neither works.</summary>
    private sealed record Candidate(WrapFinding Finding, string File, int Start, int End, string Kind, string Target, SyntaxNode? Member, string? Problem = null);

    private static Candidate Target(SourceLines lines, SyntaxToken token, WrapFinding finding)
    {
        var statement = token.Parent?.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault(s => s is not BlockSyntax && s.Parent is BlockSyntax);
        if (statement is not null && StatementIsRemovable(statement) && OwnLines(lines, statement.FullSpan, statement.Span) is var (start, end))
        {
            var owner = statement.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            return new Candidate(finding, finding.File, start, end, "statement", Describe(owner) + ": " + FirstLine(statement), null);
        }

        var member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseNamespaceDeclarationSyntax and not GlobalStatementSyntax);
        if (member is null)
        {
            return new Candidate(finding, finding.File, 0, 0, "member", "", null, "it is not inside a member (a using directive or an assembly attribute)");
        }

        var span = TextSpan.FromBounds(DocumentationStart(member), member.Span.End);
        return OwnLines(lines, member.FullSpan, span) is var (memberStart, memberEnd)
            ? new Candidate(finding, finding.File, memberStart, memberEnd, "member", Describe(member), member)
            : new Candidate(finding, finding.File, 0, 0, "member", Describe(member), member, $"{Describe(member)} shares its lines with other code");
    }

    /// <summary>Removing the statement leaves the method valid: no exits of its own and no local used later.</summary>
    private static bool StatementIsRemovable(StatementSyntax statement)
    {
        var exits = statement.DescendantNodesAndSelf(n => n == statement || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Any(n => n is ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax or GotoStatementSyntax or YieldStatementSyntax or ThrowExpressionSyntax);
        if (exits || statement is LabeledStatementSyntax or LocalFunctionStatementSyntax)
        {
            return false;
        }

        if (statement is LocalDeclarationStatementSyntax declaration && statement.Parent is BlockSyntax block)
        {
            var names = declaration.Declaration.Variables.Select(v => v.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
            return !block.Statements.Where(s => s.SpanStart > statement.Span.End)
                .SelectMany(s => s.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                .Any(n => names.Contains(n.Identifier.ValueText));
        }

        return true;
    }

    /// <summary>The 0-based lines a span covers, when nothing else shares them (whitespace and comments aside).</summary>
    private static (int Start, int End)? OwnLines(SourceLines lines, TextSpan full, TextSpan span)
    {
        var text = lines.Text;
        var lineStarts = LineStarts(lines);
        var start = LineOf(lineStarts, span.Start);
        var end = LineOf(lineStarts, span.End - 1);
        var before = text[lineStarts[start]..span.Start];
        var afterEnd = end + 1 < lineStarts.Count ? lineStarts[end + 1] : text.Length;
        var after = text[span.End..afterEnd].Trim();
        return string.IsNullOrWhiteSpace(before) && (after.Length == 0 || after.StartsWith("//", StringComparison.Ordinal)) ? (start, end) : null;
    }

    private static List<int> LineStarts(SourceLines lines)
    {
        var starts = new List<int>(lines.Lines.Count);
        var offset = 0;
        foreach (var line in lines.Lines)
        {
            starts.Add(offset);
            offset += line.Length;
        }

        return starts;
    }

    private static int LineOf(List<int> starts, int position)
    {
        var index = starts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>Where a member starts counting its documentation comment.</summary>
    private static int DocumentationStart(MemberDeclarationSyntax member)
    {
        var doc = member.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        return doc == default ? member.Span.Start : doc.SpanStart;
    }

    /// <summary>Whether the line already sits in a branch whose condition is exactly this one.</summary>
    private static bool Guarded(IReadOnlyList<DirectiveChain> chains, int line, string condition) =>
        chains.SelectMany(c => c.Branches).Any(b => b.Condition is not null && Normalize(b.Condition.ToString()) == Normalize(condition) && line >= b.FirstLine && line <= b.LastLine);

    private static string Normalize(string condition) => string.Concat(condition.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Members are wrapped only when code compiled on every target does not need them; a member
    /// used only from other wrapped code goes with it (a fixed point over the candidates).
    /// </summary>
    private static List<Candidate> Accept(
        IfdefWrapRequest request,
        Dictionary<string, (SourceLines Lines, SyntaxTree Tree, IReadOnlyList<DirectiveChain> Chains)> sources,
        List<Candidate> candidates,
        List<IfdefNotWrapped> notWrapped)
    {
        var members = candidates.Where(c => c.Member is not null).ToList();
        var references = members.Count == 0 ? new Dictionary<Candidate, (List<(string File, int Line)> Uses, string? Blocker)>()
            : References(request, sources, members);
        var accepted = candidates.ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var member in members.Where(accepted.Contains).ToList())
            {
                var (uses, blocker) = references[member];
                var shared = uses.FirstOrDefault(u => !Covered(accepted, u.File, u.Line) && !InGuardedRegion(request, sources, u.File, u.Line));
                if (blocker is null && shared == default)
                {
                    continue;
                }

                accepted.Remove(member);
                changed = true;
                NotWrappable(request, notWrapped, member.Finding, blocker ?? $"{member.Target} is used at {shared.File}:{shared.Line + 1}, which compiles on every target");
            }
        }
        while (changed);

        return [.. accepted];
    }

    private static bool Covered(HashSet<Candidate> accepted, string file, int line) =>
        accepted.Any(c => c.File == file && line >= c.Start && line <= c.End);

    private static bool InGuardedRegion(
        IfdefWrapRequest request, Dictionary<string, (SourceLines Lines, SyntaxTree Tree, IReadOnlyList<DirectiveChain> Chains)> sources, string file, int line)
    {
        if (!sources.TryGetValue(file, out var source))
        {
            var path = RepoPaths.ToAbsolute(request.RepositoryRoot, file);
            if (!File.Exists(path))
            {
                return false;
            }

            var lines = SourceLines.Read(path);
            source = (lines, CSharpSyntaxTree.ParseText(lines.Text, path: path), ConditionalDirectives.Chains(lines.Text));
            sources[file] = source;
        }

        return Guarded(source.Chains, line, request.Condition);
    }

    /// <summary>
    /// For each member candidate: where the solution uses it (every project's recorded
    /// compilation, matched by documentation ID), or why it must stay (an override or an
    /// interface implementation).
    /// </summary>
    private static Dictionary<Candidate, (List<(string File, int Line)> Uses, string? Blocker)> References(
        IfdefWrapRequest request,
        Dictionary<string, (SourceLines Lines, SyntaxTree Tree, IReadOnlyList<DirectiveChain> Chains)> sources,
        List<Candidate> members)
    {
        using var loader = new CompilationLoader(request.RepositoryRoot);
        var compilations = request.Model.Projects
            .Where(p => p.Language == "csharp")
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => (Project: p, Compilation: loader.LoadForProject(p)))
            .Where(p => p.Compilation is not null)
            .ToList();

        var symbols = new Dictionary<Candidate, ISymbol?>();
        foreach (var member in members)
        {
            symbols[member] = Declared(request.RepositoryRoot, compilations.Select(c => c.Compilation!), member, sources[member.File].Tree);
        }

        var ids = symbols.Where(s => s.Value is not null)
            .Select(s => (Candidate: s.Key, Id: s.Value!.OriginalDefinition.GetDocumentationCommentId()))
            .Where(s => s.Id is not null)
            .GroupBy(s => s.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Candidate).ToList(), StringComparer.Ordinal);
        var names = symbols.Values.OfType<ISymbol>().Select(s => s is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor ? ctor.ContainingType.Name : s.Name).ToHashSet(StringComparer.Ordinal);
        var uses = members.ToDictionary(m => m, _ => new List<(string File, int Line)>());

        foreach (var tree in compilations.SelectMany(c => c.Compilation!.SyntaxTrees.Select(t => (c.Compilation, Tree: t))).DistinctBy(t => t.Tree.FilePath))
        {
            var file = RepoPaths.ToRepositoryRelative(request.RepositoryRoot, Path.GetFullPath(tree.Tree.FilePath));
            if (file.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }

            var model = tree.Compilation!.GetSemanticModel(tree.Tree);
            foreach (var node in tree.Tree.GetRoot().DescendantNodes())
            {
                var named = node switch
                {
                    SimpleNameSyntax simple when names.Contains(simple.Identifier.ValueText) => node,
                    BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax => node,
                    _ => null,
                };
                if (named is null || model.GetSymbolInfo(named).Symbol is not { } used
                    || used.OriginalDefinition.GetDocumentationCommentId() is not { } id || !ids.TryGetValue(id, out var owners))
                {
                    continue;
                }

                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line;
                foreach (var owner in owners.Where(o => !(o.File == file && line >= o.Start && line <= o.End)))
                {
                    uses[owner].Add((file, line));
                }
            }
        }

        return members.ToDictionary(m => m, m => (uses[m], Blocker(m, symbols[m])));
    }

    /// <summary>The member's symbol in a recorded compilation holding the file, when the recorded text still matches.</summary>
    private static ISymbol? Declared(string root, IEnumerable<Compilation> compilations, Candidate member, SyntaxTree current)
    {
        foreach (var compilation in compilations)
        {
            var tree = compilation.SyntaxTrees.FirstOrDefault(t => RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(t.FilePath)) == member.File);
            if (tree is null || !tree.GetText().ContentEquals(current.GetText()))
            {
                continue;
            }

            var node = tree.GetRoot().FindNode(member.Member!.Span);
            if (node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault() is { } declaration)
            {
                var model = compilation.GetSemanticModel(tree);
                return declaration is BaseFieldDeclarationSyntax field
                    ? model.GetDeclaredSymbol(field.Declaration.Variables[0])
                    : model.GetDeclaredSymbol(declaration);
            }
        }

        return null;
    }

    /// <summary>Why the member must exist on every target regardless of references.</summary>
    private static string? Blocker(Candidate member, ISymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        if (symbol.IsOverride || symbol.IsAbstract)
        {
            return $"{member.Target} {(symbol.IsOverride ? "overrides" : "is")} an abstract or virtual member that every target has";
        }

        if (symbol.ContainingType is { } type && symbol is not INamedTypeSymbol)
        {
            foreach (var implemented in type.AllInterfaces.SelectMany(i => i.GetMembers()))
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(implemented), symbol))
                {
                    return $"{member.Target} implements {implemented.ContainingType.Name}.{implemented.Name}";
                }
            }
        }

        return null;
    }

    private static (List<IfdefWrap> Wraps, ChangeSet ChangeSet) Edit(
        IfdefWrapRequest request, Dictionary<string, (SourceLines Lines, SyntaxTree Tree, IReadOnlyList<DirectiveChain> Chains)> sources, List<Candidate> accepted)
    {
        var wraps = new List<IfdefWrap>();
        var changeSet = new ChangeSet();
        foreach (var file in accepted.Select(c => c.File).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var lines = sources[file].Lines;

            // Outermost ranges only (a member absorbs its statements), then adjacent ranges merge.
            var ranges = accepted.Where(c => c.File == file).OrderBy(c => c.Start).ThenByDescending(c => c.End).ToList();
            var merged = new List<(int Start, int End, List<Candidate> Items)>();
            foreach (var range in ranges)
            {
                if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
                {
                    var last = merged[^1];
                    last.Items.Add(range);
                    merged[^1] = (last.Start, Math.Max(last.End, range.End), last.Items);
                }
                else
                {
                    merged.Add((range.Start, range.End, [range]));
                }
            }

            var output = new List<string>();
            var next = 0;
            foreach (var (start, end, items) in merged)
            {
                output.AddRange(lines.Lines.Skip(next).Take(start - next));
                // Directives at the start of the line, as dotnet format and Visual Studio place them.
                var endLine = lines.Lines[end];
                output.Add("#if " + request.Condition + lines.NewLine);
                output.AddRange(lines.Lines.Skip(start).Take(end - start + 1));
                if (!endLine.EndsWith('\n'))
                {
                    output[^1] += lines.NewLine;
                }

                output.Add("#endif" + (endLine.EndsWith('\n') ? lines.NewLine : ""));
                next = end + 1;

                wraps.Add(new IfdefWrap(
                    file, start + 1, end + 1,
                    items.Any(i => i.Kind == "member") ? "member" : "statement",
                    string.Join("; ", items.Where(i => !items.Any(o => o != i && o.Start <= i.Start && o.End >= i.End && (o.End - o.Start) > (i.End - i.Start))).Select(i => i.Target).Distinct(StringComparer.Ordinal)),
                    [.. items.Select(i => i.Finding.Rule).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                    items.Count));
            }

            output.AddRange(lines.Lines.Skip(next));
            changeSet.Edit(file, lines.Bytes, lines.Encode(output));
        }

        return (wraps, changeSet);
    }

    private static void Stale(IfdefWrapRequest request, List<IfdefNotWrapped> notWrapped, WrapFinding finding, string why)
    {
        var reason = $"The finding no longer matches the source: {why}.";
        notWrapped.Add(new IfdefNotWrapped(finding.File, finding.Line, finding.Rule, finding.Symbol, "OFR3602", reason));
        request.Diagnostics.Report(DiagnosticCatalog.OFR3602, $"{finding.Rule} {finding.Symbol}: {reason}", new DiagnosticLocation(null, finding.File, finding.Line, finding.Column));
    }

    private static void NotWrappable(IfdefWrapRequest request, List<IfdefNotWrapped> notWrapped, WrapFinding finding, string why)
    {
        var reason = $"Not wrapped: {why}; it needs a real port.";
        notWrapped.Add(new IfdefNotWrapped(finding.File, finding.Line, finding.Rule, finding.Symbol, "OFR3601", reason));
        request.Diagnostics.Report(DiagnosticCatalog.OFR3601, $"{finding.Rule} {finding.Symbol}: {reason}", new DiagnosticLocation(null, finding.File, finding.Line, finding.Column));
    }

    /// <summary>The last identifier of a reported symbol: <c>System.Console.Beep(int, int)</c> → <c>Beep</c>.</summary>
    internal static string SimpleName(string symbol)
    {
        var name = symbol;
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            name = name[..paren];
        }

        var angle = name.IndexOf('<', StringComparison.Ordinal);
        if (angle >= 0)
        {
            name = name[..angle];
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static string Describe(SyntaxNode? member) => member switch
    {
        null => "top level",
        MethodDeclarationSyntax method => method.Identifier.ValueText + "()",
        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText + " constructor",
        PropertyDeclarationSyntax property => property.Identifier.ValueText,
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        BaseFieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        EventDeclarationSyntax @event => @event.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        _ => member.Kind().ToString(),
    };

    private static string FirstLine(SyntaxNode node)
    {
        var text = node.ToString();
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return (newline >= 0 ? text[..newline] : text).Trim();
    }
}
