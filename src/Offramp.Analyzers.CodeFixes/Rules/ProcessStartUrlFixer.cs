using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class ProcessStartUrlFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.ProcessStartUrl;

    protected override async Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var invocations = diagnostics.Select(d => root!.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true))
            .Select(n => n.FirstAncestorOrSelf<InvocationExpressionSyntax>())
            .OfType<InvocationExpressionSyntax>()
            .Distinct()
            .ToList();
        var fixedRoot = root!.ReplaceNodes(invocations, (original, _) =>
        {
            var arguments = string.Join(", ", original.ArgumentList.Arguments.Select(a => a.Expression.ToString()));
            var info = Simplify.Names(SyntaxFactory.ParseExpression($"new System.Diagnostics.ProcessStartInfo({arguments}) {{ UseShellExecute = true }}"));
            return original.WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(info)))
                .WithTriviaFrom(original.ArgumentList));
        });
        return document.WithSyntaxRoot(fixedRoot);
    }
}
