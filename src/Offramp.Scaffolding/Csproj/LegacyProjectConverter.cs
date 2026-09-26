using System.Text;
using System.Xml;
using System.Xml.Linq;
using Offramp.Core.Paths;
using Offramp.Refactoring.Codemods;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Csproj;

/// <summary>A <c>&lt;package&gt;</c> of a packages.config.</summary>
public sealed record PackagesConfigEntry(string Id, string Version, bool DevelopmentDependency);

/// <summary>What <see cref="LegacyProjectConverter"/> needs besides the project file.</summary>
public sealed record ConversionInput
{
    public required string RepositoryRoot { get; init; }

    public required ProjectInfo Project { get; init; }

    public required byte[] Bytes { get; init; }

    public IReadOnlyList<PackagesConfigEntry> Packages { get; init; } = [];

    /// <summary><c>--tfm</c>; empty keeps the project's framework.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary>Package versions live in Directory.Packages.props.</summary>
    public bool CentralVersions { get; init; }

    /// <summary><c>--nullable</c>, else null.</summary>
    public string? Nullable { get; init; }

    /// <summary>Properties that replace assembly attributes the SDK generates.</summary>
    public IReadOnlyList<CodemodPropertyEdit> Properties { get; init; } = [];
}

/// <summary>A note about the conversion: the diagnostic code and message.</summary>
public sealed record ConversionNote(string Code, string Message);

/// <summary>The SDK-style project, or why the project is not converted.</summary>
public sealed record ConversionOutput
{
    /// <summary>The new project file's bytes (the original's byte order mark and line endings); null when not converted.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Why the project is not converted (<c>OFR4304</c>), else null.</summary>
    public string? Refused { get; init; }

    public required string Sdk { get; init; }

    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary><c>globbed</c> when the SDK's default items give the same files, else <c>explicit</c>.</summary>
    public string CompileItems { get; init; } = "globbed";

    public IReadOnlyList<PackagesConfigEntry> Packages { get; init; } = [];

    /// <summary>What the SDK makes unnecessary and was left out, in document order (property and item names).</summary>
    public IReadOnlyList<string> Dropped { get; init; } = [];

    public IReadOnlyList<ConversionNote> Notes { get; init; } = [];
}

/// <summary>
/// Converts a legacy (non-SDK) C# project file to an SDK-style one
/// (docs/spec/commands/scaffold.md#csproj-modernize). The conversion is a pure function of
/// the file, the model's evaluated compile items, the files on disk, and packages.config:
/// properties the SDK sets are dropped, the rest kept; compile and resource items become
/// the SDK's globs when those give the same files; packages.config becomes PackageReference
/// items; build events become targets. <c>csproj modernize</c> proves the result compiles
/// the same inputs by building it.
/// </summary>
public static class LegacyProjectConverter
{
    private const string WebApplicationGuid = "{349C5851-65DF-11DA-9384-00065B846F21}";
    private const string WpfGuid = "{60DC8134-EBA5-43B8-BCC9-BB4BC16C2548}";

