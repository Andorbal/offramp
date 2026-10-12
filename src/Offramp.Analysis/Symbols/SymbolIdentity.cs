using Microsoft.CodeAnalysis;

namespace Offramp.Analysis.Symbols;

/// <summary>What a symbol also answers to. Shared by <c>audit dead-code</c> and the call graph.</summary>
internal static class SymbolIdentity
{
    /// <summary>The interface members a method implements (explicitly or implicitly) and the methods it overrides.</summary>
    public static IEnumerable<IMethodSymbol> Aliases(IMethodSymbol method)
    {
        foreach (var implemented in method.ExplicitInterfaceImplementations)
        {
            yield return implemented;
        }

        if (method.ContainingType is { } type)
        {
            foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers(method.Name)).OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method))
                {
                    yield return member;
                }
            }
        }

        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
        {
            yield return overridden;
        }
    }
}
