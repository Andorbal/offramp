using Offramp.Core.Model;

namespace Offramp.Workspace.Model;

/// <summary>The facts kind detection looks at, gathered across a project's target frameworks.</summary>
public sealed record ProjectFacts
{
    public string? Sdk { get; init; }

    public string? OutputType { get; init; }

    public bool IsTestProject { get; init; }

    public bool UseWpf { get; init; }

    public bool UseWindowsForms { get; init; }

    public IReadOnlySet<string> PackageIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The packages a packages.config lists.</summary>
    public IReadOnlySet<string> PackagesConfigIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> AssemblyReferences { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string? ProjectTypeGuids { get; init; }

    public bool HasWebConfig { get; init; }
}

/// <summary>
/// Project kind detection, in the order of docs/spec/02-workspace-model.md; the
/// first rule that matches wins and its evidence is recorded.
/// </summary>
public static class ProjectKindDetector
{
    public static readonly IReadOnlyList<string> TestPackages =
    [
        "xunit", "xunit.core", "xunit.v3", "xunit.v3.core", "xunit.runner.visualstudio",
        "NUnit", "NUnit3TestAdapter", "NUnit4TestAdapter",
        "MSTest", "MSTest.TestFramework", "MSTest.TestAdapter", "Microsoft.NET.Test.Sdk",
        "TUnit", "TUnit.Core", "TUnit.Engine",
    ];

    /// <summary>The assemblies of those frameworks, for projects that reference them by HintPath instead of a package.</summary>
    public static readonly IReadOnlyList<string> TestAssemblies =
    [
        "Microsoft.VisualStudio.QualityTools.UnitTestFramework", "Microsoft.VisualStudio.TestPlatform.TestFramework",
        "nunit.framework", "TUnit.Core", "xunit", "xunit.core", "xunit.v3.core", "MbUnit.Framework",
    ];

    private const string TestProjectGuid = "3AC096D0-A1C2-E12C-1390-A8335801FDAB";
    private const string WpfProjectGuid = "60DC8134-EBA5-43B8-BCC9-BB4BC16C2548";

    private static readonly string[] WebProjectGuids =
    [
        "349C5851-65DF-11DA-9384-00065B846F21", // Web Application
        "E24C65DC-7377-472B-9ABA-BC803B73C61A", // Web Site
        "603C0E0B-DB56-11DC-BE95-000D561079B0", // ASP.NET MVC 1
        "F85E285D-A4E0-4152-9332-AB1D724D3325", // ASP.NET MVC 2
        "E53F8FEA-EAE0-44A6-8774-FFD645390401", // ASP.NET MVC 3
        "E3E379DF-F4C6-4180-9B81-6769533ABE47", // ASP.NET MVC 4
    ];

    /// <summary>
    /// The kind and its evidence. Without <paramref name="frameworks"/>, a test framework the project
    /// references is no evidence: what the project is when an application uses it (ADR 0074).
    /// </summary>
    public static (ProjectKind Kind, string Evidence) Detect(ProjectFacts facts, bool frameworks = true)
    {
        var outputType = facts.OutputType ?? "Library";
        var isExe = outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase);
        var isWinExe = outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
        var isExecutable = isExe || isWinExe;
        var isLibrary = outputType.Equals("Library", StringComparison.OrdinalIgnoreCase);

        // The legacy test-project guid is an explicit declaration. IsTestProject is often set by
        // Microsoft.NET.Test.Sdk, so application evidence can outweigh that property (ADR 0077).
        if (HasGuid(facts, TestProjectGuid))
        {
            return (ProjectKind.Test, $"ProjectTypeGuids {{{TestProjectGuid}}}");
        }

        if (facts.IsTestProject)
        {
            var withoutProperty = Detect(facts with { IsTestProject = false }, frameworks: false);
            if (withoutProperty.Kind is ProjectKind.Web or ProjectKind.Service or ProjectKind.Winforms or ProjectKind.Wpf)
            {
                return (withoutProperty.Kind, $"{withoutProperty.Evidence} (IsTestProject=true ignored)");
            }

            return (ProjectKind.Test, "IsTestProject=true");
        }

        // web
        if (facts.Sdk == "Microsoft.NET.Sdk.Web")
        {
            return (ProjectKind.Web, "Sdk=Microsoft.NET.Sdk.Web");
        }

        var webGuid = WebProjectGuids.FirstOrDefault(g => HasGuid(facts, g));
        if (webGuid is not null)
        {
            return (ProjectKind.Web, $"ProjectTypeGuids {{{webGuid}}}");
        }

        if (facts.AssemblyReferences.Contains("System.Web") && isLibrary && facts.HasWebConfig)
        {
            return (ProjectKind.Web, "Reference System.Web + OutputType=Library + web.config");
        }

