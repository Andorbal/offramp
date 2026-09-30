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

    /// <summary>
    /// Diagnostic property: the MSBuild property that turns the SDK's attribute off
    /// (<c>GenerateAssemblyVersionAttribute</c>), for an attribute that has to stay where it is.
    /// </summary>
    public const string Switch = "OfframpSwitch";

    public override Codemod Codemod => Codemods.AssemblyInfo;

    protected override void Register(AnalysisContext context) =>
        context.RegisterCompilationStartAction(start =>
        {
            if (Applies(start.Options.AnalyzerConfigOptionsProvider.GlobalOptions))
            {
                start.RegisterSyntaxNodeAction(Analyze, SyntaxKind.Attribute);
            }
        });

    /// <summary>True in an SDK-style project that generates assembly info (the build properties the package makes visible).</summary>
    public static bool Applies(AnalyzerConfigOptions options)
    {
        var sdk = options.TryGetValue("build_property.UsingMicrosoftNETSdk", out var usingSdk) && string.Equals(usingSdk, "true", StringComparison.OrdinalIgnoreCase);
        var generates = !options.TryGetValue("build_property.GenerateAssemblyInfo", out var generate) || !string.Equals(generate, "false", StringComparison.OrdinalIgnoreCase);
        return sdk && generates;
    }

    /// <summary>
    /// The site an assembly attribute is when the SDK generates it, else null. The codemod driver
    /// also asks this of generated code, which analyzers do not look at.
    /// </summary>
    public static Diagnostic? Site(AttributeSyntax attribute, SemanticModel model, CancellationToken cancellationToken)
    {
        if (attribute.Parent is not AttributeListSyntax { Target.Identifier.ValueText: "assembly" }
            || model.GetSymbolInfo(attribute, cancellationToken).Symbol is not IMethodSymbol constructor
            || !Generated.TryGetValue(Symbols.Name(constructor.ContainingType), out var property))
        {
            return null;
        }

        var value = attribute.ArgumentList?.Arguments.FirstOrDefault() is { } argument
            ? model.GetConstantValue(argument.Expression, cancellationToken).Value as string
            : null;
        var properties = ImmutableDictionary<string, string?>.Empty.Add(Property, property).Add(Value, value)
            .Add(Switch, "Generate" + constructor.ContainingType.Name);
        return Diagnostic.Create(Codemods.AssemblyInfo.Descriptor, attribute.GetLocation(), properties,
            $"The SDK generates {constructor.ContainingType.Name}{(property.Length > 0 ? $" from <{property}>" : "")}; this one is a duplicate (CS0579).");
    }

    private void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (Site((AttributeSyntax)context.Node, context.SemanticModel, context.CancellationToken) is { } site)
        {
            context.ReportDiagnostic(site);
        }
    }
}
