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
        "MSTest", "MSTest.TestFramework", "MSTest.TestAdapter",
        "TUnit", "TUnit.Core", "TUnit.Engine",
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

    public static (ProjectKind Kind, string Evidence) Detect(ProjectFacts facts)
    {
        var outputType = facts.OutputType ?? "Library";
        var isExe = outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase);
        var isWinExe = outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
        var isLibrary = outputType.Equals("Library", StringComparison.OrdinalIgnoreCase);

        // test
        if (facts.IsTestProject)
        {
            return (ProjectKind.Test, "IsTestProject=true");
        }

        var testPackage = TestPackages.FirstOrDefault(facts.PackageIds.Contains);
        if (testPackage is not null)
        {
            return (ProjectKind.Test, $"PackageReference {testPackage}");
        }

        if (HasGuid(facts, TestProjectGuid))
        {
            return (ProjectKind.Test, $"ProjectTypeGuids {{{TestProjectGuid}}}");
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
        if (facts.AssemblyReferences.Contains("System.ServiceProcess") && isExe)
        {
            return (ProjectKind.Service, "Reference System.ServiceProcess + OutputType=Exe");
        }

        foreach (var package in new[] { "Topshelf", "Microsoft.Extensions.Hosting.WindowsServices" })
        {
            if (facts.PackageIds.Contains(package))
            {
                return (ProjectKind.Service, $"PackageReference {package}");
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

        if (isLibrary)
        {
            return (ProjectKind.Library, "OutputType=Library");
        }

        return (ProjectKind.Unknown, $"OutputType={outputType}");
    }

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
