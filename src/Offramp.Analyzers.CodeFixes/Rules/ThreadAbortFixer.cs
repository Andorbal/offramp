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
public sealed class ThreadAbortFixer : CodemodFixer
{
    public override Codemod Codemod => Codemods.ThreadAbort;

    public override async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken).ConfigureAwait(false);
        var model = editor.SemanticModel;
        var root = editor.OriginalRoot;
        var done = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var abort in diagnostics.Select(d => root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<InvocationExpressionSyntax>()).OfType<InvocationExpressionSyntax>().Distinct())
        {
            if (ThreadAbortAnalyzer.Plan(model, abort, cancellationToken) is not { } plan)
            {
                continue;
            }

            var cancellation = "_" + plan.Field.Name.TrimStart('_') + "Cancellation";
            editor.ReplaceNode(abort, SyntaxFactory.ParseExpression($"{cancellation}.Cancel()").WithTriviaFrom(abort));
            if (!done.Add(plan.Field))
            {
                continue;
            }

            foreach (var loop in plan.Body.Body!.Statements.OfType<WhileStatementSyntax>())
            {
                var condition = loop.Condition is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.TrueLiteralExpression }
                    ? $"!{cancellation}.IsCancellationRequested"
                    : $"({loop.Condition}) && !{cancellation}.IsCancellationRequested";
                editor.ReplaceNode(loop.Condition, SyntaxFactory.ParseExpression(condition).WithTriviaFrom(loop.Condition));
                foreach (var sleep in loop.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(sleep, cancellationToken).Symbol is IMethodSymbol { Name: "Sleep", IsStatic: true } method
                        && Symbols.Name(method.ContainingType) == "System.Threading.Thread" && sleep.ArgumentList.Arguments.Count == 1)
                    {
                        // Wakes when cancelled, as Abort interrupted the sleep.
                        editor.ReplaceNode(sleep, SyntaxFactory.ParseExpression($"{cancellation}.Token.WaitHandle.WaitOne({sleep.ArgumentList.Arguments[0]})").WithTriviaFrom(sleep));
                    }
                }
            }

            var type = abort.FirstAncestorOrSelf<TypeDeclarationSyntax>()!;
            var field = Simplify.Names(SyntaxFactory.ParseMemberDeclaration(
                $"private readonly System.Threading.CancellationTokenSource {cancellation} = new System.Threading.CancellationTokenSource();\n")!)
                .WithAdditionalAnnotations(Formatter.Annotation);
            Members.InsertField(editor, type, field);
        }

        return editor.GetChangedDocument();
    }
}
