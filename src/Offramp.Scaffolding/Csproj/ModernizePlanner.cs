using System.Xml.Linq;
using Offramp.Analysis.Compilations;
using Offramp.Analyzers.CodeFixes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.Codemods;
using Offramp.Refactoring.ProjectFiles;
using Catalog = Offramp.Analyzers.Codemods;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Csproj;

/// <summary>What <c>csproj modernize</c> is asked to do.</summary>
public sealed record ModernizeRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required IReadOnlyList<ProjectInfo> Projects { get; init; }

    /// <summary><c>--tfm</c>; empty keeps each project's frameworks.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary><c>--nullable</c>, else null.</summary>
    public string? Nullable { get; init; }

    public required CompilationLoader Loader { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;
}

/// <summary>The dry run and the change set that applies it.</summary>
public sealed record ModernizePlan(ModernizeResult Result, ChangeSet ChangeSet);

/// <summary>
/// Plans <c>csproj modernize</c> (docs/spec/commands/scaffold.md#csproj-modernize): legacy
/// projects become SDK-style (<see cref="LegacyProjectConverter"/>), with packages.config
/// turned into PackageReference items and the AssemblyInfo attributes the SDK generates
/// removed by the <c>assemblyinfo</c> codemod; every project gets the requested target
/// frameworks and nullable setting.
/// </summary>
public static class ModernizePlanner
{
    private static readonly Dictionary<string, string> AsSdkProject = new(StringComparer.Ordinal)
    {
        ["build_property.UsingMicrosoftNETSdk"] = "true",
        ["build_property.GenerateAssemblyInfo"] = "true",
    };

    public static async Task<ModernizePlan> PlanAsync(ModernizeRequest request, CancellationToken cancellationToken)
    {
        var changeSet = new ChangeSet();
        var central = new SortedDictionary<string, (byte[] Before, ProjectFileEditor Editor)>(StringComparer.Ordinal);
        var results = new List<ModernizedProject>();
        foreach (var project in request.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            results.Add(project.SdkStyle
                ? Hygiene(request, project, changeSet)
                : await ConvertAsync(request, project, changeSet, central, cancellationToken));
        }

        foreach (var (path, (before, editor)) in central)
        {
            changeSet.Edit(path, before, editor.Save());
        }

        return new ModernizePlan(new ModernizeResult { Projects = results, Preview = changeSet.IsEmpty ? null : changeSet.Preview() }, changeSet);
    }

    /// <summary>An SDK-style project: the requested target frameworks and nullable setting.</summary>
    private static ModernizedProject Hygiene(ModernizeRequest request, ProjectInfo project, ChangeSet changeSet)
    {
        var path = RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id);
        var before = File.ReadAllBytes(path);
        var editor = ProjectFileEditor.Load(before);
        var frameworks = project.TargetFrameworks;
        if (request.TargetFrameworks.Count > 0 && !request.TargetFrameworks.SequenceEqual(project.TargetFrameworks))
        {
            frameworks = request.TargetFrameworks;
            editor.RemoveProperty("TargetFramework");
            editor.RemoveProperty("TargetFrameworks");
            if (frameworks.Count == 1)
            {
                editor.SetProperty("TargetFramework", frameworks[0]);
            }
            else
            {
                editor.SetProperty("TargetFrameworks", string.Join(';', frameworks));
            }
        }

        if (request.Nullable is { } nullable)
        {
            editor.SetProperty("Nullable", nullable);
        }

