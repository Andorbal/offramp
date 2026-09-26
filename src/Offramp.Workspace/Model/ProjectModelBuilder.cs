using Offramp.Core.Configuration;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;

namespace Offramp.Workspace.Model;

/// <summary>What the builder needs besides a project's evaluations.</summary>
public sealed record ProjectBuildContext
{
    public required CapturePathMapper Paths { get; init; }

    public required OfframpConfig Config { get; init; }

    /// <summary>(project id, target framework) → compiler call in the model's compiler log.</summary>
    public required IReadOnlyDictionary<(string Project, string Tfm), CompilerCallRef> CompilerCalls { get; init; }

    /// <summary>(project id, target framework) → preprocessor symbols the compiler saw.</summary>
    public IReadOnlyDictionary<(string Project, string Tfm), IReadOnlyList<string>> CompilerDefines { get; init; } =
        new Dictionary<(string, string), IReadOnlyList<string>>();

    public required PathGlobs Excluded { get; init; }
}

/// <summary>Turns one project's evaluations into a <see cref="ProjectInfo"/> (docs/spec/02-workspace-model.md).</summary>
public static class ProjectModelBuilder
{
    /// <summary>Properties recorded in <c>properties</c> when set (they do not vary by target framework in practice).</summary>
    private static readonly string[] RecordedProperties =
    [
        "LangVersion", "Nullable", "TreatWarningsAsErrors", "NoWarn", "GenerateSerializationAssemblies",
        "ManagePackageVersionsCentrally", "DirectoryPackagesPropsPath", "UseWPF", "UseWindowsForms", "IsPackable",
        "IsTestProject", "ProjectTypeGuids", "ImplicitUsings", "EnableWindowsTargeting", "OfframpCompileOnly",
    ];

