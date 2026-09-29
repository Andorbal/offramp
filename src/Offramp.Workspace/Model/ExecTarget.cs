using System.Xml;
using System.Xml.Linq;

namespace Offramp.Workspace.Model;

/// <summary>The target an <c>Exec</c> task ran in, and the files it declares it writes.</summary>
/// <param name="Name">The target's name.</param>
/// <param name="Outputs">Its <c>Outputs</c>, as absolute paths where the properties they use are known, else as written.</param>
public sealed record ExecTarget(string Name, IReadOnlyList<string> Outputs)
{
    /// <summary>
    /// The target around the <c>Exec</c> at <paramref name="line"/> of <paramref name="file"/> (a project or targets file
    /// in this checkout), or null when it is not there. Properties in <c>Outputs</c> are expanded from the file's own
    /// unconditional property definitions and the project's directory; nothing is evaluated.
    /// </summary>
    public static ExecTarget? Find(string file, int line, string projectFile)
    {
        var document = ProjectFileChecks.Load(file);
        var exec = document?.Descendants().FirstOrDefault(e => ((IXmlLineInfo)e).LineNumber == line && e.Name.LocalName == "Exec");
        var target = exec?.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Target");
        if (target is null)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectDirectory"] = projectDirectory,
            ["ProjectDir"] = projectDirectory + "/",
            ["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectFile),
            ["MSBuildThisFileDirectory"] = Path.GetDirectoryName(Path.GetFullPath(file))! + "/",
        };
        foreach (var property in document!.Root!.Elements().Where(e => e.Name.LocalName == "PropertyGroup" && e.Attribute("Condition") is null).Elements())
        {
            if (property.Attribute("Condition") is null)
            {
                properties[property.Name.LocalName] = property.Value.Trim();
            }
        }

        var outputs = (target.Attribute("Outputs")?.Value ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => Expand(o, properties, depth: 0))
            .Select(o => o.Contains("$(", StringComparison.Ordinal) || o.Contains("@(", StringComparison.Ordinal) || o.Contains("%(", StringComparison.Ordinal)
                ? o
                : Path.GetFullPath(Path.Combine(projectDirectory, o.Replace('\\', '/'))))
            .ToList();
        return new ExecTarget(target.Attribute("Name")?.Value ?? "", outputs);
    }

    /// <summary>The value with every known property substituted, recursively; unknown ones stay as written.</summary>
    private static string Expand(string value, Dictionary<string, string> properties, int depth)
    {
        if (depth > 8)
        {
            return value;
        }

        var result = new System.Text.StringBuilder();
        var at = 0;
        while (at < value.Length)
        {
            var start = value.IndexOf("$(", at, StringComparison.Ordinal);
            var end = start < 0 ? -1 : value.IndexOf(')', start + 2);
            if (end < 0)
            {
                result.Append(value, at, value.Length - at);
                break;
            }

            var name = value[(start + 2)..end];
            result.Append(value, at, start - at)
                .Append(properties.TryGetValue(name, out var replacement) ? Expand(replacement, properties, depth + 1) : value[start..(end + 1)]);
            at = end + 1;
        }

        return result.ToString();
    }
}
