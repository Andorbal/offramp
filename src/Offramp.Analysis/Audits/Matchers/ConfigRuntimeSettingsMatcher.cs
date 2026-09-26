using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Offramp.Core.Paths;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3116</c>: garbage collector and threading settings under <c>&lt;runtime&gt;</c> in the
/// project's <c>app.config</c> or <c>web.config</c>. Modern .NET reads them from
/// <c>runtimeconfig.json</c> (MSBuild properties); the configuration file is ignored.
/// </summary>
public sealed class ConfigRuntimeSettingsMatcher : IAuditMatcher
{
    /// <summary>app.config element → runtimeconfig.json setting (empty: the default on modern .NET).</summary>
    private static readonly Dictionary<string, string> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gcServer"] = "System.GC.Server (<ServerGarbageCollection>)",
        ["gcConcurrent"] = "System.GC.Concurrent (<ConcurrentGarbageCollection>)",
        ["GCCpuGroup"] = "System.GC.CpuGroup",
        ["GCHeapAffinitizeMask"] = "System.GC.HeapAffinitizeMask",
        ["GCHeapCount"] = "System.GC.HeapCount",
        ["GCHeapHardLimit"] = "System.GC.HeapHardLimit",
        ["GCNoAffinitize"] = "System.GC.NoAffinitize",
        ["GCLargePages"] = "System.GC.LargePages",
        ["gcAllowVeryLargeObjects"] = "",
        ["Thread_UseAllCpuGroups"] = "System.Threading.Thread.UseAllCpuGroups",
        ["gcTrimCommitOnLowMemory"] = "System.GC.RetainVM (closest)",
    };

    public string Name => "config-runtime-settings";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3116") is not { } rule)
        {
            yield break;
        }

        var projectDirectory = Path.GetDirectoryName(RepoPaths.ToAbsolute(context.RepositoryRoot, context.Project.Id))!;
        var files = Directory.Exists(projectDirectory)
            ? Directory.EnumerateFiles(projectDirectory).Where(f => Path.GetFileName(f) is var n
                && (n.Equals("app.config", StringComparison.OrdinalIgnoreCase) || n.Equals("web.config", StringComparison.OrdinalIgnoreCase)))
            : [];
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            var document = TryLoad(path);
            var runtime = document?.Root?.Element("runtime");
            if (runtime is null)
            {
                continue;
            }

            var relative = RepoPaths.ToRepositoryRelative(context.RepositoryRoot, path);
            foreach (var element in runtime.Elements().Where(e => Settings.ContainsKey(e.Name.LocalName)))
            {
                var setting = Settings[element.Name.LocalName];
                var line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 1;
                var value = element.Attribute("enabled")?.Value ?? element.Attribute("value")?.Value ?? "";
                var message = setting.Length == 0
                    ? $"<{element.Name.LocalName}> in {name} is the default on modern .NET; the element is ignored."
                    : $"<{element.Name.LocalName}> in {name} is ignored on modern .NET; set {setting} instead.";
                var details = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["element"] = element.Name.LocalName, ["value"] = value };
                if (setting.Length > 0)
                {
                    details["runtimeconfig"] = setting;
                }

                yield return new RawFinding(rule, Location.None, element.Name.LocalName, message, details) { FileLocation = (relative, line) };
            }
        }
    }

    private static XDocument? TryLoad(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
