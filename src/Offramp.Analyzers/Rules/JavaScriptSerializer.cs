using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM005: <c>JavaScriptSerializer.Serialize(obj)</c>, <c>Deserialize&lt;T&gt;(json)</c>, and
/// <c>Deserialize(json, type)</c> become <c>System.Text.Json.JsonSerializer</c> calls with
/// options that keep names as declared, read case-insensitively, and include fields.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JavaScriptSerializerAnalyzer : CodemodAnalyzer
{
    public const string OptionsField = "JavaScriptSerializerOptions";

    public override Codemod Codemod => Codemods.JavaScriptSerializer;

    protected override void Register(AnalysisContext context) =>
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);

    private void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (Symbols.Name(method.ContainingType) != "System.Web.Script.Serialization.JavaScriptSerializer" || method.IsStatic)
        {
            return;
        }

        var supported = method.Name switch
        {
            "Serialize" => method.Parameters.Length == 1,
            "Deserialize" => method.Parameters.Length == 1 && method.IsGenericMethod || method.Parameters.Length == 2 && Symbols.IsType(method.Parameters[1].Type, "System.Type"),
            _ => false,
        };
        string? skip = null;
        if (!supported)
        {
            skip = $"JavaScriptSerializer.{method.Name} has no System.Text.Json equivalent with the same result.";
        }
        else if (invocation.Syntax.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax))
        {
            skip = "not in a class or struct that can hold the options.";
        }

        context.ReportDiagnostic(Codemods.Site(Codemod, invocation.Syntax.GetLocation(),
            "JavaScriptSerializer is System.Web only; use System.Text.Json.JsonSerializer. Dates are written as ISO 8601, not \\/Date(...)\\/.", skip));
    }
}
