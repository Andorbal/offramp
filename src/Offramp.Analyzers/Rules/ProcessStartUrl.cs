using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>OFRM008: <c>Process.Start(fileName[, arguments])</c> keeps the shell, as on .NET Framework.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProcessStartUrlAnalyzer : CodemodAnalyzer
{
    public override Codemod Codemod => Codemods.ProcessStartUrl;

    protected override void Register(AnalysisContext context) =>
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);

    private void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (IsShellStart(invocation.TargetMethod))
        {
            context.ReportDiagnostic(Codemods.Site(Codemod, invocation.Syntax.GetLocation(),
                "Process.Start with a file name does not use the shell on modern .NET, so URLs and documents stop opening; pass a ProcessStartInfo with UseShellExecute = true."));
        }
    }

    internal static bool IsShellStart(IMethodSymbol method) =>
        method is { Name: "Start", IsStatic: true } && Symbols.Name(method.ContainingType) == "System.Diagnostics.Process"
        && method.Parameters.Length is 1 or 2 && method.Parameters.All(p => p.Type.SpecialType == SpecialType.System_String);
}