        var after = editor.Save();
        changeSet.Edit(project.Id, before, after);
        return new ModernizedProject
        {
            Project = project.Id,
            Style = "sdk",
            Changed = !before.AsSpan().SequenceEqual(after),
            TargetFrameworks = frameworks,
        };
    }

    private static async Task<ModernizedProject> ConvertAsync(ModernizeRequest request, ProjectInfo project, ChangeSet changeSet,
        SortedDictionary<string, (byte[] Before, ProjectFileEditor Editor)> central, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        if (project.Language != "csharp")
        {
            return Refuse(request, project, $"a {project.Language} project; only C# projects are converted.");
        }

        var packagesConfig = Path.Combine(Path.GetDirectoryName(project.Id) ?? "", "packages.config").Replace('\\', '/');
        var packages = project.PackagesConfig ? ReadPackagesConfig(RepoPaths.ToAbsolute(root, packagesConfig)) : [];

        // AssemblyInfo: the attributes the SDK generates go, their values become properties.
        var properties = new List<CodemodPropertyEdit>();
        var sourceEdits = new List<FileEdit>();
        if (project.CompilerCalls.Count > 0)
        {
            var assemblyInfo = await CodemodRunner.PlanAsync(new CodemodRequest
            {
                RepositoryRoot = root,
                Model = request.Model,
                Projects = [project],
                Codemods = [CodemodRegistry.For(Catalog.AssemblyInfo)],
                Loader = request.Loader,
                Diagnostics = request.Diagnostics,
                PropertyOverrides = AsSdkProject,
            }, cancellationToken);
            properties.AddRange(assemblyInfo.Result.Projects.SelectMany(p => p.Properties));
            sourceEdits.AddRange(assemblyInfo.ChangeSet.Edits.Where(e => e.Path != project.Id));
        }

        var centralVersions = project.Properties.TryGetValue("ManagePackageVersionsCentrally", out var cpm) && string.Equals(cpm, "true", StringComparison.OrdinalIgnoreCase);
        var before = File.ReadAllBytes(RepoPaths.ToAbsolute(root, project.Id));
        var output = LegacyProjectConverter.Convert(new ConversionInput
        {
            RepositoryRoot = root,
            Project = project,
            Bytes = before,
            Packages = packages,
            TargetFrameworks = request.TargetFrameworks,
            CentralVersions = centralVersions,
            Nullable = request.Nullable,
            Properties = properties,
        });
        if (output.Refused is { } reason)
        {
            return Refuse(request, project, reason);
        }

        changeSet.Edit(project.Id, before, output.Bytes!);
        var files = new List<string>();
        foreach (var edit in sourceEdits)
        {
            changeSet.Edit(edit.Path, edit.Before, edit.After);
            files.Add(edit.Path);
        }

        if (packages.Count > 0)
        {
            changeSet.Delete(root, packagesConfig);
            files.Add(packagesConfig);
            if (centralVersions && CentralFile(root, project) is { } props)
            {
                if (!central.TryGetValue(props, out var entry))
                {
                    var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(root, props));
                    entry = (bytes, ProjectFileEditor.Load(bytes));
                    central[props] = entry;
                }

                foreach (var package in packages)
                {
                    entry.Editor.AddPackageVersion(package.Id, package.Version);
                }

                files.Add(props);
            }
        }

        foreach (var note in output.Notes)
        {
            request.Diagnostics.Report(note.Code == "OFR4301" ? DiagnosticCatalog.OFR4301 : DiagnosticCatalog.OFR4302, note.Message, new DiagnosticLocation(project.Id));
        }

        return new ModernizedProject
        {
            Project = project.Id,
            Style = "legacy",
            Changed = true,
            TargetFrameworks = output.TargetFrameworks,
            CompileItems = output.CompileItems,
            Packages = [.. packages.Select(p => new ModernizedPackage(p.Id, p.Version, p.DevelopmentDependency))],
            Properties = properties,
            Dropped = output.Dropped,
            Files = [.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
    }

    private static ModernizedProject Refuse(ModernizeRequest request, ProjectInfo project, string reason)
    {
        request.Diagnostics.Report(DiagnosticCatalog.OFR4304, $"{project.Id} is not converted: {reason}", new DiagnosticLocation(project.Id));
        return new ModernizedProject { Project = project.Id, Style = "legacy", Skipped = reason, TargetFrameworks = project.TargetFrameworks };
    }

    public static List<PackagesConfigEntry> ReadPackagesConfig(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. XDocument.Load(path).Root!.Elements("package")
            .Select(p => new PackagesConfigEntry(
                p.Attribute("id")?.Value ?? "",
                p.Attribute("version")?.Value ?? "",
                string.Equals(p.Attribute("developmentDependency")?.Value, "true", StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.Id.Length > 0 && p.Version.Length > 0)];
    }

    private static string? CentralFile(string root, ProjectInfo project)
    {
        if (project.Properties.TryGetValue("DirectoryPackagesPropsPath", out var recorded) && File.Exists(RepoPaths.ToAbsolute(root, recorded)))
        {
            return recorded;
        }

        for (var folder = Path.GetDirectoryName(project.Id.Replace('\\', '/')); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = folder.Length == 0 ? "Directory.Packages.props" : folder.Replace('\\', '/') + "/Directory.Packages.props";
            if (File.Exists(RepoPaths.ToAbsolute(root, candidate)))
            {
                return candidate;
            }

            if (folder.Length == 0)
            {
                break;
            }
        }

        return null;
    }
}
