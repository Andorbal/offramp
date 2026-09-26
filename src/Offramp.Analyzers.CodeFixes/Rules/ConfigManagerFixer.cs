using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class ConfigManagerFixer : CodemodFixer
{
    private const string Configuration = "Microsoft.Extensions.Configuration.IConfiguration";

    public override Codemod Codemod => Codemods.ConfigManager;

    protected override async Task<Document> FixSitesAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var model = editor.SemanticModel;
        var root = editor.OriginalRoot;
        var sites = diagnostics.Select(d => root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true)).OfType<ExpressionSyntax>().Distinct().ToList();
        foreach (var group in sites.GroupBy(s => s.FirstAncestorOrSelf<TypeDeclarationSyntax>()!))
        {
            var field = "_configuration";
            var fieldName = field;
            foreach (var site in group)
            {
                editor.ReplaceNode(site, (current, _) => Rewrite((ExpressionSyntax)current, fieldName).WithTriviaFrom(current));
            }

            fieldName = Injector.Inject(editor, model, group.Key, Configuration, field, "configuration", cancellationToken);
        }

        return editor.GetChangedDocument();
    }

    /// <summary><c>ConfigurationManager.AppSettings["k"]</c> → <c>_configuration["k"]</c>; a connection string → <c>GetConnectionString</c>.</summary>
    private static ExpressionSyntax Rewrite(ExpressionSyntax site, string field)
    {
        if (site is MemberAccessExpressionSyntax { Expression: ElementAccessExpressionSyntax connection })
        {
            return Simplify.Names(SyntaxFactory.ParseExpression($"Microsoft.Extensions.Configuration.ConfigurationExtensions.GetConnectionString({field}, {connection.ArgumentList.Arguments[0]})"))
                ;
        }

        var access = (ElementAccessExpressionSyntax)site;
        return access.WithExpression(SyntaxFactory.IdentifierName(field));
    }
}