    public static ProjectInfo Build(string projectId, IReadOnlyList<EvaluatedProject> evaluations, ProjectBuildContext context)
    {
        var outer = evaluations.FirstOrDefault(e => e.TargetFramework is null);
        var inner = evaluations.Where(e => e.TargetFramework is not null)
            .OrderBy(e => e.TargetFramework, StringComparer.Ordinal)
            .ToList();
        var all = inner.Count > 0 ? inner : [.. evaluations];
        var first = all[0];
        var projectFile = first.ProjectFile;
        var projectDirectory = Path.GetDirectoryName(projectFile.Replace('\\', '/'))!;

        var tfms = Tfm.Sort(inner.Select(e => e.TargetFramework!)
            .Concat(SplitList(outer?.Property("TargetFrameworks")).Select(Tfm.Normalize).OfType<string>()));
        var sdk = ProjectKindDetector.SdkName(first.IsTrue);
        var packages = PackageReferences(all);
        var assemblies = AssemblyReferences(all, projectDirectory, context);
        var localDirectory = Path.GetDirectoryName(RepoPaths.ToAbsolute(context.Paths.RepositoryRoot, projectId))!;
        var facts = new ProjectFacts
        {
            Sdk = sdk,
            OutputType = first.Property("OutputType"),
            IsTestProject = all.Any(e => e.IsTrue("IsTestProject")),
            UseWpf = all.Any(e => e.IsTrue("UseWPF")),
            UseWindowsForms = all.Any(e => e.IsTrue("UseWindowsForms")),
            PackageIds = packages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            AssemblyReferences = assemblies.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ProjectTypeGuids = first.Property("ProjectTypeGuids"),
            HasWebConfig = File.Exists(Path.Combine(localDirectory, "web.config")) || File.Exists(Path.Combine(localDirectory, "Web.config")),
        };
        var (kind, evidence) = ProjectKindDetector.Detect(facts);
        var projectConfig = context.Config.Projects.FirstOrDefault(p =>
            string.Equals(RepoPaths.Normalize(p.Path), projectId, StringComparison.OrdinalIgnoreCase));
        ProjectKind? kindOverride = Enum.TryParse<ProjectKind>(projectConfig?.Kind, ignoreCase: true, out var parsed) ? parsed : null;

        var outputDirectories = OutputDirectories(all, projectDirectory, context);
        var compile = all
            .SelectMany(e => e.ItemsOf("Compile"))
            .Select(i => context.Paths.ToRelative(projectDirectory, i.Include))
            .OfType<string>()
            .Where(path => !outputDirectories.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var calls = new SortedDictionary<string, CompilerCallRef>(StringComparer.Ordinal);
        foreach (var tfm in tfms)
        {
            if (context.CompilerCalls.TryGetValue((projectId, tfm), out var call))
            {
                calls[tfm] = call;
            }
        }

        var language = Language(projectFile);
        return new ProjectInfo
        {
            Id = projectId,
            Name = Path.GetFileNameWithoutExtension(projectId),
            AssemblyName = first.Property("AssemblyName"),
            RootNamespace = first.Property("RootNamespace"),
            Language = language,
            Kind = kindOverride ?? kind,
            KindEvidence = kindOverride is null ? evidence : "offramp.yml",
            SdkStyle = first.IsTrue("UsingMicrosoftNETSdk"),
            Sdk = sdk,
            TargetFrameworks = tfms,
            FrameworkClass = Tfm.Classify(tfms),
            OutputType = first.Property("OutputType") ?? "Library",
            IsTestProject = facts.IsTestProject,
            Properties = Properties(all, context),
            DefineConstants = DefineConstants(projectId, inner, context),
            WindowsOnlyBuildSteps = [.. WindowsOnlyBuildSteps.Detect(projectFile, evaluations).Select(s => s.Id)],
            PackagesConfig = File.Exists(Path.Combine(localDirectory, "packages.config")),
            Compile = compile,
            CompileExplicit = !first.IsTrue("UsingMicrosoftNETSdk")
                || string.Equals(first.Property("EnableDefaultCompileItems"), "false", StringComparison.OrdinalIgnoreCase),
            ProjectReferences = [.. all
                .SelectMany(e => e.ItemsOf("ProjectReference"))
                .Select(i => context.Paths.ToRelative(projectDirectory, i.Include))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            PackageReferences = packages,
            AssemblyReferences = assemblies,
            ComReferences = [.. all
                .SelectMany(e => e.ItemsOf("COMReference"))
                .GroupBy(i => i.Include, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ComReferenceInfo
                {
                    Name = g.Key,
                    Guid = g.First().Get("Guid"),
                    EmbedInteropTypes = string.Equals(g.First().Get("EmbedInteropTypes"), "true", StringComparison.OrdinalIgnoreCase),
                })
                .OrderBy(c => c.Name, StringComparer.Ordinal)],
            InternalsVisibleTo = [.. all
                .SelectMany(e => e.ItemsOf("InternalsVisibleTo"))
                .Select(i => i.Include)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)],
            Resolved = Resolved(all, tfms, context),
            CompilerCalls = calls,
            Loc = CountLines(compile, context.Paths.RepositoryRoot),
            Partial = language is "csharp" or "vb" && tfms.Any(t => !calls.ContainsKey(t)),
            Config = new ProjectConfigState
            {
                KindOverride = kindOverride,
                Excluded = context.Excluded.Matches(projectId),
                Frozen = projectConfig?.Frozen ?? false,
            },
        };
    }

    public static string Language(string projectFile) => Path.GetExtension(projectFile).ToLowerInvariant() switch
    {
        ".csproj" => "csharp",
        ".vbproj" => "vb",
        ".fsproj" => "fsharp",
        _ => "other",
    };

    /// <summary>Lines in the compile items, counted from the files on disk.</summary>
    public static int CountLines(IEnumerable<string> files, string repositoryRoot)
    {
        var total = 0;
        foreach (var file in files)
        {
            var path = RepoPaths.ToAbsolute(repositoryRoot, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                continue;
            }

            var lines = bytes.Count(b => b == (byte)'\n');
            total += bytes[^1] == (byte)'\n' ? lines : lines + 1;
        }

        return total;
    }

    private static SortedDictionary<string, string> Properties(IReadOnlyList<EvaluatedProject> evaluations, ProjectBuildContext context)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in RecordedProperties)
        {
            var value = evaluations.Select(e => e.Property(name)).FirstOrDefault(v => v is not null);
            if (value is null
                || (name == "DirectoryPackagesPropsPath" && !evaluations.Any(e => e.IsTrue("ManagePackageVersionsCentrally"))))
            {
                continue;
            }

            result[name] = name == "DirectoryPackagesPropsPath" ? context.Paths.ToRelative(value) ?? Path.GetFileName(value) : value;
        }

