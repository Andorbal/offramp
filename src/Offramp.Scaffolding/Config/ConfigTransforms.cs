using System.Text.Json.Nodes;
using System.Xml.Linq;
using Offramp.Core.Paths;

namespace Offramp.Scaffolding.Config;

/// <summary>
/// XDT transforms (App.Release.config, Web.Release.config) as appsettings.{Environment}.json
/// overrides: <c>SetAttributes</c>, <c>Replace</c>, and <c>Insert</c> of appSettings and
/// connection string entries (located by key or name), and <c>SetAttributes</c> or
/// <c>Replace</c> on a converted custom section. Everything else is listed (OFR4404).
/// </summary>
public static class ConfigTransforms
{
    private static readonly XNamespace Xdt = "http://schemas.microsoft.com/XML-Document-Transform";

    /// <summary>The transform files next to a configuration file: App.Release.config for App.config.</summary>
    public static IEnumerable<string> Find(string root, string source)
    {
        var directory = Path.GetDirectoryName(source.Replace('\\', '/')) ?? "";
        var stem = Path.GetFileNameWithoutExtension(source);
        return Directory.EnumerateFiles(RepoPaths.ToAbsolute(root, directory), stem + ".*.config")
            .Select(f => RepoPaths.Normalize(Path.Combine(directory, Path.GetFileName(f))))
            .Order(StringComparer.Ordinal);
    }

    public static (ConvertedTransform Result, JsonObject Overrides) Convert(string root, string transform, IReadOnlyDictionary<string, (string Key, SectionSchema Schema)> sections)
    {
        var name = Path.GetFileNameWithoutExtension(transform);
        var environment = name[(name.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var overrides = new JsonObject();
        var notConverted = new List<string>();
        var count = 0;
        var document = XDocument.Load(RepoPaths.ToAbsolute(root, transform));
        foreach (var element in document.Root!.Descendants().Where(e => e.Attribute(Xdt + "Transform") is not null))
        {
            var verb = element.Attribute(Xdt + "Transform")!.Value;
            var locator = element.Attribute(Xdt + "Locator")?.Value;
            var parent = element.Parent?.Name.LocalName;
            var path = string.Join('/', element.AncestorsAndSelf().Reverse().Skip(1).Select(e => e.Name.LocalName));
            var overriding = verb.StartsWith("SetAttributes", StringComparison.Ordinal) || verb == "Replace";
            if (parent == "appSettings" && element.Name.LocalName == "add" && ((overriding && locator == "Match(key)") || verb == "Insert")
                && element.Attribute("key")?.Value is { Length: > 0 } key && element.Attribute("value") is { } value)
            {
                overrides[key] = value.Value;
                count++;
            }
            else if (parent == "connectionStrings" && element.Name.LocalName == "add" && ((overriding && locator == "Match(name)") || verb == "Insert")
                && element.Attribute("name")?.Value is { Length: > 0 } connection && element.Attribute("connectionString") is { } text)
            {
                if (overrides["ConnectionStrings"] is not JsonObject strings)
                {
                    strings = new JsonObject();
                    overrides["ConnectionStrings"] = strings;
                }

                strings[connection] = text.Value;
                count++;
            }
            else if (element.Parent == document.Root && overriding && locator is null && sections.TryGetValue(element.Name.LocalName, out var section))
            {
                var values = new JsonObject();
                foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace != Xdt && !a.IsNamespaceDeclaration))
                {
                    var property = section.Schema.Properties.FirstOrDefault(p => p.XmlName == attribute.Name.LocalName && p.Kind == SettingKind.Value);
                    if (property is not null && SectionSchema.Parse(property, attribute.Value) is { } parsed)
                    {
                        values[property.Name] = ConfigConverter.Value(parsed);
                        count++;
                    }
                    else
                    {
                        notConverted.Add($"{path}/@{attribute.Name.LocalName} ({verb})");
                    }
                }

                if (values.Count > 0)
                {
                    overrides[section.Key] = values;
                }
            }
            else
            {
                notConverted.Add($"{path} ({verb}{(locator is null ? "" : ", " + locator)})");
            }
        }

        return (new ConvertedTransform { File = transform, Environment = environment, Overrides = count, NotConverted = notConverted }, overrides);
    }
}
