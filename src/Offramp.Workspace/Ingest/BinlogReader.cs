using Microsoft.Build.Logging.StructuredLogger;
using LoggedProject = Microsoft.Build.Logging.StructuredLogger.Project;

namespace Offramp.Workspace.Ingest;

/// <summary>Everything Offramp reads from an MSBuild binary log.</summary>
public sealed record BinlogData
{
    /// <summary>One entry per project and target framework (plus the outer evaluation of multi-targeting projects).</summary>
    public required IReadOnlyList<EvaluatedProject> Evaluations { get; init; }

    public required IReadOnlyList<BuildError> Errors { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>The solution the build ran on, as recorded (absolute), or null.</summary>
    public string? SolutionPath { get; init; }

    public string? SdkVersion { get; init; }

    public string? RuntimeIdentifier { get; init; }

    /// <summary>
    /// Compilations whose compiler task logged an error, as captured: the compiler log records their calls,
    /// but what the compiler saw does not compile.
    /// </summary>
    public IReadOnlyList<FailedCompilation> FailedCompilations { get; init; } = [];

    /// <summary>
    /// Files a project's own build copied its assembly to (an <c>AfterBuild</c> copy into a site's <c>bin</c>, as
    /// DotNetNuke's modules do), as captured, sorted; the copy to its own output folder included.
    /// </summary>
    public IReadOnlyList<AssemblyCopy> AssemblyCopies { get; init; } = [];
}

/// <summary>A project's build copying its own assembly to <paramref name="Destination"/> (a file path, as captured).</summary>
public sealed record AssemblyCopy(string ProjectFile, string Destination);

/// <summary>A project and target framework (null when none is known) whose compiler task logged an error.</summary>
public sealed record FailedCompilation(string ProjectFile, string? TargetFramework);

/// <summary>Reads evaluations, items, targets, and errors from a binary log with MSBuild.StructuredLogger.</summary>
public static class BinlogReader
{
    private static readonly object ReadLock = new();

    /// <summary>Properties copied from each evaluation; the rest are dropped to keep large repositories small in memory.</summary>
    public static readonly IReadOnlySet<string> PropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TargetFramework", "TargetFrameworks", "TargetFrameworkIdentifier", "TargetFrameworkVersion", "TargetFrameworkMoniker",
        "OutputType", "AssemblyName", "RootNamespace",
        "UsingMicrosoftNETSdk", "UsingMicrosoftNETSdkWeb", "UsingMicrosoftNETSdkWorker", "UsingMicrosoftNETSdkRazor",
        "UsingMicrosoftNETSdkBlazorWebAssembly", "UsingMicrosoftNETSdkWindowsDesktop",
        "UseWPF", "UseWindowsForms", "IsTestProject", "IsPackable", "GenerateSerializationAssemblies",
        "ManagePackageVersionsCentrally", "DirectoryPackagesPropsPath", "ProjectTypeGuids", "LangVersion", "Nullable",
        "TreatWarningsAsErrors", "NoWarn", "DefineConstants", "ProjectAssetsFile", "PreBuildEvent", "PostBuildEvent",
        "TransformOnBuild", "EnableDefaultCompileItems", "NETCoreSdkVersion", "NETCoreSdkRuntimeIdentifier",
        "SolutionPath", "SqlServerVerification", "DSP", "ImplicitUsings", "ExcludeRestorePackageImports", "MSBuildRestoreSessionId",
        "EnableWindowsTargeting", "OfframpCompileOnly", "AdditionalExplicitAssemblyReferences", "MvcBuildViews", "BaseIntermediateOutputPath", "IntermediateOutputPath", "BaseOutputPath", "OutputPath",
        "GenerateResourceUsePreserializedResources", "SkipEnsureBindingRedirects",
        "MicrosoftCommonPropsHasBeenImported", "ImportDirectoryBuildProps", "DirectoryBuildPropsPath",
        "SignAssembly", "AssemblyOriginatorKeyFile", "DelaySign", "PublicSign",
        "NuGetPackageRoot",
        "OutDir",
    };

