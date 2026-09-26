using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;
using Catalog = Offramp.Analyzers.Codemods;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Config;

/// <summary>What <c>config convert</c> is asked to do.</summary>
public sealed record ConfigConvertRequest
{
    public required string RepositoryRoot { get; init; }

    public required ProjectInfo Project { get; init; }

    /// <summary>The project's recorded compilation, where custom section classes are found; null leaves custom sections unresolved.</summary>
    public Compilation? Compilation { get; init; }

    /// <summary><c>--out</c>, repository-relative; null writes next to the configuration file.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary><c>--sections</c>: appSettings, connectionStrings, custom (every custom section), or section names; empty converts everything.</summary>
    public IReadOnlyList<string> Sections { get; init; } = [];

    public bool Shim { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>The dry run and the change set that writes it.</summary>
public sealed record ConfigConvertPlan(ConfigConvertResult Result, ChangeSet ChangeSet);

/// <summary>
/// <c>config convert</c> (docs/spec/commands/scaffold.md#config-convert): App.config or
/// Web.config to appsettings.json. appSettings become root keys (what
/// <c>IConfiguration["key"]</c> reads), connection strings the ConnectionStrings section,
/// and custom sections nested objects shaped by their section classes, read with the semantic
/// model, with an options class for each. Transforms that are overrides become
/// appsettings.{Environment}.json.
/// </summary>
public static class ConfigConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, string> DictionaryHandlers = new(StringComparer.Ordinal)
    {
        ["System.Configuration.NameValueSectionHandler"] = "add",
        ["System.Configuration.AppSettingsSection"] = "add",
        ["System.Configuration.DictionarySectionHandler"] = "add",
        ["System.Configuration.NameValueFileSectionHandler"] = "add",
        ["System.Configuration.SingleTagSectionHandler"] = "attributes",
    };

    public static ConfigConvertPlan? Plan(ConfigConvertRequest request)
    {
        var root = request.RepositoryRoot;
        var project = request.Project;
        var directory = RepoPaths.Normalize(Path.GetDirectoryName(project.Id) ?? "");
        var source = FindConfig(root, directory);
        if (source is null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4405, $"{project.Id} has no App.config or Web.config in its folder.", new DiagnosticLocation(project.Id));
            return null;
        }

        var document = XDocument.Load(RepoPaths.ToAbsolute(root, source));
        var context = new Context(request, source, SectionTypes(document.Root!));
        var json = new JsonObject();
        foreach (var element in document.Root!.Elements())
        {
            Convert(context, element, json);
        }

        var output = request.OutputDirectory is { } requested ? RepoPaths.Normalize(requested).TrimEnd('/') : directory;
        var ns = project.RootNamespace ?? project.AssemblyName ?? Path.GetFileNameWithoutExtension(project.Id);
        var files = new List<(string Path, string Content)>
        {
            (Join(output, "appsettings.json"), json.ToJsonString(JsonOptions) + "\n"),
            (Join(output, "ConfigurationOptions.cs"), ConfigTemplates.Options(ns, context.AppSettingKeys, context.Schemas)),
        };

        var transforms = new List<ConvertedTransform>();
        foreach (var transform in ConfigTransforms.Find(root, source))
        {
            var converted = ConfigTransforms.Convert(root, transform, context.SectionSchemas);
            var name = $"appsettings.{converted.Result.Environment}.json";
            if (converted.Overrides.Count > 0)
            {
                files.Add((Join(output, name), converted.Overrides.ToJsonString(JsonOptions) + "\n"));
            }

            if (converted.Result.NotConverted.Count > 0)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR4404,
                    $"{transform}: {string.Join("; ", converted.Result.NotConverted)}.", new DiagnosticLocation(project.Id, transform));
            }

