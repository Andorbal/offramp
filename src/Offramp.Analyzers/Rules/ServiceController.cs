using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM012: <c>ServiceController</c> used outside a Windows service needs the
/// System.ServiceProcess.ServiceController package on modern .NET. The code stays; the
/// codemod adds the package.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ServiceControllerAnalyzer : CodemodAnalyzer
{
    public override Codemod Codemod => Codemods.ServiceController;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("System.ServiceProcess.ServiceController") is null)
            {
                return;
            }

            start.RegisterOperationAction(operation =>
            {
                var type = operation.Operation switch
                {
                    IObjectCreationOperation creation => creation.Type,
                    IInvocationOperation invocation => invocation.TargetMethod.ContainingType,
                    _ => null,
                };
                if (Symbols.IsType(type, "System.ServiceProcess.ServiceController") && !InService(operation.ContainingSymbol))
                {
                    operation.ReportDiagnostic(Codemods.Site(Codemod, operation.Operation.Syntax.GetLocation(),
                        "ServiceController is in the System.ServiceProcess.ServiceController package on modern .NET."));
                }
            }, OperationKind.ObjectCreation, OperationKind.Invocation);
        });

    /// <summary>Code inside a ServiceBase subclass is the service itself (see offramp service).</summary>
    private static bool InService(ISymbol symbol)
    {
        for (var type = symbol.ContainingType?.BaseType; type is not null; type = type.BaseType)
        {
            if (Symbols.Name(type) == "System.ServiceProcess.ServiceBase")
            {
                return true;
            }
        }

        return false;
    }
}
