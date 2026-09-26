using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM014: in a project with the <c>ConfigurationManagerShim</c> class that
/// <c>offramp config convert --shim</c> writes, <c>ConfigurationManager.AppSettings</c> and
/// <c>ConnectionStrings</c> become the shim's. The shim has the indexers, <c>AppSettings.Get</c>,
/// and a connection string's <c>Name</c> and <c>ConnectionString</c>; other uses are reported
/// with the reason.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConfigManagerShimAnalyzer : CodemodAnalyzer
{
    public const string ShimName = "ConfigurationManagerShim";

    /// <summary>Diagnostic property: the shim's fully qualified name.</summary>
    public const string Shim = "OfframpShim";

    public override Codemod Codemod => Codemods.ConfigManagerShim;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var shim = start.Compilation.GetSymbolsWithName(ShimName, SymbolFilter.Type, start.CancellationToken)
                .OfType<INamedTypeSymbol>()
                .Where(t => t.IsStatic && t.GetMembers("AppSettings").Any() && t.GetMembers("ConnectionStrings").Any())
                .OrderBy(t => t.ToDisplayString(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (shim is null)
            {
                return;
            }

            var name = shim.ToDisplayString();
            start.RegisterSyntaxNodeAction(node =>
            {
                var access = (MemberAccessExpressionSyntax)node.Node;
                if (node.SemanticModel.GetSymbolInfo(access, node.CancellationToken).Symbol is not IPropertySymbol { Name: "AppSettings" or "ConnectionStrings" } property
                    || Symbols.Name(property.ContainingType) != "System.Configuration.ConfigurationManager")
                {
                    return;
                }

                var properties = System.Collections.Immutable.ImmutableDictionary<string, string?>.Empty.Add(Shim, name);
                var skip = WhyNot(access, property.Name);
                var message = $"{name} reads IConfiguration with ConfigurationManager's API.";
                node.ReportDiagnostic(Diagnostic.Create(Codemod.Descriptor, access.GetLocation(),
                    skip is null ? properties : properties.Add(Codemods.SkipReason, skip),
                    skip is null ? message : message + " Not rewritten: " + skip));
            }, SyntaxKind.SimpleMemberAccessExpression);
        });

    private static string? WhyNot(MemberAccessExpressionSyntax access, string property)
    {
        var parent = access.Parent;
        if (parent is ElementAccessExpressionSyntax element && element.Expression == access)
        {
            if (property == "AppSettings")
            {
                return element.Parent is AssignmentExpressionSyntax assignment && assignment.Left == element ? "writes the setting, and the shim is read-only." : null;
            }

            return element.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConnectionString" or "Name" } settings && settings.Expression == element
                ? null
                : "uses the connection string settings beyond Name and ConnectionString.";
        }

        if (property == "AppSettings" && parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Get" } get && get.Expression == access && get.Parent is InvocationExpressionSyntax)
        {
            return null;
        }

        var member = parent is MemberAccessExpressionSyntax other && other.Expression == access ? other.Name.Identifier.ValueText : "the collection itself";
        return $"uses {property}.{member}, which the shim does not have.";
    }
}
