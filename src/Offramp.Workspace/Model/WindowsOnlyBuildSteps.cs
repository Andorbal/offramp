using Offramp.Core.Diagnostics;
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
    public static readonly IReadOnlyList<string> Order = ["sgen", "com", "entity-deploy", "t4", "fakes", "ssdt", "build-event"];

    private static readonly string[] WindowsCommandMarkers =
        [".exe", ".bat", ".cmd", "xcopy", "robocopy", "copy ", "del ", "powershell", "cmd ", "%"];

    public static IReadOnlyList<WindowsOnlyStep> Detect(string projectFile, IEnumerable<EvaluatedProject> evaluations)
    {
        var found = new Dictionary<string, WindowsOnlyStep>(StringComparer.Ordinal);
        void Add(string id, DiagnosticDescriptor descriptor, string evidence) => found.TryAdd(id, new WindowsOnlyStep(id, descriptor, evidence));

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
        }

        return [.. Order.Where(found.ContainsKey).Select(id => found[id])];
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
