using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM007 (experimental): <c>thread.Abort()</c> on a field whose thread runs a method of the
/// same type with a top-level <c>while</c> loop becomes cancellation: the loop checks a
/// <c>CancellationTokenSource</c>, <c>Thread.Sleep</c> in it waits on the token, and Abort
/// becomes Cancel. The loop now ends at its next check instead of at any instruction.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThreadAbortAnalyzer : CodemodAnalyzer
{
    public override Codemod Codemod => Codemods.ThreadAbort;

    protected override void Register(AnalysisContext context) =>
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);

    private void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!Symbols.IsMethod(invocation.TargetMethod, "System.Threading.Thread", "Abort") || invocation.Arguments.Length != 0)
        {
            return;
        }

        var skip = Plan(invocation.SemanticModel!, (InvocationExpressionSyntax)invocation.Syntax, context.CancellationToken) is null
            ? "the thread is not a field of this type whose body is a method with a top-level while loop in the same file."
            : null;
        context.ReportDiagnostic(Codemods.Site(Codemod, invocation.Syntax.GetLocation(), "Thread.Abort throws PlatformNotSupportedException on modern .NET; stop the thread with cancellation.", skip));
    }

    /// <summary>The thread field and the method whose loop it runs, when the pattern holds.</summary>
    internal static (IFieldSymbol Field, MethodDeclarationSyntax Body)? Plan(SemanticModel model, InvocationExpressionSyntax abort, CancellationToken cancellationToken)
    {
        if (abort.Expression is not MemberAccessExpressionSyntax access || model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is not IFieldSymbol field
            || abort.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } type)
        {
            return null;
        }

        foreach (var creation in type.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            if (!Symbols.IsType(model.GetTypeInfo(creation, cancellationToken).Type, "System.Threading.Thread") || creation.ArgumentList?.Arguments.Count is not 1
                || creation.Parent is not AssignmentExpressionSyntax assignment || !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, field))
            {
                continue;
            }

            var start = creation.ArgumentList.Arguments[0].Expression;
            if (start is ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 1 } wrapped)
            {
                start = wrapped.ArgumentList.Arguments[0].Expression;
            }

            if (model.GetSymbolInfo(start, cancellationToken).Symbol is IMethodSymbol method
                && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is MethodDeclarationSyntax { Body: not null } body
                && body.SyntaxTree == abort.SyntaxTree
                && body.Body.Statements.OfType<WhileStatementSyntax>().Any())
            {
                return (field, body);
            }
        }

        return null;
    }
}
