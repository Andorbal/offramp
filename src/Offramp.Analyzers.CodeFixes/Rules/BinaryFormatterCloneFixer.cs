using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Offramp.Analyzers.Rules;

namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class BinaryFormatterCloneFixer : CodemodFixer
{
    private const string Options = BinaryFormatterCloneAnalyzer.OptionsField;

    public override Codemod Codemod => Codemods.BinaryFormatterClone;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var root = editor.OriginalRoot;
        var methods = diagnostics.Select(d => root.FindToken(d.Location.SourceSpan.Start).Parent?.FirstAncestorOrSelf<MethodDeclarationSyntax>()).OfType<MethodDeclarationSyntax>().Distinct().ToList();
        foreach (var method in methods)
        {
            if (BinaryFormatterCloneAnalyzer.Clone(editor.SemanticModel, method, cancellationToken) is not { Value: { } value } clone)
            {
                continue;
            }

            var indent = method.Body!.OpenBraceToken.LeadingTrivia.ToFullString();
            var body = Simplify.Names(SyntaxFactory.ParseStatement(
                $"return System.Text.Json.JsonSerializer.Deserialize<{clone.Type}>(System.Text.Json.JsonSerializer.Serialize<{clone.Type}>({value}, {Options}), {Options});"))
                .WithAdditionalAnnotations(Formatter.Annotation);
            editor.ReplaceNode(method.Body, method.Body.WithStatements(SyntaxFactory.SingletonList(body)).WithAdditionalAnnotations(Formatter.Annotation));
        }

        foreach (var type in methods.Select(m => m.FirstAncestorOrSelf<TypeDeclarationSyntax>()!).Distinct())
        {
            if (!type.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == Options)))
            {
                var field = Simplify.Names(SyntaxFactory.ParseMemberDeclaration(
                    $"/// <summary>Deep clones copy public properties and fields (offramp codemod binaryformatter-clone).</summary>\n" +
                    $"private static readonly System.Text.Json.JsonSerializerOptions {Options} = new System.Text.Json.JsonSerializerOptions {{ IncludeFields = true }};\n")!)
                    .WithAdditionalAnnotations(Formatter.Annotation);
                Members.InsertField(editor, type, field);
            }
        }

        return editor.GetChangedDocument();
    }
}
