using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Offramp.Analyzers.CodeFixes;

internal static class Simplify
{
    /// <summary>
    /// Marks every qualified name in a new node for the simplifier, which shortens
    /// <c>System.StringComparison.Ordinal</c> to <c>StringComparison.Ordinal</c> where a using
    /// allows (it only reduces the annotated nodes themselves).
    /// </summary>
    public static T Names<T>(T node)
        where T : SyntaxNode =>
        node.ReplaceNodes(
                node.DescendantNodesAndSelf().Where(n => n is QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax),
                (_, rewritten) => rewritten.WithAdditionalAnnotations(Simplifier.Annotation))
            .WithAdditionalAnnotations(Simplifier.Annotation);
}
