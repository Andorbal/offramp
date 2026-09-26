using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>OFRM011: <c>TimeZoneInfo.FindSystemTimeZoneById</c> → <c>TZConvert.GetTimeZoneInfo</c>, which accepts Windows and IANA IDs everywhere.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TimeZoneIdsAnalyzer : CodemodAnalyzer
{
    public override Codemod Codemod => Codemods.TimeZoneIds;

    protected override void Register(AnalysisContext context) =>
        context.RegisterOperationAction(Analyze, OperationKind.Invocation);

    private void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!Symbols.IsMethod(invocation.TargetMethod, "System.TimeZoneInfo", "FindSystemTimeZoneById") || invocation.Arguments.Length != 1)
        {
            return;
        }

        var id = invocation.Arguments[0].Value.ConstantValue;
        var skip = id.HasValue && id.Value is string text && text.IndexOf('/') >= 0 ? $"\"{text}\" is already an IANA ID." : null;
        context.ReportDiagnostic(Codemods.Site(Codemod, invocation.Syntax.GetLocation(),
            "FindSystemTimeZoneById needs the ID in the host's format (Windows IDs fail on Linux without ICU); TZConvert.GetTimeZoneInfo accepts both.", skip));
    }
}
