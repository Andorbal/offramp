using System.Text.Json.Nodes;
using System.Xml.Linq;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;

namespace Offramp.Workspace.Model;

/// <summary>
/// Projects in a solution that are not C#, Visual Basic, or F# (C++, installer, database, JavaScript projects).
/// Offramp does not migrate them, so they stay out of the model, its framework counts, and the plan, whether
/// MSBuild evaluated them or not, and <c>scan</c> names each once: <c>OFR0024</c>, or <c>OFR0025</c> for a
/// C++/CLI project, which compiles .NET code and does need migrating
/// (docs/decisions/0049-what-the-workspace-model-records.md).
/// </summary>
public static class OtherProjects
{
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ccproj"] = "Azure cloud service project",
        [".dcproj"] = "Docker Compose project",
        [".deployproj"] = "Azure resource group project",
        [".esproj"] = "JavaScript project",
        [".njsproj"] = "Node.js project",
        [".nuproj"] = "NuGet packaging project",
        [".pyproj"] = "Python project",
        [".sfproj"] = "Service Fabric application project",
        [".shproj"] = "shared project (its files compile in the projects that import it)",
        [".sqlproj"] = "SQL Server Database Project",
        [".vcproj"] = "C++ project",
        [".vcxproj"] = "C++ project",
        [".vdproj"] = "Visual Studio Installer project",
        [".wapproj"] = "Windows Application Packaging project",
        [".wixproj"] = "WiX installer project",
    };

    /// <summary>True for a C#, Visual Basic, or F# project file, the projects the model holds.</summary>
    public static bool IsDotNet(string projectPath) => ProjectModelBuilder.Language(projectPath) != "other";

    /// <summary>
    /// True for a solution entry that is a project file of another kind; a Web Site project (a folder) is not one,
    /// and a SQL Server Database Project MSBuild did not evaluate stays a project that could not load (OFR0101, OFR0114).
    /// </summary>
    public static bool IsOtherListed(string projectPath) =>
        !IsDotNet(projectPath)
        && Path.GetExtension(projectPath).EndsWith("proj", StringComparison.OrdinalIgnoreCase)
        && !projectPath.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>What kind of project the file is, from its extension.</summary>
    public static string Describe(string projectPath)
    {
        var extension = Path.GetExtension(projectPath);
        return Kinds.TryGetValue(extension, out var kind) ? kind : $"{extension.ToLowerInvariant()} project";
    }

    /// <summary>
    /// The <c>CLRSupport</c> value a C++ project file sets anywhere (any configuration), other than <c>false</c>;
    /// null when it sets none, or the file cannot be read.
    /// </summary>
    public static string? ClrSupport(string projectFile)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        return document.Descendants()
            .Where(e => e.Name.LocalName == "CLRSupport")
            .Select(e => e.Value.Trim())
            .Where(v => v.Length > 0 && !v.Equals("false", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Names one project Offramp leaves out: <c>OFR0025</c> for C++/CLI on .NET Framework, else <c>OFR0024</c>.</summary>
    public static Diagnostic? Report(DiagnosticBag diagnostics, string repositoryRoot, string project)
    {
        var kind = Describe(project);
        var clr = ClrSupport(RepoPaths.ToAbsolute(repositoryRoot, project));
        var location = new DiagnosticLocation(Project: project);
        if (clr is not null && !clr.Equals("NetCore", StringComparison.OrdinalIgnoreCase))
        {
            return diagnostics.Report(DiagnosticCatalog.OFR0025,
                $"C++/CLI project (CLRSupport={clr}): it compiles .NET Framework code, which Offramp does not migrate. .NET runs C++/CLI on Windows only, built with CLRSupport=NetCore; port it, or replace it with P/Invoke or a .NET library.",
                location,
                [KeyValuePair.Create<string, JsonNode?>("kind", kind), KeyValuePair.Create<string, JsonNode?>("clrSupport", clr)]);
        }

        var what = clr is null ? kind : $"C++/CLI project for .NET (CLRSupport={clr})";
        return diagnostics.Report(DiagnosticCatalog.OFR0024,
            $"Not a C#, Visual Basic, or F# project ({what}); it is left out of the model, its counts, and the plan.",
            location,
            [KeyValuePair.Create<string, JsonNode?>("kind", kind), KeyValuePair.Create<string, JsonNode?>("clrSupport", clr)]);
    }
}