        return result;
    }

    /// <summary>
    /// The symbols the compiler saw when there is a compiler call (they include the
    /// SDK's implicit ones, such as NETFRAMEWORK); otherwise the evaluated property.
    /// </summary>
    private static SortedDictionary<string, IReadOnlyList<string>> DefineConstants(
        string projectId, IEnumerable<EvaluatedProject> inner, ProjectBuildContext context)
    {
        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var e in inner)
        {
            var tfm = e.TargetFramework!;
            result[tfm] = context.CompilerDefines.TryGetValue((projectId, tfm), out var defines)
                ? [.. defines.Distinct(StringComparer.Ordinal)]
                : [.. SplitList(e.Property("DefineConstants")).Distinct(StringComparer.Ordinal)];
        }

        return result;
    }

    private static IReadOnlyList<PackageReferenceInfo> PackageReferences(IReadOnlyList<EvaluatedProject> evaluations)
    {
        var byId = new SortedDictionary<string, (EvaluatedItem Item, string? CentralVersion, SortedSet<string> Tfms)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in evaluations)
        {
            var central = e.ItemsOf("PackageVersion")
                .GroupBy(i => i.Include, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Get("Version"), StringComparer.OrdinalIgnoreCase);
            foreach (var item in e.ItemsOf("PackageReference"))
            {
                if (string.Equals(item.Get("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!byId.TryGetValue(item.Include, out var entry))
                {
                    entry = (item, central.GetValueOrDefault(item.Include), new SortedSet<string>(StringComparer.Ordinal));
                    byId[item.Include] = entry;
                }

                if (e.TargetFramework is not null)
                {
                    entry.Tfms.Add(e.TargetFramework);
                }
            }
        }

        return [.. byId.Values.Select(v => new PackageReferenceInfo
        {
            Id = v.Item.Include,
            Version = v.Item.Get("Version") ?? v.CentralVersion,
            VersionOverride = v.Item.Get("VersionOverride"),
            PrivateAssets = v.Item.Get("PrivateAssets"),
            Tfms = Tfm.Sort(v.Tfms),
        })];
    }

    private static IReadOnlyList<AssemblyReferenceInfo> AssemblyReferences(
        IReadOnlyList<EvaluatedProject> evaluations, string projectDirectory, ProjectBuildContext context)
    {
        var byName = new SortedDictionary<string, AssemblyReferenceInfo>(StringComparer.OrdinalIgnoreCase);
        var implicitReferences = evaluations
            .SelectMany(e => e.ItemsOf("_SDKImplicitReference"))
            .Select(i => i.Include)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in evaluations.SelectMany(e => e.ItemsOf("Reference")))
        {
            if (IsImplicitReference(item, implicitReferences))
            {
                continue;
            }

            // A Reference may name a file directly (Include="..\lib\Foo.dll"); the file is
            // then its hint path and the file name its assembly name.
            var includeIsPath = IsFilePath(item.Include);
            var hintPath = item.Get("HintPath") ?? (includeIsPath ? item.Include : null);
            var name = includeIsPath
                ? Path.GetFileNameWithoutExtension(item.Include.Replace('\\', '/'))
                : item.Include.Split(',')[0].Trim();
            if (byName.ContainsKey(name))
            {
                continue;
            }

            if (hintPath is null)
            {
                byName[name] = new AssemblyReferenceInfo { Name = name, Kind = AssemblyReferenceKind.Framework };
                continue;
            }

            var relative = context.Paths.ToRelative(projectDirectory, hintPath);
            var local = relative is null ? null : RepoPaths.ToAbsolute(context.Paths.RepositoryRoot, relative);
            byName[name] = new AssemblyReferenceInfo
            {
                Name = name,
                HintPath = relative ?? Path.GetFileName(hintPath.Replace('\\', '/')),
                Kind = AssemblyReferenceKind.File,
                Metadata = local is not null && File.Exists(local) ? AssemblyFileInspector.Inspect(local) : null,
            };
        }

        return [.. byName.Values];
    }

    /// <summary>
    /// The project's intermediate and output directories ("src/Foo/obj/", "src/Foo/bin/"), as
    /// repository-relative prefixes. Compile items there are generated during the build
    /// (AssemblyInfo, global usings); some logs record them with the evaluation's items.
    /// </summary>
    private static List<string> OutputDirectories(IReadOnlyList<EvaluatedProject> evaluations, string projectDirectory, ProjectBuildContext context)
    {
        var names = new[] { "BaseIntermediateOutputPath", "IntermediateOutputPath", "BaseOutputPath", "OutputPath" };
        var values = evaluations.SelectMany(e => names.Select(e.Property)).OfType<string>()
            .Concat(["obj/", "bin/"]);
        var project = (context.Paths.ToRelative(projectDirectory) ?? "") + "/";
        return
        [
            .. values
                .Select(v => context.Paths.ToRelative(projectDirectory, v))
                .OfType<string>()
                .Select(v => v.TrimEnd('/') + "/")
                // A directory that holds the project itself is not an output directory to filter by.
                .Where(v => v != "/" && !project.StartsWith(v, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Implicit references: SDK-defined ones, mscorlib, and references a package's build
    /// targets inject (NuGetPackageId metadata, or Pack=false without a HintPath). Those vary
    /// with the machine (the reference-assemblies package on macOS/Linux, the targeting pack
    /// on Windows, NETStandard.Library's facades) and are the package's, not the project's.
    /// </summary>
    private static bool IsImplicitReference(EvaluatedItem item, HashSet<string> implicitReferences)
    {
        var hasHint = item.Get("HintPath") is not null;
        return string.Equals(item.Get("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)
            || item.Get("NuGetPackageId") is not null
            || (!hasHint && implicitReferences.Contains(item.Include))
            || (!hasHint && string.Equals(item.Get("Pack"), "false", StringComparison.OrdinalIgnoreCase))
            || string.Equals(item.Include, "mscorlib", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFilePath(string include) =>
        include.Contains('/', StringComparison.Ordinal)
        || include.Contains('\\', StringComparison.Ordinal)
        || include.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || include.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static SortedDictionary<string, ResolvedFramework> Resolved(
        IReadOnlyList<EvaluatedProject> evaluations, IReadOnlyList<string> tfms, ProjectBuildContext context)
    {
        var assetsFile = evaluations.Select(e => e.Property("ProjectAssetsFile")).FirstOrDefault(p => p is not null);
        var local = context.Paths.ToLocal(assetsFile);
        var all = AssetsFileReader.Read(local);
        var result = new SortedDictionary<string, ResolvedFramework>(StringComparer.Ordinal);
        foreach (var (tfm, resolved) in all)
        {
            var normalized = Tfm.Normalize(tfm);
            if (normalized is not null && tfms.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result[normalized] = resolved;
            }
        }

        return result;
    }

    internal static IEnumerable<string> SplitList(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
