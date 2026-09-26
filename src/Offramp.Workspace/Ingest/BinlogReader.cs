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
}

/// <summary>Reads evaluations, items, targets, and errors from a binary log with MSBuild.StructuredLogger.</summary>
public static class BinlogReader
{
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
        "EnableWindowsTargeting", "OfframpCompileOnly", "BaseIntermediateOutputPath", "IntermediateOutputPath", "BaseOutputPath", "OutputPath",
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
        var build = BinaryLog.ReadBuild(binlogPath);
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
        };
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
