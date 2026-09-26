using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offramp.Analysis.Conditional;

/// <summary>One branch of an <c>#if</c> chain: its directive line and the lines it guards (0-based).</summary>
/// <param name="DirectiveLine">The <c>#if</c>, <c>#elif</c>, or <c>#else</c> line.</param>
/// <param name="Condition">The condition; null for <c>#else</c>.</param>
/// <param name="FirstLine">The first guarded line.</param>
/// <param name="LastLine">The last guarded line (less than <paramref name="FirstLine"/> when the branch is empty).</param>
public sealed record DirectiveBranch(int DirectiveLine, ExpressionSyntax? Condition, int FirstLine, int LastLine)
{
    public int Lines => Math.Max(0, LastLine - FirstLine + 1);

    public string Kind => Condition is null ? "else" : "if";
}

/// <summary>An <c>#if</c> … <c>#endif</c> chain.</summary>
public sealed record DirectiveChain(IReadOnlyList<DirectiveBranch> Branches, int EndifLine)
{
    public int IfLine => Branches[0].DirectiveLine;

    /// <summary>The symbols the conditions name, sorted.</summary>
    public IReadOnlyList<string> Symbols { get; } = [.. Branches
        .Where(b => b.Condition is not null)
        .SelectMany(b => b.Condition!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        .Select(n => n.Identifier.ValueText)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)];

    /// <summary>Lines inside the chain, directives excluded.</summary>
    public int GuardedLines => Branches.Sum(b => b.Lines);

    public string ConditionText => Branches[0].Condition?.ToString() ?? "";
}

/// <summary>
/// Conditional compilation regions from the syntax tree (the parser records every directive,
/// active or not), and conditions evaluated with one symbol known and the rest unknown.
/// </summary>
public static class ConditionalDirectives
{
    /// <summary>Every well-formed chain in the file, outermost first by position.</summary>
    public static IReadOnlyList<DirectiveChain> Chains(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var chains = new List<DirectiveChain>();
        for (var current = tree.GetRoot().GetFirstDirective(); current is not null; current = current.GetNextDirective())
        {
            if (current is not IfDirectiveTriviaSyntax directive)
            {
                continue;
            }

            var related = directive.GetRelatedDirectives();
            if (related.Count < 2 || related[^1] is not EndIfDirectiveTriviaSyntax endif)
            {
                continue;
            }

            var branches = new List<DirectiveBranch>();
            for (var i = 0; i < related.Count - 1; i++)
            {
                var line = Line(related[i]);
                var next = Line(related[i + 1]);
                var condition = related[i] switch
                {
                    IfDirectiveTriviaSyntax ifDirective => ifDirective.Condition,
                    ElifDirectiveTriviaSyntax elif => elif.Condition,
                    _ => null,
                };
                branches.Add(new DirectiveBranch(line, condition, line + 1, next - 1));
            }

            chains.Add(new DirectiveChain(branches, Line(endif)));
        }

        return [.. chains.OrderBy(c => c.IfLine)];
    }

    /// <summary>
    /// A condition's value when <paramref name="symbol"/> is <paramref name="defined"/> and every
    /// other symbol is unknown: true, false, or null when it depends on the others.
    /// </summary>
    public static bool? Evaluate(ExpressionSyntax condition, string symbol, bool defined) => condition switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText == symbol ? defined : null,
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) => true,
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression) => false,
        ParenthesizedExpressionSyntax parenthesized => Evaluate(parenthesized.Expression, symbol, defined),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not => !Evaluate(not.Operand, symbol, defined),
        BinaryExpressionSyntax binary => Binary(binary, symbol, defined),
        _ => null,
    };

    private static bool? Binary(BinaryExpressionSyntax binary, string symbol, bool defined)
    {
        var left = Evaluate(binary.Left, symbol, defined);
        var right = Evaluate(binary.Right, symbol, defined);
        return binary.Kind() switch
        {
            SyntaxKind.LogicalAndExpression => left == false || right == false ? false : left == true && right == true ? true : null,
            SyntaxKind.LogicalOrExpression => left == true || right == true ? true : left == false && right == false ? false : null,
            SyntaxKind.EqualsExpression => left is { } l && right is { } r ? l == r : null,
            SyntaxKind.NotEqualsExpression => left is { } l2 && right is { } r2 ? l2 != r2 : null,
            _ => null,
        };
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line;
}
