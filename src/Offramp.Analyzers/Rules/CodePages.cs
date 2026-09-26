using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM010: an executable that asks for a code page encoding (<c>Encoding.GetEncoding</c> with
/// anything but UTF-8/16/32, ASCII, or Latin-1) and never registers the code pages provider
/// gets <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c> at its entry point.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodePagesAnalyzer : CodemodAnalyzer
{
    private static readonly ImmutableHashSet<string> BuiltIn = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "utf-8", "utf8", "utf-16", "utf-16le", "utf-16be", "unicode", "unicodefffe", "utf-32", "utf-32le", "utf-32be",
        "us-ascii", "ascii", "iso-8859-1", "latin1", "65001", "1200", "1201", "12000", "12001", "20127", "28591");

    public override Codemod Codemod => Codemods.CodePages;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var codePages = new ConcurrentBag<Location>();
            var registered = 0;
            start.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                if (Symbols.IsMethod(invocation.TargetMethod, "System.Text.Encoding", "GetEncoding") && invocation.Arguments.Length >= 1
                    && !(invocation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: var value } && BuiltIn.Contains(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")))
                {
                    codePages.Add(invocation.Syntax.GetLocation());
                }
            }, OperationKind.Invocation);

            // Found by syntax: the provider's type may not resolve on .NET Framework without its package.
            start.RegisterSyntaxNodeAction(node =>
            {
                if (node.Node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "RegisterProvider" } access }
                    && node.SemanticModel.GetSymbolInfo(access.Expression, node.CancellationToken).Symbol is ITypeSymbol encoding && Symbols.Name(encoding) == "System.Text.Encoding")
                {
                    Interlocked.Exchange(ref registered, 1);
                }
            }, SyntaxKind.InvocationExpression);
            start.RegisterCompilationEndAction(end =>
            {
                if (codePages.IsEmpty || registered == 1 || end.Compilation.GetEntryPoint(end.CancellationToken) is not { } entry
                    || entry.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(end.CancellationToken) is not MethodDeclarationSyntax { Body: not null } main)
                {
                    return;
                }

                var first = codePages.OrderBy(l => l.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(l => l.SourceSpan.Start).First().GetLineSpan();
                end.ReportDiagnostic(Codemods.Site(Codemod, main.Identifier.GetLocation(),
                    $"The code asks for code page encodings ({Path.GetFileName(first.Path)}:{first.StartLinePosition.Line + 1}) that modern .NET knows only after CodePagesEncodingProvider is registered."));
            });
        });
}
