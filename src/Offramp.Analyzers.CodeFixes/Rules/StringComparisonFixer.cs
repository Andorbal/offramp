using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class StringComparisonFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.StringComparison;

    protected override async Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var nodes = diagnostics.Select(d => root!.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true))
            .Select(n => (SyntaxNode?)n.FirstAncestorOrSelf<InvocationExpressionSyntax>(i => i.Span == n.Span) ?? n.FirstAncestorOrSelf<BinaryExpressionSyntax>(b => b.Span == n.Span))
            .OfType<ExpressionSyntax>().Distinct().ToList();
        var fixedRoot = root!.ReplaceNodes(nodes, (original, _) => Rewrite(model!, original) ?? original);
        return document.WithSyntaxRoot(fixedRoot);
    }

    private static ExpressionSyntax? Rewrite(SemanticModel model, ExpressionSyntax node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method:
                var ignoreCase = method.Name == "Compare" && invocation.ArgumentList.Arguments.Count == 3
                    && model.GetConstantValue(invocation.ArgumentList.Arguments[2].Expression).Value is true;
                var comparison = Comparison(ignoreCase);
                var arguments = invocation.ArgumentList.Arguments;
                if (method.Name == "Compare" && arguments.Count == 3)
                {
                    arguments = arguments.RemoveAt(2);
                }

                return invocation.WithArgumentList(invocation.ArgumentList.WithArguments(Lists.Append(arguments, SyntaxFactory.Argument(comparison))));
            case BinaryExpressionSyntax binary:
                var left = Folded(binary.Left);
                var right = Folded(binary.Right);
                if (left is null || right is null)
                {
                    return null;
                }

                var equals = (ExpressionSyntax)Simplify.Names(SyntaxFactory.ParseExpression($"string.Equals({left}, {right}, System.StringComparison.OrdinalIgnoreCase)"));
                return (binary.IsKind(SyntaxKind.NotEqualsExpression) ? SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, equals) : equals)
                    .WithTriviaFrom(binary);
            default:
                return null;
        }
    }

    private static ExpressionSyntax Comparison(bool ignoreCase) =>
        Simplify.Names(SyntaxFactory.ParseExpression("System.StringComparison." + (ignoreCase ? "OrdinalIgnoreCase" : "Ordinal")));

    /// <summary>The receiver of <c>x.ToLower()</c>: <c>x</c>.</summary>
    private static ExpressionSyntax? Folded(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access, ArgumentList.Arguments.Count: 0 } ? access.Expression.WithoutTrivia() : null;
}
