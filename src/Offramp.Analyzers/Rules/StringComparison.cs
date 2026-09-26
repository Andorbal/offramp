using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM009 (opt-in): culture-sensitive string calls become ordinal. <c>StartsWith</c>,
/// <c>EndsWith</c>, <c>IndexOf</c>, <c>LastIndexOf</c> with a string, and
/// <c>string.Compare(a, b[, ignoreCase])</c> get a <c>StringComparison</c>;
/// <c>a.ToLower() == b.ToLower()</c> (and <c>ToUpper</c>, <c>!=</c>, <c>Equals</c>) becomes
/// <c>string.Equals(a, b, StringComparison.OrdinalIgnoreCase)</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StringComparisonAnalyzer : CodemodAnalyzer
{
    internal const string Kind = "OfframpKind";

    public override Codemod Codemod => Codemods.StringComparison;

    protected override void Register(AnalysisContext context)
    {
        context.RegisterOperationAction(AnalyzeCall, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeEquality, OperationKind.Binary);
    }

    private void AnalyzeCall(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.ContainingType.SpecialType != SpecialType.System_String)
        {
            return;
        }

        var culture = method.Name switch
        {
            "StartsWith" or "EndsWith" => !method.IsStatic && method.Parameters.Length == 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_String,
            "IndexOf" or "LastIndexOf" => !method.IsStatic && method.Parameters.Length is 1 or 2 or 3 && method.Parameters[0].Type.SpecialType == SpecialType.System_String
                && method.Parameters.Skip(1).All(p => p.Type.SpecialType == SpecialType.System_Int32),
            "Compare" => method.IsStatic && method.Parameters.Length is 2 or 3 && method.Parameters.Take(2).All(p => p.Type.SpecialType == SpecialType.System_String)
                && (method.Parameters.Length == 2 || method.Parameters[2].Type.SpecialType == SpecialType.System_Boolean),
            _ => false,
        };
        if (!culture)
        {
            return;
        }

        string? skip = null;
        if (method.Name == "Compare" && method.Parameters.Length == 3 && !invocation.Arguments[2].Value.ConstantValue.HasValue)
        {
            skip = "ignoreCase is not a constant.";
        }

        context.ReportDiagnostic(Codemods.Site(Codemod, invocation.Syntax.GetLocation(), $"string.{method.Name} compares with the current culture; compare ordinally.", skip));
    }

    private void AnalyzeEquality(OperationAnalysisContext context)
    {
        var binary = (IBinaryOperation)context.Operation;
        if (binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals
            && binary.LeftOperand.Type?.SpecialType == SpecialType.System_String
            && CaseFolded(binary.LeftOperand) is not null && CaseFolded(binary.RightOperand) is not null)
        {
            context.ReportDiagnostic(Codemods.Site(Codemod, binary.Syntax.GetLocation(), "Comparing ToLower()/ToUpper() results is culture-sensitive; compare with StringComparison.OrdinalIgnoreCase."));
        }
    }

    /// <summary>The string a ToLower()/ToUpper() call folds, or null.</summary>
    internal static IOperation? CaseFolded(IOperation operation) =>
        operation is IInvocationOperation { TargetMethod: { Name: "ToLower" or "ToUpper" or "ToLowerInvariant" or "ToUpperInvariant", Parameters.Length: 0, ContainingType.SpecialType: SpecialType.System_String } } call
            ? call.Instance
            : null;
}
