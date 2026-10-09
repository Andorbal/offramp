using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;

namespace Offramp.Analysis.Serialization;

/// <summary>
/// The types a serializer argument carries: its static type, or, when that is <c>object</c>, an
/// interface, or a type parameter and the argument is a parameter of the containing method, the
/// types passed for it at that method's call sites in the compilation (three calls deep).
/// Shared by <c>audit serialization</c> and <c>newtonsoft scan</c>.
/// </summary>
internal static class PayloadTypes
{
    public const int MaxDepth = 3;

    public static IEnumerable<ITypeSymbol> Of(Compilation compilation, SemanticModel model, ExpressionSyntax argument, int depth = 0)
    {
        var type = model.GetTypeInfo(argument).Type;
        if (type is null)
        {
            yield break;
        }

        var parameter = model.GetSymbolInfo(Unwrap(argument)).Symbol as IParameterSymbol;
        if (!IsOpaque(type) || parameter is null || depth >= MaxDepth || parameter.ContainingSymbol is not IMethodSymbol owner)
        {
            if (type is not ITypeParameterSymbol)
            {
                yield return type;
            }

            yield break;
        }

        // Follow the parameter to the call sites of its method in this compilation.
        foreach (var tree in compilation.SyntaxTrees)
        {
            var siteModel = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (AuditEngine.Bound(siteModel, invocation) is not IMethodSymbol target
                    || !SymbolEqualityComparer.Default.Equals((target.ReducedFrom ?? target).OriginalDefinition, owner.OriginalDefinition))
                {
                    continue;
                }

                var index = parameter.Ordinal - (target.ReducedFrom is null ? 0 : 1);
                if (index >= 0 && index < invocation.ArgumentList.Arguments.Count)
                {
                    foreach (var passed in Of(compilation, siteModel, invocation.ArgumentList.Arguments[index].Expression, depth + 1))
                    {
                        yield return passed;
                    }
                }
                else if (index == -1 && invocation.Expression is MemberAccessExpressionSyntax access)
                {
                    foreach (var passed in Of(compilation, siteModel, access.Expression, depth + 1))
                    {
                        yield return passed;
                    }
                }
            }
        }
    }

    /// <summary>A static type that says nothing about the payload.</summary>
    public static bool IsOpaque(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Object || type is ITypeParameterSymbol || type.TypeKind == TypeKind.Interface;

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesized => Unwrap(parenthesized.Expression),
        CastExpressionSyntax cast => Unwrap(cast.Expression),
        _ => expression,
    };
}