            transforms.Add(converted.Result with { Output = converted.Overrides.Count > 0 ? Join(output, name) : null });
        }

        if (request.Shim)
        {
            files.Add((Join(output, "ConfigurationManagerShim.cs"), ConfigTemplates.Shim(ns)));
        }

        var existing = files.Where(f => File.Exists(RepoPaths.ToAbsolute(root, f.Path))).Select(f => f.Path).ToList();
        if (existing.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4406, $"{string.Join(", ", existing)} already exist{(existing.Count == 1 ? "s" : "")}; nothing was written.", new DiagnosticLocation(project.Id));
            return null;
        }

        var changeSet = new ChangeSet();
        foreach (var (path, content) in files)
        {
            changeSet.Create(path, content);
        }

        var nextSteps = ProjectEdits(request, changeSet, output, directory);
        var result = new ConfigConvertResult
        {
            Project = project.Id,
            Source = source,
            Sections = context.Sections,
            Transforms = transforms,
            Files = [.. files.Select(f => f.Path).Order(StringComparer.Ordinal)],
            Shim = request.Shim,
            NextSteps = nextSteps,
            Preview = changeSet.Preview(),
        };
        return new ConfigConvertPlan(result, changeSet);
    }

    private static void Convert(Context context, XElement element, JsonObject json)
    {
        var name = element.Name.LocalName;
        switch (name)
        {
            case "configSections":
                return;
            case "appSettings":
                if (context.Includes("appSettings"))
                {
                    AppSettings(context, element, json);
                }

                return;
            case "connectionStrings":
                if (context.Includes("connectionStrings"))
                {
                    ConnectionStrings(context, element, json);
                }

                return;
            case "startup":
                context.Sections.Add(new ConvertedSection { Name = name, Kind = "dropped", Notes = ["The supported runtime is the target framework's now."] });
                return;
            case "runtime":
                context.Sections.Add(new ConvertedSection { Name = name, Kind = "dropped", Notes = ["Binding redirects and runtime settings: modern .NET unifies assembly versions itself; GC and threading settings go in runtimeconfig.json (the project's properties)."] });
                return;
            case "system.serviceModel":
                context.Request.Diagnostics.Report(DiagnosticCatalog.OFR4402, $"{context.Source}: system.serviceModel is not converted; configure WCF clients in code and move services to CoreWCF.", new DiagnosticLocation(context.Request.Project.Id, context.Source));
                context.Sections.Add(new ConvertedSection { Name = name, Kind = "unsupported", Notes = ["WCF configuration (OFR4402)."] });
                return;
            case "system.web" or "system.webServer":
                context.Request.Diagnostics.Report(DiagnosticCatalog.OFR4403, $"{context.Source}: {name} belongs to the web migration (`offramp web scaffold`).", new DiagnosticLocation(context.Request.Project.Id, context.Source));
                context.Sections.Add(new ConvertedSection { Name = name, Kind = "dropped", Notes = ["ASP.NET and IIS settings (OFR4403)."] });
                return;
            case "system.diagnostics":
                context.Sections.Add(new ConvertedSection { Name = name, Kind = "dropped", Notes = ["Trace sources and listeners: configure Microsoft.Extensions.Logging (a Logging section) instead."] });
                return;
        }

        if (!context.SectionTypes.TryGetValue(name, out var type))
        {
            context.Sections.Add(new ConvertedSection { Name = name, Kind = "unsupported", Notes = ["Not declared in configSections."] });
            Unsupported(context, name, "it is not declared in configSections");
            return;
        }

        if (!context.Includes("custom") && !context.Includes(name))
        {
            return;
        }

        CustomSection(context, element, type, json);
    }

    private static void AppSettings(Context context, XElement element, JsonObject json)
    {
        var notes = new List<string>();
        if (element.Attribute("file") is { } file)
        {
            notes.Add($"The settings in {file.Value} (the file attribute) are not read; convert that file's keys by hand.");
        }

        var values = 0;
        foreach (var add in element.Elements("add"))
        {
            if (add.Attribute("key")?.Value is { Length: > 0 } key)
            {
                json[key] = add.Attribute("value")?.Value ?? "";
                if (!context.AppSettingKeys.Contains(key))
                {
                    context.AppSettingKeys.Add(key);
                }

                values++;
            }
        }

        context.Sections.Add(new ConvertedSection { Name = "appSettings", Kind = "appSettings", JsonKey = "", Options = "AppSettingsOptions", Values = values, Notes = notes });
    }

    private static void ConnectionStrings(Context context, XElement element, JsonObject json)
    {
        var strings = new JsonObject();
        var notes = new List<string>();
        if (element.Attribute("configSource") is { } external)
        {
            notes.Add($"The connection strings in {external.Value} (configSource) are not read.");
        }

        foreach (var add in element.Elements("add"))
        {
            if (add.Attribute("name")?.Value is { Length: > 0 } name)
            {
                strings[name] = add.Attribute("connectionString")?.Value ?? "";
                if (add.Attribute("providerName")?.Value is { Length: > 0 } provider)
                {
                    notes.Add($"{name}: providerName {provider} has no appsettings.json counterpart; the code chooses the provider.");
                }
            }
        }

        if (strings.Count > 0)
        {
            json["ConnectionStrings"] = strings;
        }

        context.Sections.Add(new ConvertedSection { Name = "connectionStrings", Kind = "connectionStrings", JsonKey = "ConnectionStrings", Values = strings.Count, Notes = notes });
    }

    private static void CustomSection(Context context, XElement element, string type, JsonObject json)
    {
        var name = element.Name.LocalName;
        var key = Templates.Pascal(name);
        var typeName = type.Split(',')[0].Trim();
        if (DictionaryHandlers.TryGetValue(typeName, out var shape))
        {
            var values = new JsonObject();
            if (shape == "add")
            {
                foreach (var add in element.Elements("add"))
                {
                    if (add.Attribute("key")?.Value is { Length: > 0 } itemKey)
                    {
                        values[itemKey] = add.Attribute("value")?.Value ?? "";
                    }
                }
            }
            else
            {
                foreach (var attribute in element.Attributes())
                {
                    values[attribute.Name.LocalName] = attribute.Value;
                }
            }

            json[key] = values;
            context.Sections.Add(new ConvertedSection { Name = name, Kind = "custom", JsonKey = key, Values = values.Count, Notes = [$"{typeName}: a dictionary; read it with GetSection(\"{key}\")."] });
            return;
        }

        if (context.Request.Compilation?.GetTypeByMetadataName(typeName) is not { } symbol)
        {
            context.Sections.Add(new ConvertedSection { Name = name, Kind = "unsupported", Notes = [$"The section class {typeName} is not in the solution."] });
            Unsupported(context, name, $"its section class {typeName} is not in the solution");
            return;
        }

        var schema = SectionSchema.From(symbol);
        var notes = new List<string>();
        var value = Element(context, name, element, schema, notes);
        json[key] = value;
        context.Schemas.Add((key, schema));
        context.SectionSchemas[name] = (key, schema);
        context.Sections.Add(new ConvertedSection { Name = name, Kind = "custom", JsonKey = key, Options = schema.OptionsName, Values = Count(value), Notes = notes });
    }

    /// <summary>An element as a JSON object: attributes and child elements by the schema, in declaration order.</summary>
    public static JsonObject Element(Context context, string path, XElement element, SectionSchema schema, List<string> notes)
    {
        var result = new JsonObject();
        foreach (var property in schema.Properties)
        {
            var where = path + "/" + property.XmlName;
            switch (property.Kind)
            {
                case SettingKind.Value when element.Attribute(property.XmlName) is { } attribute:
                    if (SectionSchema.Parse(property, attribute.Value) is { } parsed)
                    {
                        result[property.Name] = Value(parsed);
                    }
                    else
                    {
                        notes.Add($"{where}: \"{attribute.Value}\" is not a {property.ValueType}.");
                        Unsupported(context, where, $"\"{attribute.Value}\" is not a {property.ValueType}");
                    }

                    break;
                case SettingKind.Element when element.Element(property.XmlName) is { } child:
                    result[property.Name] = Element(context, where, child, property.Element!, notes);
                    break;
                case SettingKind.Unsupported when element.Attribute(property.XmlName) is not null || element.Element(property.XmlName) is not null:
                    notes.Add($"{where}: {property.Reason}");
                    Unsupported(context, where, property.Reason!.TrimEnd('.'));
                    break;
            }
        }

        return result;
    }

    /// <summary>A parsed setting as a JSON value.</summary>
    public static JsonValue Value(object parsed) => parsed switch
    {
        bool flag => JsonValue.Create(flag),
        int integer => JsonValue.Create(integer),
        long wide => JsonValue.Create(wide),
        double real => JsonValue.Create(real),
        decimal money => JsonValue.Create(money),
        _ => JsonValue.Create(parsed.ToString()!),
    };

    private static void Unsupported(Context context, string what, string why) =>
        context.Request.Diagnostics.Report(DiagnosticCatalog.OFR4401, $"{context.Source}: {what} is left out: {why}.", new DiagnosticLocation(context.Request.Project.Id, context.Source));

    /// <summary>For an SDK-style project: copy appsettings*.json to the output, and reference the configuration abstractions for the shim.</summary>
    private static List<string> ProjectEdits(ConfigConvertRequest request, ChangeSet changeSet, string output, string directory)
    {
        var steps = new List<string>
        {
            "Read settings through IConfiguration: a host builder loads appsettings.json and appsettings.{Environment}.json; bind each section with services.Configure<TOptions>(configuration.GetSection(\"Key\")).",
        };
        if (!request.Project.SdkStyle)
        {
            steps.Add("The project is not SDK-style: run `offramp csproj modernize` so appsettings.json is copied to the output and packages can be referenced.");
            if (request.Shim)
            {
                steps.Add("Add the Microsoft.Extensions.Configuration.Abstractions package, which ConfigurationManagerShim needs.");
            }
        }
        else
        {
            var before = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, request.Project.Id));
            var editor = ProjectFileEditor.Load(before);
            if (output == directory && !(request.Project.Sdk ?? "").Contains("Web", StringComparison.OrdinalIgnoreCase))
            {
                editor.AddUpdate("None", "appsettings*.json", [KeyValuePair.Create("CopyToOutputDirectory", "PreserveNewest")]);
            }

            if (request.Shim)
            {
                var package = Catalog.ConfigManager.Packages[0];
                editor.AddPackageReference(package.Id, package.Version);
            }

            changeSet.Edit(request.Project.Id, before, editor.Save());
        }

        if (request.Shim)
        {
            steps.Add("Call ConfigurationManagerShim.Initialize(configuration) at startup, then `offramp scan` and `offramp codemod run --mod config-manager-shim` to point ConfigurationManager call sites at the shim.");
        }

        return steps;
    }

    private static string? FindConfig(string root, string directory)
    {
        var absolute = RepoPaths.ToAbsolute(root, directory);
        return Directory.EnumerateFiles(absolute)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(f => f.Equals("App.config", StringComparison.OrdinalIgnoreCase) || f.Equals("Web.config", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(f => Join(directory, f))
            .FirstOrDefault();
    }

    /// <summary>Section name → type, from configSections (sections in groups by their element name).</summary>
    private static Dictionary<string, string> SectionTypes(XElement configuration)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in configuration.Element("configSections")?.Descendants("section") ?? [])
        {
            if (section.Attribute("name")?.Value is { Length: > 0 } name && section.Attribute("type")?.Value is { Length: > 0 } type)
            {
                result[name] = type;
            }
        }

        return result;
    }

    private static int Count(JsonObject value) => value.Sum(p => p.Value is JsonObject nested ? Count(nested) : 1);

    private static string Join(string directory, string file) => directory.Length == 0 ? file : directory + "/" + file;

    /// <summary>Per-run state.</summary>
    public sealed class Context(ConfigConvertRequest request, string source, Dictionary<string, string> sectionTypes)
    {
        public ConfigConvertRequest Request { get; } = request;

        public string Source { get; } = source;

        public Dictionary<string, string> SectionTypes { get; } = sectionTypes;

        public List<ConvertedSection> Sections { get; } = [];

        public List<string> AppSettingKeys { get; } = [];

        /// <summary>JSON key and schema of each converted custom section, in document order.</summary>
        public List<(string Key, SectionSchema Schema)> Schemas { get; } = [];

        /// <summary>By element name, for transforms.</summary>
        public Dictionary<string, (string Key, SectionSchema Schema)> SectionSchemas { get; } = new(StringComparer.Ordinal);

        public bool Includes(string name) =>
            Request.Sections.Count == 0 || Request.Sections.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class Templates
{
    public static string Pascal(string name)
    {
        var parts = name.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
