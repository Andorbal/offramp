using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers.Rules;

/// <summary>
/// OFRM013: in an SDK-style project that generates assembly info (the default), the assembly
/// attributes the SDK always writes (title, company, product, configuration, and the three
/// versions) are duplicates in AssemblyInfo.cs. Their values go to the project file as
/// properties (the diagnostic carries them) and the attributes go.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AssemblyInfoAnalyzer : CodemodAnalyzer
{
    /// <summary>Attribute → the MSBuild property that sets the SDK's generated value.</summary>
    internal static readonly ImmutableDictionary<string, string> Generated = new Dictionary<string, string>
    {
        ["System.Reflection.AssemblyTitleAttribute"] = "AssemblyTitle",
        ["System.Reflection.AssemblyCompanyAttribute"] = "Company",
        ["System.Reflection.AssemblyProductAttribute"] = "Product",
        ["System.Reflection.AssemblyConfigurationAttribute"] = "",
        ["System.Reflection.AssemblyVersionAttribute"] = "AssemblyVersion",
        ["System.Reflection.AssemblyFileVersionAttribute"] = "FileVersion",
        ["System.Reflection.AssemblyInformationalVersionAttribute"] = "InformationalVersion",
    }.ToImmutableDictionary();

    /// <summary>Diagnostic properties: the MSBuild property and its value.</summary>
    public const string Property = "OfframpProperty";

    public const string Value = "OfframpValue";

    public override Codemod Codemod => Codemods.AssemblyInfo;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            var options = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            var sdk = options.TryGetValue("build_property.UsingMicrosoftNETSdk", out var usingSdk) && string.Equals(usingSdk, "true", StringComparison.OrdinalIgnoreCase);
            var generates = !options.TryGetValue("build_property.GenerateAssemblyInfo", out var generate) || !string.Equals(generate, "false", StringComparison.OrdinalIgnoreCase);
            if (sdk && generates)
            {
                start.RegisterSyntaxNodeAction(Analyze, SyntaxKind.Attribute);
            }
        });

    private void Analyze(SyntaxNodeAnalysisContext context)
    {
        var attribute = (AttributeSyntax)context.Node;
        if (attribute.Parent is not AttributeListSyntax { Target.Identifier.ValueText: "assembly" }
            || context.SemanticModel.GetSymbolInfo(attribute, context.CancellationToken).Symbol is not IMethodSymbol constructor
            || !Generated.TryGetValue(Symbols.Name(constructor.ContainingType), out var property))
        {
            return;
        }

        var value = attribute.ArgumentList?.Arguments.FirstOrDefault() is { } argument
            ? context.SemanticModel.GetConstantValue(argument.Expression, context.CancellationToken).Value as string
            : null;
        var properties = ImmutableDictionary<string, string?>.Empty.Add(Property, property).Add(Value, value);
        context.ReportDiagnostic(Diagnostic.Create(Codemod.Descriptor, attribute.GetLocation(), properties,
            $"The SDK generates {constructor.ContainingType.Name}{(property.Length > 0 ? $" from <{property}>" : "")}; this one is a duplicate (CS0579)."));
    }
}
