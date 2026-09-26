using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Offramp.Analyzers.CodeFixes;

/// <summary>
/// Adds a constructor-injected dependency to a class once: a readonly field (or the existing
/// one of that type), a constructor parameter, and the assignment. Returns the field's name.
/// </summary>
internal static class Injector
{
    public static string Inject(DocumentEditor editor, SemanticModel model, TypeDeclarationSyntax type, string typeName, string field, string parameter, CancellationToken cancellationToken)
    {
        var symbol = model.GetDeclaredSymbol(type, cancellationToken)!;
        var existing = symbol.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => !f.IsStatic && Symbols.Name(f.Type) == typeName);
        if (existing is not null)
        {
            return existing.Name;
        }

        var names = symbol.GetMembers().Select(m => m.Name).ToImmutableHashSet();
        var fieldName = names.Contains(field) ? field + "Dependency" : field;
        var constructor = Injection.Constructor(model, type, cancellationToken)!;
        var parameters = constructor.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToImmutableHashSet();
        var parameterName = parameters.Contains(parameter) ? parameter + "Dependency" : parameter;

        var declaration = (FieldDeclarationSyntax)Simplify.Names(SyntaxFactory.ParseMemberDeclaration($"private readonly {typeName} {fieldName};\n")!)
            .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);
        Members.InsertField(editor, type, declaration);
        editor.ReplaceNode(constructor, (current, _) =>
        {
            var ctor = (ConstructorDeclarationSyntax)current;
            var newParameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameterName))
                .WithType(Simplify.Names(SyntaxFactory.ParseTypeName($"{typeName}")).WithTrailingTrivia(SyntaxFactory.Space));
            var assignment = SyntaxFactory.ParseStatement($"{fieldName} = {parameterName};\n").WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation);
            var list = Lists.Append(ctor.ParameterList.Parameters, newParameter);
            return ctor.WithParameterList(ctor.ParameterList.WithParameters(list))
                .WithBody(ctor.Body!.WithStatements(ctor.Body.Statements.Insert(0, assignment)));
        });
        return fieldName;
    }
}
