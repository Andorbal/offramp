using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Scaffolding.Newtonsoft;

/// <summary>
/// Writes an expression the scanner judged reproducible (constants, enum members, <c>typeof</c>,
/// public statics, and creations of public types from those) back out as C# 6, fully qualified,
/// so a type probe can create the same settings a site uses. Null for anything else.
/// </summary>
internal static class OperationText
{
    public static string? Of(IOperation operation) => operation switch
    {
        ILiteralOperation literal => CSharp6.Literal(literal.ConstantValue.Value),
        IDefaultValueOperation { Type: { } type } => $"default({CSharp6.Type(type)})",
        ITypeOfOperation typeOf => $"typeof({CSharp6.Type(typeOf.TypeOperand)})",
        IConversionOperation { IsImplicit: true } conversion => Of(conversion.Operand),
        IConversionOperation { Type: { } type } conversion => Of(conversion.Operand) is { } inner ? $"(({CSharp6.Type(type)}){inner})" : null,
        IParenthesizedOperation parenthesized => Of(parenthesized.Operand) is { } inner ? $"({inner})" : null,
        IUnaryOperation unary => Of(unary.Operand) is { } inner ? Unary(unary.OperatorKind) + inner : null,
        IBinaryOperation binary => Of(binary.LeftOperand) is { } left && Of(binary.RightOperand) is { } right && Binary(binary.OperatorKind) is { } op
            ? $"{left} {op} {right}"
            : null,
        IFieldReferenceOperation { Field: { IsStatic: true } field } => field.HasConstantValue && field.ContainingType.TypeKind != TypeKind.Enum && field.ContainingType.SpecialType != SpecialType.None
            ? CSharp6.Literal(field.ConstantValue)
            : $"{CSharp6.Type(field.ContainingType)}.{field.Name}",
        IPropertyReferenceOperation { Property: { IsStatic: true } property, Arguments.Length: 0 } => $"{CSharp6.Type(property.ContainingType)}.{property.Name}",
        IObjectCreationOperation { Type: { } type } creation => Creation(type, creation),
        IArrayCreationOperation { Type: IArrayTypeSymbol array } creation => creation.Initializer is null
            ? (creation.DimensionSizes.Length == 1 && Of(creation.DimensionSizes[0]) is { } size ? $"new {CSharp6.Type(array.ElementType)}[{size}]" : null)
            : Items(creation.Initializer.ElementValues) is { } items ? $"new {CSharp6.Type(array.ElementType)}[] {{ {items} }}" : null,
        _ => null,
    };

    private static string? Creation(ITypeSymbol type, IObjectCreationOperation creation)
    {
        if (Items(creation.Arguments.Select(a => a.Value)) is not { } arguments)
        {
            return null;
        }

        var text = $"new {CSharp6.Type(type)}({arguments})";
        if (creation.Initializer is null || creation.Initializer.Initializers.Length == 0)
        {
            return text;
        }

        return Initializer(creation.Initializer) is { } initializer ? $"{text} {initializer}" : null;
    }

    private static string? Initializer(IObjectOrCollectionInitializerOperation initializer)
    {
        var parts = new List<string>();
        foreach (var part in initializer.Initializers)
        {
            var text = part switch
            {
                ISimpleAssignmentOperation { Target: IMemberReferenceOperation member } assignment => Of(assignment.Value) is { } value ? $"{member.Member.Name} = {value}" : null,
                IMemberInitializerOperation { InitializedMember: IMemberReferenceOperation member } nested => Initializer(nested.Initializer) is { } inner ? $"{member.Member.Name} = {inner}" : null,
                IInvocationOperation { Arguments.Length: 1 } add => Of(add.Arguments[0].Value),
                IInvocationOperation add => Items(add.Arguments.Select(a => a.Value)) is { } items ? $"{{ {items} }}" : null,
                _ => null,
            };
            if (text is null)
            {
                return null;
            }

            parts.Add(text);
        }

        return $"{{ {string.Join(", ", parts)} }}";
    }

    private static string? Items(IEnumerable<IOperation> operations)
    {
        var items = operations.Select(Of).ToList();
        return items.Any(i => i is null) ? null : string.Join(", ", items);
    }

    private static string Unary(UnaryOperatorKind kind) => kind switch
    {
        UnaryOperatorKind.Minus => "-",
        UnaryOperatorKind.BitwiseNegation => "~",
        UnaryOperatorKind.Not => "!",
        _ => "+",
    };

    private static string? Binary(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Or => "|",
        BinaryOperatorKind.And => "&",
        BinaryOperatorKind.Add => "+",
        BinaryOperatorKind.Subtract => "-",
        BinaryOperatorKind.Multiply => "*",
        _ => null,
    };
}
