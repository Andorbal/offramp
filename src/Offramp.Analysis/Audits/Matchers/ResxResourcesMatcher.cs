using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Offramp.Core.Paths;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3212</c> and <c>OFR3213</c>: what the project's <c>.resx</c> files hold besides strings.
/// An entry whose MIME type is a BinaryFormatter or SoapFormatter payload cannot be read on
/// .NET 9 and later (<c>OFR3212</c>); any other non-string entry (a typed value, a byte array,
/// a file reference to anything but text or bytes) makes <c>GenerateResource</c> under the .NET
/// SDK ask for <c>GenerateResourceUsePreserializedResources</c> and <c>System.Resources.Extensions</c>
/// (<c>OFR3213</c>). Every <c>.resx</c> under the project folder is read, outside <c>bin/</c>,
/// <c>obj/</c>, and dot-directories.
/// </summary>
public sealed class ResxResourcesMatcher : IAuditMatcher
{
    private static readonly Dictionary<string, string> FormatterMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/x-microsoft.net.object.binary.base64"] = "BinaryFormatter",
        ["application/x-microsoft.net.object.soap.base64"] = "SoapFormatter",
    };

    private const string ByteArrayMimeType = "application/x-microsoft.net.object.bytearray.base64";

    public string Name => "resx-resources";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        var formatter = context.Rule("OFR3212");
        var nonString = context.Rule("OFR3213");
        if (formatter is null && nonString is null)
        {
            yield break;
        }

        var projectDirectory = Path.GetDirectoryName(RepoPaths.ToAbsolute(context.RepositoryRoot, context.Project.Id))!;
        foreach (var path in ResxFiles(projectDirectory).Order(StringComparer.Ordinal))
        {
            var relative = RepoPaths.ToRepositoryRelative(context.RepositoryRoot, path);
            var document = TryLoad(path);
            if (document?.Root is null)
            {
                continue;
            }

            foreach (var data in document.Root.Elements("data"))
            {
                var name = data.Attribute("name")?.Value ?? "";
                var mimeType = data.Attribute("mimetype")?.Value;
                var type = data.Attribute("type")?.Value;
                var line = ((IXmlLineInfo)data).HasLineInfo() ? ((IXmlLineInfo)data).LineNumber : 1;

                if (mimeType is not null && FormatterMimeTypes.TryGetValue(mimeType, out var serializer))
                {
                    if (formatter is not null)
                    {
                        yield return new RawFinding(formatter, Location.None, name,
                            $"Resource '{name}' in {Path.GetFileName(path)} is a {serializer} payload{TypeNote(type)}; it cannot be read on .NET 9 and later.",
                            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["resource"] = name, ["mimetype"] = mimeType, ["serializer"] = serializer })
                        { FileLocation = (relative, line) };
                    }

                    continue;
                }

                if (nonString is not null && NonStringType(type, mimeType, data.Value) is { } valueType)
                {
                    yield return new RawFinding(nonString, Location.None, name,
                        $"Resource '{name}' in {Path.GetFileName(path)} is a {valueType}, not a string; under the .NET SDK, GenerateResource needs GenerateResourceUsePreserializedResources=true and System.Resources.Extensions.",
                        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["resource"] = name, ["type"] = valueType })
                    { FileLocation = (relative, line) };
                }
            }
        }
    }

    private static string TypeNote(string? type) => type is null ? "" : $" ({TypeName(type)})";

    /// <summary>The non-string type of an entry, or null for a string (or a null placeholder).</summary>
    internal static string? NonStringType(string? type, string? mimeType, string value)
    {
        if (string.Equals(mimeType, ByteArrayMimeType, StringComparison.OrdinalIgnoreCase))
        {
            return type is null ? "System.Byte[]" : TypeName(type);
        }

        if (type is null)
        {
            return null;
        }

        var name = TypeName(type);
        if (name is "System.String" or "System.Resources.ResXNullRef")
        {
            return null;
        }

        if (name == "System.Resources.ResXFileRef")
        {
            // path;Type, Assembly[;encoding]
            var parts = value.Split(';');
            var referenced = parts.Length > 1 ? TypeName(parts[1].Trim()) : "System.Byte[]";
            return referenced is "System.String" or "System.Byte[]" ? null : $"{referenced} (file reference)";
        }

        return name;
    }

    /// <summary>The type name without its assembly part.</summary>
    private static string TypeName(string assemblyQualified)
    {
        var comma = assemblyQualified.IndexOf(',', StringComparison.Ordinal);
        return (comma < 0 ? assemblyQualified : assemblyQualified[..comma]).Trim();
    }

    private static IEnumerable<string> ResxFiles(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            yield break;
        }

        var pending = new Stack<string>([projectDirectory]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*.resx"))
            {
                yield return file;
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(subdirectory);
                if (!name.StartsWith('.') && !name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Push(subdirectory);
                }
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