    /// <summary>Item types copied from each evaluation.</summary>
    public static readonly IReadOnlySet<string> ItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "ProjectReference", "PackageReference", "PackageVersion", "Reference", "COMReference", "COMFileReference",
        "EmbeddedResource", "None", "Content", "InternalsVisibleTo", "Analyzer", "EntityDeploy", "Fakes", "T4Template",
        "TextTemplate", "Build", "PostDeploy", "PreDeploy", "_SDKImplicitReference",
    };

    public static BinlogData Read(string binlogPath)
    {
        var build = ReadBuild(binlogPath);
        var evaluations = new List<ProjectEvaluation>();
        build.VisitAllChildren<ProjectEvaluation>(evaluations.Add);
        var projects = new List<LoggedProject>();
        build.VisitAllChildren<LoggedProject>(projects.Add);
        var errors = new List<Error>();
        build.VisitAllChildren<Error>(errors.Add);

        var targetsByEvaluation = projects
            .GroupBy(p => p.EvaluationId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g
                .SelectMany(p => p.Children.OfType<Target>())
                .Where(t => !t.Skipped)
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

        // Last build (non-restore) evaluation wins per project and target framework.
        var chosen = new Dictionary<(string File, string? Tfm), EvaluatedProject>();
        var tfmByEvaluation = new Dictionary<int, string?>();
        foreach (var evaluation in evaluations.OrderBy(e => e.Id))
        {
            if (evaluation.ProjectFile is null || evaluation.ProjectFile.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var properties = SelectProperties(evaluation);
            if (IsRestoreEvaluation(properties))
            {
                continue;
            }

            var tfm = Tfm.Normalize(Get(properties, "TargetFramework"))
                ?? (Get(properties, "TargetFrameworks") is null
                    ? Tfm.FromIdentifier(Get(properties, "TargetFrameworkIdentifier"), Get(properties, "TargetFrameworkVersion"))
                    : null);
            tfmByEvaluation[evaluation.Id] = tfm;
            chosen[(evaluation.ProjectFile, tfm)] = new EvaluatedProject
            {
                ProjectFile = evaluation.ProjectFile,
                TargetFramework = tfm,
                Properties = properties,
                Items = SelectItems(evaluation),
                Imports = SelectImports(evaluation),
                TargetsExecuted = targetsByEvaluation.GetValueOrDefault(evaluation.Id) ?? new HashSet<string>(),
            };
        }

        var any = chosen.Values.FirstOrDefault(e => e.Property("NETCoreSdkVersion") is not null);
        return new BinlogData
        {
            Evaluations = [.. chosen.Values.OrderBy(e => e.ProjectFile, StringComparer.Ordinal).ThenBy(e => e.TargetFramework, StringComparer.Ordinal)],
            Errors = [.. errors.Select(ToBuildError)],
            Succeeded = build.Succeeded,
            SolutionPath = chosen.Values.Select(e => e.Property("SolutionPath")).FirstOrDefault(p => p is not null && !p.Contains('*', StringComparison.Ordinal)),
            SdkVersion = any?.Property("NETCoreSdkVersion"),
            RuntimeIdentifier = any?.Property("NETCoreSdkRuntimeIdentifier"),
            FailedCompilations = FailedCompilations(errors, tfmByEvaluation),
            AssemblyCopies = AssemblyCopies(build, chosen.Values),
        };
    }

    /// <summary>Copies, by a project's own targets, of a file named after its assembly (<c>Name.dll</c>, <c>Name.exe</c>).</summary>
    private static List<AssemblyCopy> AssemblyCopies(Build build, IEnumerable<EvaluatedProject> evaluations)
    {
        var names = evaluations
            .Where(e => e.Property("AssemblyName") is not null)
            .GroupBy(e => e.ProjectFile, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Property("AssemblyName")!).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var copies = new List<CopyTask>();
        build.VisitAllChildren<CopyTask>(copies.Add);
        return
        [
            .. copies
                .Select(c => (Project: c.GetNearestParent<LoggedProject>()?.ProjectFile, Task: c))
                .Where(c => c.Project is not null && names.ContainsKey(c.Project))
                .SelectMany(c => c.Task.FileCopyOperations
                    .Where(o => o.Destination is not null && IsAssemblyOf(o.Source, names[c.Project!]))
                    .Select(o => new AssemblyCopy(c.Project!, o.Destination)))
                .Distinct()
                .OrderBy(c => c.ProjectFile, StringComparer.Ordinal)
                .ThenBy(c => c.Destination, StringComparer.Ordinal),
        ];
    }

    private static bool IsAssemblyOf(string? file, HashSet<string> assemblyNames) =>
        file is not null
        && Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".exe"
        && assemblyNames.Contains(Path.GetFileNameWithoutExtension(file.Replace('\\', '/')));

    private static readonly HashSet<string> CompilerTasks = new(StringComparer.OrdinalIgnoreCase) { "Csc", "Vbc", "Fsc" };

    /// <summary>The project and target framework of every compiler task that logged an error, sorted.</summary>
    private static List<FailedCompilation> FailedCompilations(IEnumerable<Error> errors, Dictionary<int, string?> tfmByEvaluation) =>
        [.. errors
            .Where(e => e.GetNearestParent<Microsoft.Build.Logging.StructuredLogger.Task>() is { } task && CompilerTasks.Contains(task.Name))
            .Select(e => e.GetNearestParent<LoggedProject>())
            .OfType<LoggedProject>()
            .Where(p => p.ProjectFile is not null)
            .Select(p => new FailedCompilation(
                p.ProjectFile,
                tfmByEvaluation.TryGetValue(p.EvaluationId, out var tfm) ? tfm : Tfm.Normalize(p.TargetFramework)))
            .Distinct()
            .OrderBy(f => f.ProjectFile, StringComparer.Ordinal)
            .ThenBy(f => f.TargetFramework, StringComparer.Ordinal)];

    /// <summary>
    /// Reads the log. <c>BinaryLog.ReadBuild</c> returns its result through a static field
    /// (<c>StructuredLogger.CurrentBuild</c>), so concurrent reads in one process can lose
    /// each other's builds; reads are serialized. A read that still fails comes back as a
    /// build with only an error node, which is turned into an exception.
    /// </summary>
    private static Build ReadBuild(string binlogPath)
    {
        Build build;
        lock (ReadLock)
        {
            build = BinaryLog.ReadBuild(binlogPath);
        }

        if (!build.Children.OfType<TimedNode>().Any() && build.Children.OfType<Error>().FirstOrDefault() is { } error)
        {
            throw new InvalidDataException(error.Text);
        }

        return build;
    }

    private static bool IsRestoreEvaluation(IReadOnlyDictionary<string, string> properties) =>
        string.Equals(Get(properties, "ExcludeRestorePackageImports"), "true", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> SelectProperties(ProjectEvaluation evaluation)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in evaluation.GetProperties())
        {
            if (PropertyNames.Contains(name))
            {
                result[name] = value ?? "";
            }
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<EvaluatedItem>> SelectItems(ProjectEvaluation evaluation)
    {
        var result = new Dictionary<string, IReadOnlyList<EvaluatedItem>>(StringComparer.OrdinalIgnoreCase);
        var itemsFolder = evaluation.Children.OfType<Folder>().FirstOrDefault(f => f.Name == "Items");
        if (itemsFolder is null)
        {
            return result;
        }

        foreach (var group in itemsFolder.Children.OfType<NamedNode>())
        {
            if (!ItemTypes.Contains(group.Name) || group is not TreeNode tree)
            {
                continue;
            }

            var items = new List<EvaluatedItem>();
            foreach (var item in tree.Children.OfType<Item>())
            {
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in item.Children.OfType<Metadata>())
                {
                    metadata[m.Name] = m.Value ?? "";
                }

                items.Add(new EvaluatedItem(item.Text ?? item.Name ?? "", metadata));
            }

            result[group.Name] = items;
        }

        return result;
    }

    private static List<string> SelectImports(ProjectEvaluation evaluation)
    {
        var imports = new List<string>();
        foreach (var import in evaluation.GetAllImportsTransitive())
        {
            if (import is Import i && i.ImportedProjectFilePath is { Length: > 0 } path)
            {
                imports.Add(path);
            }
        }

        return imports;
    }

    private static BuildError ToBuildError(Error error) => new(
        error.Code ?? "",
        error.Text ?? "",
        error.ProjectFile,
        error.File,
        error.LineNumber > 0 ? error.LineNumber : null,
        error.ColumnNumber > 0 ? error.ColumnNumber : null);

    private static string? Get(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}
