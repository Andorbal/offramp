using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;


namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class CodePagesFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.CodePages;

    protected override async Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var changes = new List<TextChange>();
        foreach (var main in diagnostics.Select(d => root!.FindToken(d.Location.SourceSpan.Start).Parent?.FirstAncestorOrSelf<MethodDeclarationSyntax>()).OfType<MethodDeclarationSyntax>().Distinct())
        {
            var open = main.Body!.OpenBraceToken;
            var line = text.Lines.GetLineFromPosition(open.SpanStart);
            var indent = new string(line.ToString().TakeWhile(char.IsWhiteSpace).ToArray()) + "    ";
            var newLine = text.ToString().Contains("\r\n") ? "\r\n" : "\n";
            changes.Add(new TextChange(new TextSpan(line.End, 0),
                $"{newLine}{indent}// Code page encodings (offramp codemod codepages).{newLine}{indent}System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);"));
        }

        return document.WithText(text.WithChanges(changes));
    }
}
