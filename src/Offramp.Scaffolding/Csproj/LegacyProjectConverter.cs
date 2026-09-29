using System.Text;
using System.Xml;
using System.Xml.Linq;
using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;
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

    /// <summary>
    /// Set <c>DisableTransitiveProjectReferences</c>: a referenced project has project references of
    /// its own, which the SDK would pass on and the legacy build did not.
    /// </summary>
    public bool DisableTransitiveProjectReferences { get; init; }
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

    private static readonly HashSet<string> WpfAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml",
    };

    /// <summary>
    /// Targets the common targets define, which a target of the same name in an SDK-style project's
    /// body no longer replaces: the SDK imports them after the body (BeforeBuild and AfterBuild are
    /// renamed and hooked instead).
    /// </summary>
    private static readonly HashSet<string> CommonTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "BeforeRebuild", "AfterRebuild", "BeforeClean", "AfterClean", "BeforeResolveReferences", "AfterResolveReferences",
        "BeforeCompile", "AfterCompile", "BeforeResGen", "AfterResGen", "BeforePublish", "AfterPublish",
        "Build", "CoreBuild", "Rebuild", "Clean", "Compile", "CoreCompile", "ResolveReferences", "ResolveAssemblyReferences",
        "PrepareForBuild", "CopyFilesToOutputDirectory", "_CopyFilesMarkedCopyLocal", "GetCopyToOutputDirectoryItems",
        "_CopyOutOfDateSourceItemsToOutputDirectory", "GenerateBindingRedirects", "PrepareResources", "CoreResGen",
    };

    /// <summary>Properties the SDK sets from TargetFramework and its own layout, which an imported file should not set too.</summary>
    private static readonly string[] SdkOwnedProperties =
    [
        "TargetFrameworkVersion", "TargetFrameworkIdentifier", "TargetFramework", "TargetFrameworks", "OutputPath", "BaseOutputPath",
        "IntermediateOutputPath", "BaseIntermediateOutputPath", "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64",
    ];

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
        var output = Body(context, project, frameworks, guids, newline);

        // SolutionDir is dropped (building the project alone must not need it), unless what is
        // kept still uses it: then its definition, with its fallback for builds outside the solution, stays.
        if (output.Contains("$(SolutionDir)", StringComparison.OrdinalIgnoreCase))
        {
            context = new Context(input, ns) { KeepSolutionDir = true };
            output = Body(context, project, frameworks, guids, newline);
        }

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

    private static string Body(Context context, XElement project, IReadOnlyList<string> frameworks, string guids, string newline)
    {
        context.Frameworks = frameworks;
        var items = Items(context, project);
        var global = GlobalProperties(context, project, frameworks, guids);
        var configurations = ConfigurationGroups(context, project);
        var tail = Tail(context, project);

        var body = new List<XElement> { global };
        body.AddRange(configurations);
        body.AddRange(items);
        body.AddRange(tail);
        return Render(body, newline);
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
            if (context.Drops(local)
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

        // A -windows target gets Windows Forms and WPF from the Windows desktop framework, not from references.
        var windows = frameworks.Any(f => f.Contains("-windows", StringComparison.OrdinalIgnoreCase));
        var referenced = FrameworkReferences(context, project);
        if (guids.Contains(WpfGuid, StringComparison.OrdinalIgnoreCase) || (windows && referenced.Overlaps(WpfAssemblies)))
        {
            group.Add(new XElement("UseWPF", "true"));
        }

        if (windows && referenced.Contains("System.Windows.Forms"))
        {
            group.Add(new XElement("UseWindowsForms", "true"));
        }

        if (context.Input.Nullable is { } nullable)
        {
            group.Add(new XElement("Nullable", nullable));
        }

        if (context.CompileItems == "explicit")
        {
            group.Add(new XElement("EnableDefaultCompileItems", "false"));
        }

        // The SDK appends the target framework to the output path; a legacy project's output
        // stays where build steps and HintPaths into its bin folder expect it.
        if (frameworks.Count == 1)
        {
            group.Add(new XElement("AppendTargetFrameworkToOutputPath", "false"));
        }

        // The SDK passes a referenced project's references on; the legacy project compiled against its own only.
        if (context.Input.DisableTransitiveProjectReferences && !group.Elements().Any(e => e.Name.LocalName == "DisableTransitiveProjectReferences"))
        {
            group.Add(new XElement("DisableTransitiveProjectReferences", "true"));
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

                if (property.Name.LocalName is "PreBuildEvent" or "PostBuildEvent" or "RunPostBuildEvent" || context.Drops(property.Name.LocalName))
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
        var frameworkReferences = new XElement("ItemGroup", new XAttribute("Condition", "'$(TargetFrameworkIdentifier)' == '.NETFramework'"));
        var compile = new XElement("ItemGroup");
        var updates = new XElement("ItemGroup");
        var projects = new XElement("ItemGroup");
        var others = new XElement("ItemGroup");
        foreach (var item in items)
        {
            var type = item.Name.LocalName;
            var include = Include(item);
            // Metadata written as attributes (ReferenceOutputAssembly="false") and the item's Condition count too.
            var metadata = item.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName is not ("Include" or "Update" or "Remove"))
                .Select(a => (object)new XAttribute(a.Name.LocalName, a.Value))
                .Concat(item.Elements().Select(Strip))
                .ToList();
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
                    else if (hint is null && context.Modern)
                    {
                        // A .NET Framework assembly: the other targets get theirs from their framework.
                        frameworkReferences.Add(new XElement("Reference", new XAttribute("Include", simple), metadata));
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
                        metadata.Where(m => m is not XElement { Name.LocalName: "Project" or "Name" })));
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

        return [.. new[] { compile, updates, references, frameworkReferences, packages, projects, others }.Where(g => g.HasElements)];
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

                // NuGet 2's package restore ("Enable NuGet Package Restore"), with RestorePackages: PackageReference restore replaces it.
                if (path.Replace('\\', '/').EndsWith(".nuget/NuGet.targets", StringComparison.OrdinalIgnoreCase))
                {
                    context.Drop("Import NuGet.targets");
                    continue;
                }

                if (SdkOwnedPropertiesIn(context, path) is { Count: > 0 } owned)
                {
                    context.Notes.Add(new ConversionNote("OFR4308",
                        $"The imported {path} sets {string.Join(", ", owned)} unconditionally. The SDK sets them from TargetFramework and its own layout, and the import now comes after the project body: condition them on '$(UsingMicrosoftNETSdk)' != 'true', or remove them."));
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
                else if (name is not null && CommonTargets.Contains(name))
                {
                    context.Notes.Add(new ConversionNote("OFR4308",
                        $"The {name} target in the project body replaced the common target of that name. In an SDK-style project the SDK's targets come after the body and win, so this one no longer takes effect: hook a target of another name to it (BeforeTargets or AfterTargets), or remove it."));
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
            var definition = project.Elements(ns + "PropertyGroup").SelectMany(g => g.Elements(ns + property)).FirstOrDefault(e => e.Value.Trim().Length > 0);
            if (definition is null)
            {
                continue;
            }

            // The property group's condition, and the property's own, now hold for the target.
            var conditions = new[] { definition.Parent!.Attribute("Condition")?.Value, definition.Attribute("Condition")?.Value }
                .Select(c => c?.Trim())
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();
            var converted = new XElement("Target", new XAttribute("Name", target), new XAttribute(hook, property));
            if (conditions.Count > 0)
            {
                converted.Add(new XAttribute("Condition", conditions.Count == 1 ? conditions[0]! : string.Join(" and ", conditions.Select(c => $"({c})"))));
            }

            converted.Add(new XElement("Exec", new XAttribute("Command", definition.Value.Trim())));
            result.Add(converted);
            context.Notes.Add(new ConversionNote("OFR4302",
                $"The {property} is now the {target} target ({hook}=\"{property}\"). Review it: macros such as $(TargetPath) keep their meaning, but paths relative to the output folder change with the SDK's bin/<configuration>/<framework>/ layout."));
        }

        return result;
    }

    /// <summary>The simple names of the project's framework references (Reference items without a HintPath).</summary>
    private static HashSet<string> FrameworkReferences(Context context, XElement project) =>
        project.Elements(context.Ns + "ItemGroup").SelectMany(g => g.Elements(context.Ns + "Reference"))
            .Where(r => r.Element(context.Ns + "HintPath") is null)
            .Select(r => Include(r).Split(',')[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The SDK-owned properties an imported file sets without a condition (on the property or its
    /// group); empty when the path uses properties other than the project's folder, or the file is
    /// not in the repository.
    /// </summary>
    private static List<string> SdkOwnedPropertiesIn(Context context, string path)
    {
        var relative = path.Replace("$(MSBuildProjectDirectory)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileDirectory)", "", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/')
            .TrimStart('/');
        if (relative.Contains("$(", StringComparison.Ordinal))
        {
            return [];
        }

        var file = Combine(RepoPaths.Normalize(Path.GetDirectoryName(context.Input.Project.Id)!), relative);
        var absolute = RepoPaths.ToAbsolute(context.Input.RepositoryRoot, file);
        if (file.StartsWith("../", StringComparison.Ordinal) || !File.Exists(absolute))
        {
            return [];
        }

        try
        {
            var imported = XDocument.Load(absolute).Root!;
            var ns = imported.Name.Namespace;
            return [.. imported.Elements(ns + "PropertyGroup").Where(g => g.Attribute("Condition") is null)
                .SelectMany(g => g.Elements())
                .Where(e => e.Attribute("Condition") is null && SdkOwnedProperties.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                .Select(e => e.Name.LocalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (XmlException)
        {
            return [];
        }
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

        public IReadOnlyList<string> Frameworks { get; set; } = [];

        /// <summary>A target framework is not .NET Framework (net8.0, netstandard2.0, net10.0-windows).</summary>
        public bool Modern => Frameworks.Any(f => !Tfm.IsNetFramework(f));

        /// <summary>Keep SolutionDir's definition: something kept uses <c>$(SolutionDir)</c>.</summary>
        public bool KeepSolutionDir { get; init; }

        public List<string> Dropped { get; } = [];

        public List<ConversionNote> Notes { get; } = [];

        /// <summary>A property the SDK sets or makes meaningless.</summary>
        public bool Drops(string property) =>
            DroppedProperties.Contains(property) && !(KeepSolutionDir && string.Equals(property, "SolutionDir", StringComparison.OrdinalIgnoreCase));

        public void Drop(string what)
        {
            if (!Dropped.Contains(what))
            {
                Dropped.Add(what);
            }
        }
    }
}
