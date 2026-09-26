using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace Offramp.Analyzers.CodeFixes;

internal static class Members
{
    /// <summary>
    /// Inserts a field as the type's first member. A blank line separates it from the next
    /// member unless that is a field too; the line break goes on the next member, which the
    /// formatter does not touch.
    /// </summary>
    public static void InsertField(DocumentEditor editor, TypeDeclarationSyntax type, MemberDeclarationSyntax field)
    {
        editor.InsertMembers(type, 0, [field]);
        if (type.Members.FirstOrDefault() is { } next and not FieldDeclarationSyntax)
        {
            var newLine = editor.OriginalRoot.ToFullString().Contains("\r\n") ? "\r\n" : "\n";
            editor.ReplaceNode(next, (current, _) => current.WithLeadingTrivia(current.GetLeadingTrivia().Insert(0, SyntaxFactory.EndOfLine(newLine))));
        }
    }
}
