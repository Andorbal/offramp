using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Offramp.Analyzers;

/// <summary>
/// Whether a site can take a dependency through its class's constructor: the class already
/// uses constructor injection (one instance constructor with parameters, with a block body),
/// the site is in an instance member (not a field initializer), and nothing in the project
/// creates the class with <c>new</c> (that call would break). Returns the reason when not.
/// </summary>
internal static class Injection
{
    public static string? WhyNot(SemanticModel model, SyntaxNode site, CancellationToken cancellationToken)
    {
        var type = site.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (type is not ClassDeclarationSyntax || model.GetDeclaredSymbol(type, cancellationToken) is not INamedTypeSymbol symbol || symbol.IsStatic)
        {
            return "not in a class that can take constructor parameters.";
        }

        var member = site.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (member is null or FieldDeclarationSyntax || member.Modifiers.Any(SyntaxKind.StaticKeyword)
            || site.Ancestors().Any(a => a is PropertyDeclarationSyntax { Initializer: not null } p && p.Initializer.Span.Contains(site.Span)))
        {
            return "in a static member or an initializer, which cannot use an injected field.";
        }

        var constructors = symbol.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).ToList();
        if (constructors.Count != 1 || constructors[0].Parameters.Length == 0)
        {
            return "the class does not use constructor injection (one constructor with parameters).";
        }

        if (constructors[0].DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not ConstructorDeclarationSyntax { Body: not null, Initializer: null or { ThisOrBaseKeyword.RawKind: (int)SyntaxKind.BaseKeyword } })
        {
            return "the constructor has no block body, or chains to another constructor.";
        }

        return null;
    }

    /// <summary>The constructor that takes the new parameter.</summary>
    public static ConstructorDeclarationSyntax? Constructor(SemanticModel model, TypeDeclarationSyntax type, CancellationToken cancellationToken) =>
        model.GetDeclaredSymbol(type, cancellationToken) is INamedTypeSymbol symbol
            ? symbol.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).Select(c => c.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken)).OfType<ConstructorDeclarationSyntax>().FirstOrDefault()
            : null;
}
