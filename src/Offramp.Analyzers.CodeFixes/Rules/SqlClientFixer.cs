using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class SqlClientFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.SqlClient;

    protected override async Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var nodes = diagnostics.Select(d => root!.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true))
            .OfType<ExpressionSyntax>().Distinct().ToList();
        var fixedRoot = root!.ReplaceNodes(nodes, (original, _) =>
            (original is NameSyntax ? SyntaxFactory.ParseName("Microsoft.Data.SqlClient") : SyntaxFactory.ParseExpression("Microsoft.Data.SqlClient"))
                .WithTriviaFrom(original));
        return document.WithSyntaxRoot(fixedRoot);
    }
}
