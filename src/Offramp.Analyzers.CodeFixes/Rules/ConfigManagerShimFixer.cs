using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analyzers.Rules;

namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class ConfigManagerShimFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.ConfigManagerShim;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var sites = diagnostics
            .Where(d => d.Properties.ContainsKey(ConfigManagerShimAnalyzer.Shim))
            .Select(d => (Node: root!.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<MemberAccessExpressionSyntax>(), Shim: d.Properties[ConfigManagerShimAnalyzer.Shim]!))
            .Where(s => s.Node is not null)
            .GroupBy(s => s.Node!)
            .ToDictionary(g => g.Key, g => g.First().Shim);
        var fixedRoot = root!.ReplaceNodes(sites.Keys, (original, _) =>
            original.WithExpression(Simplify.Names(SyntaxFactory.ParseExpression(sites[original])).WithTriviaFrom(original.Expression)));
        return document.WithSyntaxRoot(fixedRoot);
    }
}
