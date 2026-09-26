using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM004: a <c>WebClient</c> local, in an async method, used only for
/// <c>DownloadString</c>, <c>DownloadData</c>, and <c>UploadString(address, data)</c>, becomes an
/// <c>HttpClient</c> with the awaited equivalents. Other sites are reported with the reason.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WebClientAnalyzer : CodemodAnalyzer
{
    internal static readonly string[] Convertible = ["DownloadString", "DownloadData", "UploadString", "Dispose"];

    public override Codemod Codemod => Codemods.WebClient;

    protected override void Register(AnalysisContext context) =>
        context.RegisterOperationAction(Analyze, OperationKind.ObjectCreation);

    private void Analyze(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (!Symbols.IsType(creation.Type, "System.Net.WebClient"))
        {
            return;
        }

        context.ReportDiagnostic(Codemods.Site(Codemod, creation.Syntax.GetLocation(),
            "WebClient is obsolete on modern .NET; use HttpClient.", WhyNot(creation, context.CancellationToken)));
    }

    private static string? WhyNot(IObjectCreationOperation creation, CancellationToken cancellationToken)
    {
        if (creation.SemanticModel!.Compilation.GetTypeByMetadataName("System.Net.Http.HttpClient") is null)
        {
            return "the project does not reference System.Net.Http, where HttpClient is.";
        }

        if (creation.Arguments.Length > 0 || creation.Initializer is not null)
        {
            return "the WebClient is configured when it is created.";
        }

        if (creation.Syntax.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1 })
        {
            return "the WebClient is not assigned to a local of its own.";
        }

        var model = creation.SemanticModel!;
        if (model.GetDeclaredSymbol(declarator, cancellationToken) is not ILocalSymbol local)
        {
            return "the WebClient is not assigned to a local of its own.";
        }

        var method = creation.Syntax.FirstAncestorOrSelf<SyntaxNode>(n => n is BaseMethodDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
        var isAsync = method switch
        {
            BaseMethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.AsyncKeyword),
            AnonymousFunctionExpressionSyntax f => f.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            LocalFunctionStatementSyntax l => l.Modifiers.Any(SyntaxKind.AsyncKeyword),
            _ => false,
        };
        if (!isAsync)
        {
            return "the method is synchronous; HttpClient's methods are asynchronous.";
        }

        foreach (var use in method!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == local.Name))
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(use, cancellationToken).Symbol, local))
            {
                continue;
            }

            if (use.Parent is not MemberAccessExpressionSyntax access || access.Expression != use || access.Parent is not InvocationExpressionSyntax invocation
                || Array.IndexOf(Convertible, access.Name.Identifier.ValueText) < 0
                || (access.Name.Identifier.ValueText == "UploadString" && invocation.ArgumentList.Arguments.Count != 2))
            {
                var member = use.Parent is MemberAccessExpressionSyntax other && other.Expression == use ? other.Name.Identifier.ValueText : "the WebClient itself";
                return $"uses {member}, which has no direct HttpClient equivalent here.";
            }

            if (invocation.Ancestors().Any(a => a is LockStatementSyntax))
            {
                return "a call is inside a lock, where await is not allowed.";
            }
        }

        return null;
    }
}
