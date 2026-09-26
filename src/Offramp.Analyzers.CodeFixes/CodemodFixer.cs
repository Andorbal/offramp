using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Offramp.Analyzers.CodeFixes;

/// <summary>
/// The fixer half of a codemod. Every fix goes through <see cref="FixDocumentAsync"/>, which
/// rewrites the given sites of one document in one pass, so a single fix, fix-all, and
/// <c>offramp codemod run</c> produce the same text. Skipped sites are never fixed.
/// </summary>
public abstract class CodemodFixer : CodeFixProvider
{
    public abstract Codemod Codemod { get; }

    public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(Codemod.Id);

    public sealed override FixAllProvider GetFixAllProvider() =>
        FixAllProvider.Create(async (context, document, diagnostics) =>
            await FixDocumentAsync(document, Fixable(diagnostics), context.CancellationToken).ConfigureAwait(false));

    public sealed override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics.Where(d => !Codemods.IsSkipped(d)))
        {
            context.RegisterCodeFix(
                CodeAction.Create(Codemod.Title, ct => FixDocumentAsync(context.Document, ImmutableArray.Create(diagnostic), ct), Codemod.Id),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    /// <summary>Rewrites the sites of <paramref name="diagnostics"/> (all of this codemod, none skipped) in the document.</summary>
    public abstract Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken);

    /// <summary>What a code action does after its change: simplifies and formats the nodes the fixer annotated.</summary>
    public static async Task<Document> CleanupAsync(Document document, CancellationToken cancellationToken)
    {
        document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static ImmutableArray<Diagnostic> Fixable(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Where(d => !Codemods.IsSkipped(d)).ToImmutableArray();
}
