using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offramp.Analyzers;

/// <summary>
/// Where each class name is created with <c>new</c> anywhere in the compilation, from one
/// syntactic pass (computed once per compilation). Name-based on purpose: it can only make a
/// codemod skip a site (another type with the same name), never rewrite one it should not, and
/// it keeps the codemods' diagnostics local, so the IDE and fix-all can apply them.
/// </summary>
internal sealed class Creations(Compilation compilation, CancellationToken cancellationToken)
{
    private readonly Lazy<Dictionary<string, Location>> _byName = new(() =>
    {
        var found = new Dictionary<string, Location>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees.OrderBy(t => t.FilePath, StringComparer.Ordinal))
        {
            foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
            {
                var type = node switch
                {
                    ObjectCreationExpressionSyntax creation => creation.Type,
                    ImplicitObjectCreationExpressionSyntax { Parent: EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } } } => declaration.Type,
                    _ => null,
                };
                var name = type switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    QualifiedNameSyntax { Right: IdentifierNameSyntax right } => right.Identifier.ValueText,
                    _ => null,
                };
                if (name is not null && !found.ContainsKey(name))
                {
                    found[name] = node.GetLocation();
                }
            }
        }

        return found;
    });

    /// <summary>Why a class cannot take a new constructor parameter, or null.</summary>
    public string? WhyNot(INamedTypeSymbol? type)
    {
        if (type is null || !_byName.Value.TryGetValue(type.Name, out var at))
        {
            return null;
        }

        var line = at.GetLineSpan();
        return $"the class is created with new at {Path.GetFileName(line.Path)}:{line.StartLinePosition.Line + 1}, which a new constructor parameter would break.";
    }
}
