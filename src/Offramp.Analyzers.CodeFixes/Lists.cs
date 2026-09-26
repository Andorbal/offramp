using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Offramp.Analyzers.CodeFixes;

/// <summary>Separated-list edits whose separators carry their own spacing.</summary>
internal static class Lists
{
    /// <summary>
    /// Appends an item after a <c>", "</c> separator. The space is explicit: formatters differ
    /// on whether they touch the token in front of an annotated node.
    /// </summary>
    public static SeparatedSyntaxList<T> Append<T>(SeparatedSyntaxList<T> list, T item)
        where T : SyntaxNode
    {
        if (list.Count == 0)
        {
            return SyntaxFactory.SingletonSeparatedList(item);
        }

        var separators = list.GetSeparators().Take(list.Count - 1).Append(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space));
        return SyntaxFactory.SeparatedList(list.Append(item), separators);
    }
}
