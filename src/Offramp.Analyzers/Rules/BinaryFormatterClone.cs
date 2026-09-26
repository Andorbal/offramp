using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM006 (experimental): a method whose whole body deep-clones through a MemoryStream with
/// BinaryFormatter (serialize, rewind, deserialize and cast) becomes a System.Text.Json round
/// trip. BinaryFormatter copied private fields and System.Text.Json copies public members, so
/// the result needs review.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BinaryFormatterCloneAnalyzer : CodemodAnalyzer
{
    public const string OptionsField = "CloneOptions";

    public override Codemod Codemod => Codemods.BinaryFormatterClone;

    protected override void Register(AnalysisContext context) =>
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.MethodDeclaration);

    private void Analyze(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (Clone(context.SemanticModel, method, context.CancellationToken) is { } clone)
        {
            var skip = clone.Value is null ? "the serialized value is not of the returned type." : null;
            context.ReportDiagnostic(Codemods.Site(Codemod, method.Identifier.GetLocation(),
                "This method deep-clones with BinaryFormatter, which modern .NET removed; clone with System.Text.Json (public members only).", skip));
        }
    }

    /// <summary>The clone's type and serialized value, when the method is only a BinaryFormatter round trip.</summary>
    internal static (TypeSyntax Type, ExpressionSyntax? Value)? Clone(SemanticModel model, MethodDeclarationSyntax method, CancellationToken cancellationToken)
    {
        if (method.Body is null || !method.Body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(c => IsFormatter(model.GetTypeInfo(c, cancellationToken).Type)))
        {
            return null;
        }

        ExpressionSyntax? serialized = null;
        CastExpressionSyntax? cast = null;
        foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol target)
            {
                continue;
            }

            if (IsFormatter(target.ContainingType) && target.Name == "Serialize" && invocation.ArgumentList.Arguments.Count == 2)
            {
                serialized = invocation.ArgumentList.Arguments[1].Expression;
            }
            else if (IsFormatter(target.ContainingType) && target.Name == "Deserialize" && invocation.Parent is CastExpressionSyntax c && c.Parent is ReturnStatementSyntax)
            {
                cast = c;
            }
        }

        if (serialized is null || cast is null)
        {
            return null;
        }

        var castType = model.GetTypeInfo(cast.Type, cancellationToken).Type;
        var valueType = model.GetTypeInfo(serialized, cancellationToken).Type;
        return (cast.Type, SymbolEqualityComparer.Default.Equals(castType, valueType) ? serialized : null);
    }

    private static bool IsFormatter(ITypeSymbol? type) => Symbols.IsType(type, "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter");
}
