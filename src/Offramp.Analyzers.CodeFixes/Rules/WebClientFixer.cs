using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Offramp.Analyzers.CodeFixes.Rules;

[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class WebClientFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.WebClient;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var model = editor.SemanticModel;
        var root = editor.OriginalRoot;
        foreach (var creation in diagnostics.Select(d => root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true)).OfType<ObjectCreationExpressionSyntax>().Distinct())
        {
            var declarator = (VariableDeclaratorSyntax)creation.Parent!.Parent!;
            var declaration = (VariableDeclarationSyntax)declarator.Parent!;
            var local = (ILocalSymbol)model.GetDeclaredSymbol(declarator, cancellationToken)!;
            var method = creation.FirstAncestorOrSelf<SyntaxNode>(n => n is BaseMethodDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)!;
            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is MemberAccessExpressionSyntax access && access.Expression is IdentifierNameSyntax receiver
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(receiver, cancellationToken).Symbol, local) && Replacement(invocation, access) is { } replacement)
                {
                    editor.ReplaceNode(invocation, replacement.WithTriviaFrom(invocation));
                }
            }

            editor.ReplaceNode(creation, Simplify.Names(SyntaxFactory.ParseExpression("new System.Net.Http.HttpClient()")).WithTriviaFrom(creation));
            if (!declaration.Type.IsVar)
            {
                editor.ReplaceNode(declaration.Type, Simplify.Names(SyntaxFactory.ParseTypeName("System.Net.Http.HttpClient")).WithTriviaFrom(declaration.Type));
            }
        }

        return editor.GetChangedDocument();
    }

    private static ExpressionSyntax? Replacement(InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax access)
    {
        var client = access.Expression.ToString();
        var arguments = invocation.ArgumentList.Arguments;
        var text = access.Name.Identifier.ValueText switch
        {
            "DownloadString" => $"await {client}.GetStringAsync({arguments[0]})",
            "DownloadData" => $"await {client}.GetByteArrayAsync({arguments[0]})",
            "UploadString" => $"await (await {client}.PostAsync({arguments[0]}, new System.Net.Http.StringContent({arguments[1]}))).Content.ReadAsStringAsync()",
            _ => null,
        };
        if (text is null)
        {
            return null;
        }

        // An awaited call used as an operand needs parentheses.
        if (invocation.Parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax or ConditionalAccessExpressionSyntax or InvocationExpressionSyntax)
        {
            text = "(" + text + ")";
        }

        return Simplify.Names(SyntaxFactory.ParseExpression(text));
    }
}