        // test, from a test framework the project references: after the web rules, since a web application
        // that references a test framework (tests kept in the application) is still the application, and so is
        // an executable with a desktop or service application's evidence (ADR 0038, 0074)
        if (frameworks && TestFramework(facts) is { } framework)
        {
            return (isExe || isWinExe) && Detect(facts, frameworks: false) is { Kind: ProjectKind.Winforms or ProjectKind.Wpf or ProjectKind.Service } application
                ? application
                : (ProjectKind.Test, framework);
        }

        // winforms
        if (facts.UseWindowsForms)
        {
            return (ProjectKind.Winforms, "UseWindowsForms=true");
        }

        if (facts.AssemblyReferences.Contains("System.Windows.Forms") && isWinExe)
        {
            return (ProjectKind.Winforms, "Reference System.Windows.Forms + OutputType=WinExe");
        }

        // wpf
        if (facts.UseWpf)
        {
            return (ProjectKind.Wpf, "UseWPF=true");
        }

        if (facts.Sdk == "Microsoft.NET.Sdk.WindowsDesktop" && facts.AssemblyReferences.Contains("PresentationFramework"))
        {
            return (ProjectKind.Wpf, "Sdk=Microsoft.NET.Sdk.WindowsDesktop + PresentationFramework");
        }

        if (HasGuid(facts, WpfProjectGuid))
        {
            return (ProjectKind.Wpf, $"ProjectTypeGuids {{{WpfProjectGuid}}}");
        }

        // service
        if (facts.AssemblyReferences.Contains("System.ServiceProcess") && isExecutable)
        {
            return (ProjectKind.Service, $"Reference System.ServiceProcess + OutputType={outputType}");
        }

        foreach (var package in new[] { "Topshelf", "Microsoft.Extensions.Hosting.WindowsServices" })
        {
            if (facts.PackageIds.Contains(package))
            {
                return (ProjectKind.Service, $"PackageReference {package}");
            }

            if (facts.PackagesConfigIds.Contains(package) && isExecutable)
            {
                return (ProjectKind.Service, $"packages.config {package} + OutputType={outputType}");
            }
        }

        if (facts.Sdk == "Microsoft.NET.Sdk.Worker")
        {
            return (ProjectKind.Service, "Sdk=Microsoft.NET.Sdk.Worker");
        }

        // console, library
        if (isExe)
        {
            return (ProjectKind.Console, "OutputType=Exe");
        }

        if (isWinExe)
        {
            return (ProjectKind.Console, "OutputType=WinExe");
        }

        if (isLibrary)
        {
            return (ProjectKind.Library, "OutputType=Library");
        }

        return (ProjectKind.Unknown, $"OutputType={outputType}");
    }

    /// <summary>
    /// The test framework evidence: a package (PackageReference or packages.config, any output type: NUnitLite
    /// test projects are executables), or a checked-in framework assembly in a library; null when there is none.
    /// </summary>
    public static string? TestFramework(ProjectFacts facts)
    {
        if (TestPackages.FirstOrDefault(facts.PackageIds.Contains) is { } package)
        {
            return $"PackageReference {package}";
        }

        if (TestPackages.FirstOrDefault(facts.PackagesConfigIds.Contains) is { } installed)
        {
            return $"packages.config {installed}";
        }

        var isLibrary = (facts.OutputType ?? "Library").Equals("Library", StringComparison.OrdinalIgnoreCase);
        return TestAssemblies.FirstOrDefault(facts.AssemblyReferences.Contains) is { } assembly && isLibrary
            ? $"Reference {assembly} + OutputType=Library"
            : null;
    }

    /// <summary>True when the evidence for a test project is only a test framework it references, which production code may carry too.</summary>
    public static bool IsFrameworkEvidence(string evidence) =>
        evidence.StartsWith("PackageReference ", StringComparison.Ordinal)
        || evidence.StartsWith("packages.config ", StringComparison.Ordinal)
        || evidence.StartsWith("Reference ", StringComparison.Ordinal);

    /// <summary>The SDK name from the evaluated UsingMicrosoftNETSdk* properties, or null for legacy projects.</summary>
    public static string? SdkName(Func<string, bool> isTrue)
    {
        if (isTrue("UsingMicrosoftNETSdkWeb")) return "Microsoft.NET.Sdk.Web";
        if (isTrue("UsingMicrosoftNETSdkWorker")) return "Microsoft.NET.Sdk.Worker";
        if (isTrue("UsingMicrosoftNETSdkBlazorWebAssembly")) return "Microsoft.NET.Sdk.BlazorWebAssembly";
        if (isTrue("UsingMicrosoftNETSdkRazor")) return "Microsoft.NET.Sdk.Razor";
        if (isTrue("UsingMicrosoftNETSdkWindowsDesktop")) return "Microsoft.NET.Sdk.WindowsDesktop";
        return isTrue("UsingMicrosoftNETSdk") ? "Microsoft.NET.Sdk" : null;
    }

    private static bool HasGuid(ProjectFacts facts, string guid) =>
        facts.ProjectTypeGuids?.Contains(guid, StringComparison.OrdinalIgnoreCase) == true;
}
