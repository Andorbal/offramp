using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;

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

    /// <summary>Rewrites the sites of <paramref name="diagnostics"/> (all of this codemod, none skipped) in the document, then cleans up.</summary>
    public async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var fixedDocument = await FixSitesAsync(document, diagnostics, cancellationToken).ConfigureAwait(false);
        fixedDocument = await CleanupAsync(fixedDocument, cancellationToken).ConfigureAwait(false);
        return await MatchLineEndingsAsync(document, fixedDocument, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fix's changes with the file's line ending: fixers build code from strings with
    /// <c>\n</c>, and the formatter adds its own; a CRLF file must stay CRLF.
    /// </summary>
    private static async Task<Document> MatchLineEndingsAsync(Document original, Document fixedDocument, CancellationToken cancellationToken)
    {
        var before = await original.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var newLine = LineEnding(before.ToString());
        var changes = await fixedDocument.GetTextChangesAsync(original, cancellationToken).ConfigureAwait(false);
        var matched = changes.Select(c => new TextChange(c.Span, (c.NewText ?? "").Replace("\r\n", "\n").Replace("\n", newLine))).ToList();
        return original.WithText(before.WithChanges(matched));
    }

    /// <summary>Rewrites the sites, annotating what <see cref="CleanupAsync"/> simplifies and formats.</summary>
    protected abstract Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken);

    /// <summary>Simplifies and formats the nodes the fixer annotated, as a code action's cleanup would, and removes the annotations.</summary>
    /// <remarks>
    /// New lines follow the file's line ending, not the formatter's (the platform's or
    /// end_of_line from .editorconfig): a fix never mixes line endings in a file. The code
    /// action's own cleanup, which formats with the formatter's line ending, then finds
    /// nothing annotated.
    /// </remarks>
    public static async Task<Document> CleanupAsync(Document document, CancellationToken cancellationToken)
    {
        document = await Simplifier.ReduceAsync(document, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
        var formatting = options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, LineEnding(text.ToString()));
        document = await Formatter.FormatAsync(document, Formatter.Annotation, formatting, cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var annotated = root!.GetAnnotatedNodesAndTokens(Formatter.Annotation).Concat(root.GetAnnotatedNodesAndTokens(Simplifier.Annotation)).ToList();
        if (annotated.Count == 0)
        {
            return document;
        }

        return document.WithSyntaxRoot(root.ReplaceSyntax(
            annotated.Where(n => n.IsNode).Select(n => n.AsNode()!),
            (_, node) => node.WithoutAnnotations(Formatter.Annotation).WithoutAnnotations(Simplifier.Annotation),
            annotated.Where(n => n.IsToken).Select(n => n.AsToken()),
            (_, token) => token.WithoutAnnotations(Formatter.Annotation).WithoutAnnotations(Simplifier.Annotation),
            [],
            (_, trivia) => trivia));
    }

    /// <summary>The text's line ending: its first one, else the platform's.</summary>
    internal static string LineEnding(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? Environment.NewLine : index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
    }

    public static ImmutableArray<Diagnostic> Fixable(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Where(d => !Codemods.IsSkipped(d)).ToImmutableArray();
}
