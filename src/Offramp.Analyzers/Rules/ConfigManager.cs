using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM002: <c>ConfigurationManager.AppSettings["k"]</c> (and
/// <c>ConnectionStrings["k"].ConnectionString</c>) in a class that uses constructor injection
/// become reads from an injected <c>IConfiguration</c>. Other sites are reported with the reason.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConfigManagerAnalyzer : CodemodAnalyzer
{
    public override Codemod Codemod => Codemods.ConfigManager;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var creations = new Creations(start.Compilation, start.CancellationToken);
            start.RegisterSyntaxNodeAction(node =>
            {
                var access = (ElementAccessExpressionSyntax)node.Node;
                if (Site(node.SemanticModel, access, node.CancellationToken) is not { } site)
                {
                    return;
                }

                var skip = SkipReason(node.SemanticModel, access, site, node.CancellationToken)
                    ?? creations.WhyNot(node.SemanticModel.GetEnclosingSymbol(site.SpanStart, node.CancellationToken)?.ContainingType);
                node.ReportDiagnostic(Codemods.Site(Codemod, site.GetLocation(), "ConfigurationManager reads app.config, which modern .NET does not use; read IConfiguration instead.", skip));
            }, SyntaxKind.ElementAccessExpression);
        });

    /// <summary>The node to rewrite: <c>AppSettings["k"]</c>, or <c>ConnectionStrings["k"].ConnectionString</c>.</summary>
    internal static ExpressionSyntax? Site(SemanticModel model, ElementAccessExpressionSyntax access, CancellationToken cancellationToken)
    {
        if (model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is not IPropertySymbol { Name: "AppSettings" or "ConnectionStrings" } property
            || Symbols.Name(property.ContainingType) != "System.Configuration.ConfigurationManager")
        {
            return null;
        }

        return property.Name == "ConnectionStrings" && access.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConnectionString" } connection && connection.Expression == access
            ? connection
            : access;
    }

    private static string? SkipReason(SemanticModel model, ElementAccessExpressionSyntax access, ExpressionSyntax site, CancellationToken cancellationToken)
    {
        if (access.ArgumentList.Arguments.Count != 1)
        {
            return "not a single key lookup.";
        }

        if (model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is IPropertySymbol { Name: "ConnectionStrings" } && site == access)
        {
            return "uses the ConnectionStringSettings object, not only its ConnectionString.";
        }

        if (access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == site)
        {
            return "writes the setting.";
        }

        return Injection.WhyNot(model, site, cancellationToken);
    }
}
