using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Model;

/// <summary>A build step that only runs on Windows, and the evidence for it.</summary>
public sealed record WindowsOnlyStep(string Id, DiagnosticDescriptor Descriptor, string Evidence);

/// <summary>
/// Detects build steps that need Windows (docs/compiling-on-macos.md) from
/// evaluated properties, items, and imports, so detection works from logs of
/// builds that failed or never ran those steps.
/// </summary>
public static class WindowsOnlyBuildSteps
{
    /// <summary>Step ids in the order they are reported.</summary>
    public static readonly IReadOnlyList<string> Order = ["sgen", "com", "entity-deploy", "t4", "fakes", "ssdt", "build-event", "web-targets", "aspnet-compiler"];

    /// <summary>The first and last codes of the Windows-only build step range (OFR0110–OFR0116).</summary>
    public const string FirstCode = "OFR0110";

    public const string LastCode = "OFR0116";

    /// <summary>The file Visual Studio installs under <c>$(VSToolsPath)</c> for ASP.NET (System.Web) projects.</summary>
    public const string WebApplicationTargets = "Microsoft.WebApplication.targets";

    /// <summary>The package the compile-only block takes the web targets from outside Windows.</summary>
    public const string WebTargetsPackage = "MSBuild.Microsoft.VisualStudio.Web.targets";

    private static readonly string[] WindowsCommandMarkers =
        [".exe", ".bat", ".cmd", "xcopy", "robocopy", "copy ", "del ", "powershell", "cmd ", "%"];

    public static bool IsStepCode(string code) =>
        string.CompareOrdinal(code, FirstCode) >= 0 && string.CompareOrdinal(code, LastCode) <= 0;

    /// <summary>True when a loaded project has a step, or a project that could not load was reported with one.</summary>
    public static bool AnyIn(WorkspaceModel model) =>
        model.Projects.Any(p => p.WindowsOnlyBuildSteps.Count > 0) || model.Diagnostics.Any(d => IsStepCode(d.Code));

    /// <summary>
    /// The web targets step for a project that failed to evaluate, when the error is the missing
    /// Visual Studio web targets import (MSB4019); null otherwise.
    /// </summary>
    public static WindowsOnlyStep? FromEvaluationError(string code, string message) =>
        code == "MSB4019" && message.Contains(WebApplicationTargets, StringComparison.OrdinalIgnoreCase)
            ? new WindowsOnlyStep("web-targets", DiagnosticCatalog.OFR0116, "imports " + WebApplicationTargets + " from $(VSToolsPath), which only Visual Studio installs")
            : null;

    /// <summary>
    /// The steps in a project's evaluations, and in its <paramref name="errors"/>: an evaluation that failed
    /// on a missing import is recorded without its items and imports, so the error is the only evidence.
    /// </summary>
    public static IReadOnlyList<WindowsOnlyStep> Detect(string projectFile, IEnumerable<EvaluatedProject> evaluations, IEnumerable<BuildError>? errors = null)
    {
        var found = new Dictionary<string, WindowsOnlyStep>(StringComparer.Ordinal);
        void Add(string id, DiagnosticDescriptor descriptor, string evidence) => found.TryAdd(id, new WindowsOnlyStep(id, descriptor, evidence));

        foreach (var error in errors ?? [])
        {
            if (FromEvaluationError(error.Code, error.Message) is { } step)
            {
                found.TryAdd(step.Id, step);
            }
        }

        foreach (var e in evaluations)
        {
            var sgen = e.Property("GenerateSerializationAssemblies");
            if (string.Equals(sgen, "On", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(sgen, "Auto", StringComparison.OrdinalIgnoreCase) && e.TargetsExecuted.Contains("GenerateSerializationAssemblies")))
            {
                Add("sgen", DiagnosticCatalog.OFR0110, $"GenerateSerializationAssemblies={sgen}");
            }

            var com = e.ItemsOf("COMReference").Concat(e.ItemsOf("COMFileReference")).FirstOrDefault();
            if (com is not null)
            {
                Add("com", DiagnosticCatalog.OFR0111, $"COMReference {com.Include}");
            }

            var edmx = e.ItemsOf("EntityDeploy").FirstOrDefault();
            if (edmx is not null)
            {
                Add("entity-deploy", DiagnosticCatalog.OFR0112, $"EntityDeploy {edmx.Include}");
            }

            if (e.IsTrue("TransformOnBuild"))
            {
                Add("t4", DiagnosticCatalog.OFR0113, "TransformOnBuild=true");
            }
            else if (e.Imports.FirstOrDefault(i => i.EndsWith("TextTemplating.targets", StringComparison.OrdinalIgnoreCase)) is { } t4Import)
            {
                Add("t4", DiagnosticCatalog.OFR0113, "imports " + Path.GetFileName(t4Import.Replace('\\', '/')));
            }

            var fakes = e.ItemsOf("Fakes").FirstOrDefault();
            if (fakes is not null)
            {
                Add("fakes", DiagnosticCatalog.OFR0113, $"Fakes {fakes.Include}");
            }

            if (projectFile.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase))
            {
                Add("ssdt", DiagnosticCatalog.OFR0114, "SQL Server Database Project (.sqlproj)");
            }
            else if (e.Imports.FirstOrDefault(i => i.Contains("SqlTasks.targets", StringComparison.OrdinalIgnoreCase)) is { } ssdtImport)
            {
                Add("ssdt", DiagnosticCatalog.OFR0114, "imports " + Path.GetFileName(ssdtImport.Replace('\\', '/')));
            }

            foreach (var name in new[] { "PreBuildEvent", "PostBuildEvent" })
            {
                var command = e.Property(name);
                if (command is not null && WindowsCommandMarkers.Any(m => command.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    Add("build-event", DiagnosticCatalog.OFR0115, $"{name}: {FirstLine(command)}");
                }
            }

            if (e.Imports.FirstOrDefault(IsVisualStudioWebTargets) is not null)
            {
                Add("web-targets", DiagnosticCatalog.OFR0116, "imports " + WebApplicationTargets + " from Visual Studio's $(VSToolsPath)");
            }

            if (e.IsTrue("MvcBuildViews"))
            {
                Add("aspnet-compiler", DiagnosticCatalog.OFR0116, "MvcBuildViews=true (AspNetCompiler)");
            }
        }

        return [.. Order.Where(found.ContainsKey).Select(id => found[id])];
    }

    /// <summary>The web targets, imported from anywhere but the package the compile-only block adds.</summary>
    private static bool IsVisualStudioWebTargets(string import)
    {
        var path = import.Replace('\\', '/');
        return path.EndsWith("/WebApplications/" + WebApplicationTargets, StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/" + WebTargetsPackage + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
