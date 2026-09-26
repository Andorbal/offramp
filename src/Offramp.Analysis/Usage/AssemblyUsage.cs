using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offramp.Analysis.Usage;

/// <summary>How often source code uses symbols defined in given assemblies.</summary>
public static class AssemblyUsage
{
    /// <summary>
    /// Assembly name → number of names in source that bind to a type or member defined in
    /// it (C#; null for other languages, whose projects get no counts).
    /// </summary>
    public static IReadOnlyDictionary<string, int>? Count(Compilation compilation, IReadOnlyCollection<string> assemblyNames, CancellationToken cancellationToken = default)
    {
        if (compilation is not CSharpCompilation)
        {
            return null;
        }

        var counts = assemblyNames.ToDictionary(n => n, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot(cancellationToken).DescendantNodes().OfType<SimpleNameSyntax>())
            {
                // Only the rightmost name of a qualified name binds to the used symbol.
                if (name.Parent is QualifiedNameSyntax qualified && qualified.Left == name)
                {
                    continue;
                }

                var info = model.GetSymbolInfo(name, cancellationToken);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var assembly = symbol switch
                {
                    null or INamespaceSymbol or ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ILabelSymbol => null,
                    _ => symbol.ContainingAssembly?.Name,
                };
                if (assembly is not null && counts.TryGetValue(assembly, out var count))
                {
                    counts[assembly] = count + 1;
                }
            }
        }

        return counts;
    }
}
