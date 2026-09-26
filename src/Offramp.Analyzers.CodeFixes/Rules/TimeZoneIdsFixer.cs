using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class TimeZoneIdsFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.TimeZoneIds;

    protected override async Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var invocations = diagnostics.Select(d => root!.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<InvocationExpressionSyntax>())
            .OfType<InvocationExpressionSyntax>().Distinct().ToList();
        var fixedRoot = root!.ReplaceNodes(invocations, (original, _) =>
            original.WithExpression(Simplify.Names(SyntaxFactory.ParseExpression("TimeZoneConverter.TZConvert.GetTimeZoneInfo")
                .WithTriviaFrom(original.Expression))));
        return document.WithSyntaxRoot(fixedRoot);
    }
}