    /// <summary>Properties the SDK sets or makes meaningless.</summary>
    private static readonly HashSet<string> DroppedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Platform", "ProjectGuid", "AppDesignerFolder", "TargetFrameworkVersion", "TargetFrameworkProfile",
        "FileAlignment", "Deterministic", "ProjectTypeGuids", "NuGetPackageImportStamp", "SchemaVersion", "ProductVersion",
        "OldToolsVersion", "UpgradeBackupLocation", "FileUpgradeFlags", "TargetFrameworkIdentifier", "RestorePackages",
        "SolutionDir", "AutoGenerateBindingRedirects", "PreBuildEvent", "PostBuildEvent", "RunPostBuildEvent",
    };

    /// <summary>Configuration properties whose legacy template values are the SDK's defaults (by configuration).</summary>
    private static readonly Dictionary<string, string> DebugDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DebugSymbols"] = "true", ["DebugType"] = "*", ["Optimize"] = "false", ["OutputPath"] = @"bin\Debug\",
        ["DefineConstants"] = "DEBUG;TRACE", ["ErrorReport"] = "prompt", ["WarningLevel"] = "4", ["PlatformTarget"] = "AnyCPU",
    };

    private static readonly Dictionary<string, string> ReleaseDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DebugType"] = "*", ["Optimize"] = "true", ["OutputPath"] = @"bin\Release\", ["DefineConstants"] = "TRACE",
        ["ErrorReport"] = "prompt", ["WarningLevel"] = "4", ["PlatformTarget"] = "AnyCPU",
    };

    private static readonly HashSet<string> DroppedItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Folder", "BootstrapperPackage", "Service", "WCFMetadata",
    };

    public static ConversionOutput Convert(ConversionInput input)
    {
        var bom = input.Bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]);
        var text = Encoding.UTF8.GetString(input.Bytes, bom ? 3 : 0, input.Bytes.Length - (bom ? 3 : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var project = document.Root!;
        var ns = project.Name.Namespace;
        var context = new Context(input, ns);

        var guids = Property(project, ns, "ProjectTypeGuids") ?? "";
        if (guids.Contains(WebApplicationGuid, StringComparison.OrdinalIgnoreCase)
            || project.Elements(ns + "Import").Any(i => (i.Attribute("Project")?.Value ?? "").Contains("WebApplication", StringComparison.OrdinalIgnoreCase)))
        {
            return new ConversionOutput
            {
                Sdk = "Microsoft.NET.Sdk",
                Refused = "an ASP.NET web application project: the SDK has no System.Web project support. Keep it, and move routes with `offramp web scaffold`.",
            };
        }

        var frameworks = input.TargetFrameworks.Count > 0 ? input.TargetFrameworks
            : input.Project.TargetFrameworks.Count > 0 ? input.Project.TargetFrameworks
            : [FrameworkFrom(Property(project, ns, "TargetFrameworkVersion"))];
        var items = Items(context, project);
        var global = GlobalProperties(context, project, frameworks, guids);
        var configurations = ConfigurationGroups(context, project);
        var tail = Tail(context, project);

        var body = new List<XElement> { global };
        body.AddRange(configurations);
        body.AddRange(items);
        body.AddRange(tail);
        var output = Render(body, newline);
        return new ConversionOutput
        {
            Bytes = bom ? [0xEF, 0xBB, 0xBF, .. new UTF8Encoding(false).GetBytes(output)] : new UTF8Encoding(false).GetBytes(output),
            Sdk = "Microsoft.NET.Sdk",
            TargetFrameworks = frameworks,
            CompileItems = context.CompileItems,
            Packages = input.Packages,
            Dropped = context.Dropped,
            Notes = context.Notes,
        };
    }

    /// <summary><c>v4.7.2</c> → <c>net472</c>.</summary>
    public static string FrameworkFrom(string? version) =>
        string.IsNullOrEmpty(version) ? "net48" : "net" + version.TrimStart('v', 'V').Replace(".", "", StringComparison.Ordinal);

    private static XElement GlobalProperties(Context context, XElement project, IReadOnlyList<string> frameworks, string guids)
    {
        var group = new XElement("PropertyGroup");
        group.Add(frameworks.Count == 1 ? new XElement("TargetFramework", frameworks[0]) : new XElement("TargetFrameworks", string.Join(';', frameworks)));
        var name = Path.GetFileNameWithoutExtension(context.Input.Project.Id);
        foreach (var property in project.Elements(context.Ns + "PropertyGroup").Where(g => g.Attribute("Condition") is null).SelectMany(g => g.Elements()))
        {
            var local = property.Name.LocalName;
            var value = property.Value;
            if (DroppedProperties.Contains(local)
                || (local == "OutputType" && value.Equals("Library", StringComparison.OrdinalIgnoreCase))
                || (local is "RootNamespace" or "AssemblyName" && value == name)
                || (local is "TargetFramework" or "TargetFrameworks" or "Nullable" && group.Elements().Any(e => e.Name.LocalName == local)))
            {
                context.Drop(local);
                continue;
            }

            if (local == "Nullable" && context.Input.Nullable is not null)
            {
                continue;
            }

            group.Add(Strip(property));
        }

        if (guids.Contains(WpfGuid, StringComparison.OrdinalIgnoreCase))
        {
            group.Add(new XElement("UseWPF", "true"));
        }

        if (context.Input.Nullable is { } nullable)
        {
            group.Add(new XElement("Nullable", nullable));
        }

        if (context.CompileItems == "explicit")
        {
            group.Add(new XElement("EnableDefaultCompileItems", "false"));
        }

        foreach (var property in context.Input.Properties.Where(p => !group.Elements().Any(e => e.Name.LocalName == p.Name)))
        {
            group.Add(new XElement(property.Name, property.Value));
        }

        return group;
    }

    /// <summary>Configuration-conditioned groups, minus the legacy template's values the SDK defaults to.</summary>
    private static List<XElement> ConfigurationGroups(Context context, XElement project)
    {
        var result = new List<XElement>();
        foreach (var group in project.Elements(context.Ns + "PropertyGroup").Where(g => g.Attribute("Condition") is not null))
        {
            var condition = group.Attribute("Condition")!.Value;
            var defaults = condition.Contains("'Debug|AnyCPU'", StringComparison.OrdinalIgnoreCase) ? DebugDefaults
                : condition.Contains("'Release|AnyCPU'", StringComparison.OrdinalIgnoreCase) ? ReleaseDefaults
                : null;
            var kept = new XElement("PropertyGroup", new XAttribute("Condition", condition));
            foreach (var property in group.Elements())
            {
                if (defaults is not null && defaults.TryGetValue(property.Name.LocalName, out var value) && (value == "*" || string.Equals(value, property.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (property.Name.LocalName is "PreBuildEvent" or "PostBuildEvent" or "RunPostBuildEvent" || DroppedProperties.Contains(property.Name.LocalName))
                {
                    continue;
                }

                kept.Add(Strip(property));
            }

            if (kept.HasElements)
            {
                result.Add(kept);
            }
        }

        return result;
    }

    private static List<XElement> Items(Context context, XElement project)
    {
        var input = context.Input;
        var root = input.RepositoryRoot;
        var projectDirectory = RepoPaths.Normalize(Path.GetDirectoryName(input.Project.Id)!);
        var items = project.Elements(context.Ns + "ItemGroup").Where(g => g.Attribute("Condition") is null).SelectMany(g => g.Elements()).ToList();

        // Compile: the SDK's glob when it gives the same files (links aside).
        var compileInside = input.Project.Compile.Where(f => Inside(projectDirectory, f)).ToHashSet(StringComparer.Ordinal);
        var globbed = Glob(root, projectDirectory, "*.cs");
        if (!compileInside.SetEquals(globbed))
        {
            context.CompileItems = "explicit";
            var extra = globbed.Except(compileInside).Order(StringComparer.Ordinal).ToList();
            var missing = compileInside.Except(globbed).Order(StringComparer.Ordinal).ToList();
            context.Notes.Add(new ConversionNote("OFR4301",
                $"The Compile items are not what the SDK's glob gives ({(extra.Count > 0 ? "the glob adds " + string.Join(", ", extra) : "")}{(extra.Count > 0 && missing.Count > 0 ? "; " : "")}{(missing.Count > 0 ? "the glob misses " + string.Join(", ", missing) : "")}); the explicit list is kept with EnableDefaultCompileItems=false."));
        }

        var resources = items.Where(i => i.Name.LocalName == "EmbeddedResource" && Include(i).EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            .Select(i => Combine(projectDirectory, Include(i)))
            .ToHashSet(StringComparer.Ordinal);
        var resourcesGlobbed = resources.SetEquals(Glob(root, projectDirectory, "*.resx"));

        var packageFolders = input.Packages.Select(p => $@"packages\{p.Id}.{p.Version}\".ToLowerInvariant()).ToList();
        bool FromPackages(string path) => packageFolders.Any(f => path.Replace('/', '\\').ToLowerInvariant().Contains(f, StringComparison.Ordinal));

        var references = new XElement("ItemGroup");
        var compile = new XElement("ItemGroup");
        var updates = new XElement("ItemGroup");
        var projects = new XElement("ItemGroup");
        var others = new XElement("ItemGroup");
        foreach (var item in items)
        {
            var type = item.Name.LocalName;
            var include = Include(item);
            var metadata = item.Elements().Select(Strip).ToList();
            switch (type)
            {
                case "Reference":
                {
                    var hint = item.Element(context.Ns + "HintPath")?.Value;
                    var simple = include.Split(',')[0].Trim();
                    if (hint is null && CompileSets.SdkImplicitFrameworkReferences.Contains(simple))
                    {
                        context.Drop("Reference " + simple);
                    }
                    else if (hint is not null && FromPackages(hint))
                    {
                        context.Drop("Reference " + simple + " (packages.config)");
                    }
                    else
                    {
                        references.Add(new XElement("Reference", new XAttribute("Include", hint is null ? simple : include), metadata));
                    }

                    break;
                }

                case "Compile" when context.CompileItems == "explicit":
                    compile.Add(new XElement("Compile", new XAttribute("Include", include), metadata));
                    break;
                case "Compile" when !Inside(projectDirectory, Combine(projectDirectory, include)):
                    compile.Add(new XElement("Compile", new XAttribute("Include", include), metadata));
                    break;
                case "Compile" or "EmbeddedResource" when metadata.Count > 0 && (type == "Compile" || resourcesGlobbed || !include.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)):
                    if (type == "EmbeddedResource" && !include.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
                    {
                        others.Add(new XElement(type, new XAttribute("Include", include), metadata));
                    }
                    else
                    {
                        updates.Add(new XElement(type, new XAttribute("Update", include), metadata));
                    }

                    break;
                case "Compile":
                    break;
                case "EmbeddedResource" when include.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) && resourcesGlobbed:
                    break;
                case "None" when string.Equals(Path.GetFileName(include), "packages.config", StringComparison.OrdinalIgnoreCase):
                    context.Drop("None packages.config");
                    break;
                case "None" when metadata.Count == 0 && Inside(projectDirectory, Combine(projectDirectory, include)):
                    break;
                case "None" when Inside(projectDirectory, Combine(projectDirectory, include)):
                    updates.Add(new XElement("None", new XAttribute("Update", include), metadata));
                    break;
                case "ProjectReference":
                    projects.Add(new XElement("ProjectReference", new XAttribute("Include", include),
                        metadata.Where(m => m.Name.LocalName is not ("Project" or "Name"))));
                    break;
                case "Analyzer" when FromPackages(include):
                    context.Drop("Analyzer " + Path.GetFileName(include) + " (packages.config)");
                    break;
                default:
                    if (DroppedItemTypes.Contains(type))
                    {
                        context.Drop(type);
                    }
                    else
                    {
                        others.Add(Strip(item));
                    }

                    break;
            }
        }

        if (!resourcesGlobbed && resources.Count > 0)
        {
            others.AddFirst(new XComment(" Kept as listed: the .resx files on disk are not the ones the project embeds. "));
            foreach (var item in items.Where(i => i.Name.LocalName == "EmbeddedResource" && Include(i).EndsWith(".resx", StringComparison.OrdinalIgnoreCase)))
            {
                others.Add(new XElement("EmbeddedResource", new XAttribute("Remove", Include(item))));
                others.Add(new XElement("EmbeddedResource", new XAttribute("Include", Include(item)), item.Elements().Select(Strip)));
            }
        }

        var packages = new XElement("ItemGroup");
        foreach (var package in input.Packages)
        {
            var element = new XElement("PackageReference", new XAttribute("Include", package.Id));
            if (!input.CentralVersions)
            {
                element.Add(new XAttribute("Version", package.Version));
            }

            if (package.DevelopmentDependency)
            {
                element.Add(new XAttribute("PrivateAssets", "all"));
            }

            packages.Add(element);
        }

        return [.. new[] { compile, updates, references, packages, projects, others }.Where(g => g.HasElements)];
    }

    /// <summary>Kept imports and targets, build events as targets, and conditioned groups kept as they are.</summary>
    private static List<XElement> Tail(Context context, XElement project)
    {
        var ns = context.Ns;
        var result = new List<XElement>();
        foreach (var element in project.Elements())
        {
            var local = element.Name.LocalName;
            if (local == "Import")
            {
                var path = element.Attribute("Project")?.Value ?? "";
                if (path.Contains("Microsoft.Common.props", StringComparison.OrdinalIgnoreCase) || path.Contains("Microsoft.CSharp.targets", StringComparison.OrdinalIgnoreCase)
                    || path.Contains(@"packages\", StringComparison.OrdinalIgnoreCase) || path.Contains("packages/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Drop("Import " + Path.GetFileName(path.Replace('\\', '/')));
                    continue;
                }

                result.Add(Strip(element));
            }
            else if (local == "Target")
            {
                var name = element.Attribute("Name")?.Value;
                if (name == "EnsureNuGetPackageBuildImports")
                {
                    context.Drop("Target EnsureNuGetPackageBuildImports");
                    continue;
                }

                var target = Strip(element);
                if (name is "BeforeBuild" or "AfterBuild")
                {
                    target.SetAttributeValue("Name", name + "Legacy");
                    target.SetAttributeValue(name == "BeforeBuild" ? "BeforeTargets" : "AfterTargets", name);
                    context.Notes.Add(new ConversionNote("OFR4302", $"The {name} target is renamed {name}Legacy and hooked to {name}: the SDK defines {name} after the project body, so the original would be overridden."));
                }

                result.Add(target);
            }
            else if (local == "ItemGroup" && element.Attribute("Condition") is not null)
            {
                result.Add(Strip(element));
            }
        }

        foreach (var (property, target, hook) in new[] { ("PreBuildEvent", "PreBuild", "BeforeTargets"), ("PostBuildEvent", "PostBuild", "AfterTargets") })
        {
            var command = project.Elements(ns + "PropertyGroup").SelectMany(g => g.Elements(ns + property)).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            if (command is null)
            {
                continue;
            }

            result.Add(new XElement("Target", new XAttribute("Name", target), new XAttribute(hook, property),
                new XElement("Exec", new XAttribute("Command", command))));
            context.Notes.Add(new ConversionNote("OFR4302",
                $"The {property} is now the {target} target ({hook}=\"{property}\"). Review it: macros such as $(TargetPath) keep their meaning, but paths relative to the output folder change with the SDK's bin/<configuration>/<framework>/ layout."));
        }

        return result;
    }

    private static string Render(List<XElement> body, string newline)
    {
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk\">").Append(newline);
        foreach (var element in body)
        {
            builder.Append(newline);
            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace };
            var writer = new StringBuilder();
            using (var xml = XmlWriter.Create(writer, settings))
            {
                RemoveWhitespace(element).WriteTo(xml);
            }

            foreach (var line in writer.ToString().Split('\n'))
            {
                builder.Append("  ").Append(line).Append(newline);
            }
        }

        builder.Append(newline).Append("</Project>").Append(newline);
        return builder.ToString();
    }

    private static XElement RemoveWhitespace(XElement element)
    {
        var copy = new XElement(element);
        foreach (var text in copy.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value) && t.Parent!.HasElements).ToList())
        {
            text.Remove();
        }

        return copy;
    }

    /// <summary>A copy without the MSBuild 2003 namespace.</summary>
    private static XElement Strip(XElement element) =>
        new(element.Name.LocalName,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration),
            element.Nodes().Select(n => n is XElement child ? Strip(child) : n is XText text ? new XText(text) : (object)n));

    private static string? Property(XElement project, XNamespace ns, string name) =>
        project.Elements(ns + "PropertyGroup").SelectMany(g => g.Elements(ns + name)).Select(e => e.Value).FirstOrDefault();

    private static string Include(XElement item) => item.Attribute("Include")?.Value ?? "";

    private static bool Inside(string directory, string path) =>
        directory.Length == 0 ? !path.StartsWith("../", StringComparison.Ordinal) : path.StartsWith(directory + "/", StringComparison.Ordinal);

    /// <summary>A project-relative include as a repository-relative path, with <c>..</c> resolved.</summary>
    private static string Combine(string directory, string include)
    {
        var parts = new List<string>();
        foreach (var segment in (directory.Length == 0 ? include : directory + "/" + include).Replace('\\', '/').Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else
                {
                    parts.Add(segment);
                }
            }
            else if (segment is not ("" or "."))
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>What <c>**/*.ext</c> gives in the project's folder: repository-relative, without bin/, obj/, and hidden folders.</summary>
    private static HashSet<string> Glob(string root, string projectDirectory, string pattern)
    {
        var absolute = RepoPaths.ToAbsolute(root, projectDirectory);
        return Directory.EnumerateFiles(absolute, pattern, SearchOption.AllDirectories)
            .Select(f => RepoPaths.Normalize(Path.GetRelativePath(absolute, f)))
            .Where(f => !f.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                && !f.Split('/').SkipLast(1).Any(segment => segment.StartsWith('.')))
            .Select(f => projectDirectory.Length == 0 ? f : projectDirectory + "/" + f)
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed class Context(ConversionInput input, XNamespace ns)
    {
        public ConversionInput Input { get; } = input;

        public XNamespace Ns { get; } = ns;

        public string CompileItems { get; set; } = "globbed";

        public List<string> Dropped { get; } = [];

        public List<ConversionNote> Notes { get; } = [];

        public void Drop(string what)
        {
            if (!Dropped.Contains(what))
            {
                Dropped.Add(what);
            }
        }
    }
}
