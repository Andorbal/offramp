using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;


namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class AssemblyInfoFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.AssemblyInfo;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var attributes = diagnostics.Select(d => root!.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<AttributeSyntax>())
            .OfType<AttributeSyntax>().ToImmutableHashSet();
        var lists = attributes.Select(a => (AttributeListSyntax)a.Parent!).Distinct().ToList();
        var empty = lists.Where(l => l.Attributes.All(attributes.Contains)).ToList();
        // Attributes that share a list go without their trivia; whole lists keep the lines above them.
        var tracked = root!.TrackNodes(empty.Cast<SyntaxNode>().Concat(attributes));
        var fixedRoot = tracked.RemoveNodes(attributes.Where(a => !empty.Contains((AttributeListSyntax)a.Parent!)).Select(a => tracked.GetCurrentNode(a)!), SyntaxRemoveOptions.KeepNoTrivia)!;
        fixedRoot = fixedRoot.RemoveNodes(empty.Select(l => fixedRoot.GetCurrentNode(l)!), SyntaxRemoveOptions.KeepLeadingTrivia | SyntaxRemoveOptions.KeepDirectives)!;
        return document.WithSyntaxRoot(fixedRoot);
    }
}
