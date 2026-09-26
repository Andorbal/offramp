using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM003: <c>HttpContext.Current</c> in a class that uses constructor injection becomes the
/// injected <c>IHttpContextAccessor.HttpContext</c>, converted to <c>System.Web.HttpContext</c>
/// so every use stays as it was. Only where the project has ASP.NET Core and the System.Web
/// adapters (the conversion); elsewhere sites are reported with the reason.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HttpContextAnalyzer : CodemodAnalyzer
{
    public override Codemod Codemod => Codemods.HttpContext;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var accessor = start.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.IHttpContextAccessor");
            var systemWeb = start.Compilation.GetTypeByMetadataName("System.Web.HttpContext");
            var core = start.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Http.HttpContext");
            var adapters = systemWeb is not null && core is not null
                && systemWeb.GetMembers().OfType<IMethodSymbol>().Any(m => m.MethodKind == MethodKind.Conversion && m.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type.OriginalDefinition, core));
            var creations = new Creations(start.Compilation, start.CancellationToken);
            start.RegisterSyntaxNodeAction(node =>
            {
                var access = (MemberAccessExpressionSyntax)node.Node;
                if (access.Name.Identifier.ValueText != "Current"
                    || node.SemanticModel.GetSymbolInfo(access, node.CancellationToken).Symbol is not IPropertySymbol { IsStatic: true } property
                    || Symbols.Name(property.ContainingType) != "System.Web.HttpContext")
                {
                    return;
                }

                var skip = accessor is null
                    ? "the project does not reference ASP.NET Core (IHttpContextAccessor)."
                    : !adapters
                        ? "System.Web.HttpContext here is not the System.Web adapters' type, which converts from the ASP.NET Core context."
                        : access.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access
                            ? "assigns HttpContext.Current."
                            : Injection.WhyNot(node.SemanticModel, access, node.CancellationToken)
                                ?? creations.WhyNot(node.SemanticModel.GetEnclosingSymbol(access.SpanStart, node.CancellationToken)?.ContainingType);
                node.ReportDiagnostic(Codemods.Site(Codemod, access.GetLocation(), "HttpContext.Current is ambient state; take IHttpContextAccessor through the constructor.", skip));
            }, SyntaxKind.SimpleMemberAccessExpression);
        });
}
