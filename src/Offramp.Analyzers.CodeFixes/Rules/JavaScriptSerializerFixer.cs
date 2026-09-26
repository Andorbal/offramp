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
public sealed class JavaScriptSerializerFixer : CodemodFixer
{
    private const string Options = JavaScriptSerializerAnalyzer.OptionsField;

    public override Codemod Codemod => Codemods.JavaScriptSerializer;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var model = editor.SemanticModel;
        var root = editor.OriginalRoot;
        var invocations = diagnostics.Select(d => root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<InvocationExpressionSyntax>())
            .OfType<InvocationExpressionSyntax>().Distinct().ToList();
        var rewritten = new HashSet<SyntaxNode>();
        foreach (var invocation in invocations)
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method || invocation.Expression is not MemberAccessExpressionSyntax access)
            {
                continue;
            }

            var arguments = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => a.Expression.ToString()).Append(Options));
            var call = method.Name == "Serialize"
                ? $"System.Text.Json.JsonSerializer.Serialize<object>({arguments})"
                : method.IsGenericMethod
                    ? $"System.Text.Json.JsonSerializer.Deserialize<{method.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>({arguments})"
                    : $"System.Text.Json.JsonSerializer.Deserialize({arguments})";
            editor.ReplaceNode(invocation, Simplify.Names(SyntaxFactory.ParseExpression(call)).WithTriviaFrom(invocation));
            rewritten.Add(access.Expression);
        }

        RemoveUnusedSerializers(editor, model, invocations, rewritten, cancellationToken);
        foreach (var type in invocations.Select(i => i.FirstAncestorOrSelf<TypeDeclarationSyntax>()!).Distinct())
        {
            if (!type.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == Options)))
            {
                var field = Simplify.Names(SyntaxFactory.ParseMemberDeclaration(
                    $"/// <summary>JavaScriptSerializer's behavior: names as declared, case-insensitive reads, fields included (offramp codemod javascript-serializer).</summary>\n" +
                    $"private static readonly System.Text.Json.JsonSerializerOptions {Options} = new System.Text.Json.JsonSerializerOptions {{ PropertyNameCaseInsensitive = true, IncludeFields = true }};\n")!)
                    .WithAdditionalAnnotations(Formatter.Annotation);
                Members.InsertField(editor, type, field);
            }
        }

        return editor.GetChangedDocument();
    }

    /// <summary><c>var serializer = new JavaScriptSerializer();</c> goes when every use of it was rewritten.</summary>
    private static void RemoveUnusedSerializers(DocumentEditor editor, SemanticModel model, List<InvocationExpressionSyntax> invocations, HashSet<SyntaxNode> rewritten, CancellationToken cancellationToken)
    {
        foreach (var receiver in rewritten.OfType<IdentifierNameSyntax>().ToList())
        {
            if (model.GetSymbolInfo(receiver, cancellationToken).Symbol is not ILocalSymbol local
                || local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax { Initializer.Value: ObjectCreationExpressionSyntax { Initializer: null } } declarator
                || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1, Parent: LocalDeclarationStatementSyntax statement }
                || statement.Parent is not BlockSyntax block)
            {
                continue;
            }

            var uses = block.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n, cancellationToken).Symbol, local));
            if (uses.All(rewritten.Contains) && !editor.OriginalRoot.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Where(s => s == statement).Skip(1).Any())
            {
                editor.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia);
            }
        }
    }
}
