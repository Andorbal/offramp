using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers.Rules;

/// <summary>OFRM001: <c>System.Data.SqlClient</c> in usings and qualified names → <c>Microsoft.Data.SqlClient</c>.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SqlClientAnalyzer : CodemodAnalyzer
{
    internal const string Old = "System.Data.SqlClient";

    public override Codemod Codemod => Codemods.SqlClient;

    protected override void Register(AnalysisContext context) =>
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.QualifiedName, SyntaxKind.SimpleMemberAccessExpression);

    private void Analyze(SyntaxNodeAnalysisContext context)
    {
        // The node that spells the namespace itself: System.Data.SqlClient, not a longer name.
        if (context.Node.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax && IsNamespace(context.SemanticModel, (ExpressionSyntax)context.Node.Parent, context.CancellationToken))
        {
            return;
        }

        if (IsNamespace(context.SemanticModel, (ExpressionSyntax)context.Node, context.CancellationToken))
        {
            context.ReportDiagnostic(Codemods.Site(Codemod, context.Node.GetLocation(), "System.Data.SqlClient is deprecated; use Microsoft.Data.SqlClient."));
        }
    }

    internal static bool IsNamespace(SemanticModel model, ExpressionSyntax node, CancellationToken cancellationToken) =>
        node.ToString().Replace(" ", "") == Old && model.GetSymbolInfo(node, cancellationToken).Symbol is INamespaceSymbol ns && ns.ToDisplayString() == Old;
}
