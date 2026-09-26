using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class HttpContextFixer : CodemodFixer
{
    private const string Accessor = "Microsoft.AspNetCore.Http.IHttpContextAccessor";

    public override Codemod Codemod => Codemods.HttpContext;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var root = editor.OriginalRoot;
        var sites = diagnostics.Select(d => root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true)).OfType<MemberAccessExpressionSyntax>().Distinct().ToList();
        foreach (var group in sites.GroupBy(s => s.FirstAncestorOrSelf<TypeDeclarationSyntax>()!))
        {
            var fieldName = "_httpContextAccessor";
            foreach (var site in group)
            {
                // The System.Web adapters convert the ASP.NET Core context, so every use of the result stays valid.
                editor.ReplaceNode(site, (current, _) => Simplify.Names(SyntaxFactory.ParseExpression($"((System.Web.HttpContext){fieldName}.HttpContext)"))
                    .WithTriviaFrom(current));
            }

            fieldName = Injector.Inject(editor, editor.SemanticModel, group.Key, Accessor, "_httpContextAccessor", "httpContextAccessor", cancellationToken);
        }

        return editor.GetChangedDocument();
    }
}
