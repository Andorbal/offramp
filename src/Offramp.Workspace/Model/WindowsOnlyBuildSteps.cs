using System.Globalization;
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
    public static readonly IReadOnlyList<string> Order =
        ["sgen", "com", "entity-deploy", "t4", "fakes", "ssdt", "build-event", "web-targets", "aspnet-compiler", "path-case", "inline-task", "resources"];

    /// <summary>The first and last codes of the Windows-only build step range (OFR0110–OFR0119).</summary>
    public const string FirstCode = "OFR0110";

    public const string LastCode = "OFR0119";

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

        var errorList = (errors ?? []).ToList();
        foreach (var error in errorList)
        {
            if (FromEvaluationError(error.Code, error.Message) is { } step)
            {
                found.TryAdd(step.Id, step);
            }
        }

        foreach (var step in FromBuildErrors(errorList))
        {
            found.TryAdd(step.Id, step);
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

    /// <summary>
    /// Steps only a failed build shows (docs/decisions/0036-legacy-projects-outside-windows.md): paths spelled in
    /// another letter case than on disk, inline tasks, non-string resources, and <c>Exec</c> commands written for
    /// cmd.exe, from a target or a build event.
    /// </summary>
    private static IEnumerable<WindowsOnlyStep> FromBuildErrors(IReadOnlyList<BuildError> errors)
    {
        var mismatches = errors
            .Where(e => e.Code is "MSB4019" or "CS2001" or "MSB3030" or "CS0006")
            .Select(e => QuotedPath(e.Message))
            .OfType<string>()
            .Select(CaseMismatch.Find)
            .OfType<CaseMismatch>()
            .DistinctBy(m => m.Spelled, StringComparer.Ordinal)
            .ToList();
        if (mismatches.Count > 0)
        {
            var more = mismatches.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (and {mismatches.Count - 1} more)") : "";
            yield return new WindowsOnlyStep("path-case", DiagnosticCatalog.OFR0117, mismatches[0].Evidence + more);
        }

        if (errors.FirstOrDefault(e => e.Code == "MSB4801") is { } inline)
        {
            var file = inline.File is null ? "" : " in " + Path.GetFileName(inline.File.Replace('\\', '/'));
            yield return new WindowsOnlyStep("inline-task", DiagnosticCatalog.OFR0118, $"{TaskFactory(inline.Message)}{file} (MSB4801)");
        }

        // The error points into the common targets, not at the .resx file.
        if (errors.FirstOrDefault(e => e.Code is "MSB3822" or "MSB3823") is { } resources)
        {
            yield return new WindowsOnlyStep("resources", DiagnosticCatalog.OFR0119, $"non-string resources in a .resx file ({resources.Code})");
        }

        if (errors.FirstOrDefault(e => e.Code == "MSB3073" && ExecCommand(e.Message) is { } command && IsWindowsCommand(command)) is { } exec)
        {
            yield return new WindowsOnlyStep("build-event", DiagnosticCatalog.OFR0115, $"Exec: {FirstLine(ExecCommand(exec.Message)!)} (MSB3073)");
        }
    }

    private static bool IsWindowsCommand(string command) =>
        WindowsCommandMarkers.Any(m => command.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first quoted path in an error message: <c>"..."</c> for MSBuild, <c>'...'</c> for the compiler.</summary>
    private static string? QuotedPath(string message)
    {
        foreach (var quote in new[] { '"', '\'' })
        {
            var start = message.IndexOf(quote);
            var end = start < 0 ? -1 : message.IndexOf(quote, start + 1);
            if (end > start + 1)
            {
                var path = message[(start + 1)..end];
                if (Path.IsPathRooted(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>The command of MSB3073 (<c>The command "..." exited with code 1.</c>).</summary>
    private static string? ExecCommand(string message)
    {
        const string prefix = "The command \"";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        var end = message.LastIndexOf("\" exited with code", StringComparison.Ordinal);
        return start >= 0 && end > start + prefix.Length ? message[(start + prefix.Length)..end] : null;
    }

    private static string TaskFactory(string message)
    {
        const string prefix = "The task factory \"";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        var end = start < 0 ? -1 : message.IndexOf('"', start + prefix.Length);
        return end > start ? message[(start + prefix.Length)..end] : "an inline task factory";
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
