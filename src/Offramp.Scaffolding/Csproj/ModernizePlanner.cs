using System.Text.Json.Nodes;
using System.Xml.Linq;
using NuGet.Versioning;
using Offramp.Analysis.Compilations;
using Offramp.Analyzers.CodeFixes;
using Offramp.Core.Diagnostics;
using Offramp.Core.Git;
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

    /// <summary>Tells git-ignored (generated) AssemblyInfo files, which are left alone; null when there is no git to ask.</summary>
    public IGitService? Git { get; init; }
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
        var flowing = FlowingPackages(request);
        foreach (var project in request.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            results.Add(project.SdkStyle
                ? Hygiene(request, project, changeSet)
                : await ConvertAsync(request, project, changeSet, central, flowing, cancellationToken));
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
        SortedDictionary<string, (byte[] Before, ProjectFileEditor Editor)> central, Dictionary<string, List<FlowingPackage>> flowing, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        if (project.Language != "csharp")
        {
            return Refuse(request, project, $"a {project.Language} project; only C# projects are converted.");
        }

        var packagesConfig = PackagesConfigPath(project);
        var (packages, raised) = Raise(request.Model, project, project.PackagesConfig ? ReadPackagesConfig(RepoPaths.ToAbsolute(root, packagesConfig)) : [], flowing);

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
                Git = request.Git,
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
            DisableTransitiveProjectReferences = ReferencesFlowOn(request.Model, project),
        });
        if (output.Refused is { } reason)
        {
            return Refuse(request, project, reason);
        }

        ReportRaised(request, project, raised);

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
            request.Diagnostics.Report(DiagnosticCatalog.Find(note.Code)!, note.Message, new DiagnosticLocation(project.Id));
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

    /// <summary>
    /// True when an SDK-style build of the project would reference more projects than the legacy
    /// one did: a referenced project has project references of its own (or is not in the model, so
    /// nobody knows). The SDK passes those on; a legacy project compiled against its direct
    /// references only (docs/decisions/0043-modernize-keeps-the-build-inputs.md).
    /// </summary>
    private static bool ReferencesFlowOn(WorkspaceModel model, ProjectInfo project) =>
        project.ProjectReferences.Any(reference => Find(model, reference) is not { } referenced || referenced.ProjectReferences.Count > 0);

    /// <summary>A package a project passes on to the projects that reference it once it restores the PackageReference way.</summary>
    private sealed record FlowingPackage(string Id, string Version, NuGetVersion Parsed);

    /// <summary>
    /// What each project passes on once this run is applied: a project converted now, its
    /// packages.config entries; a project that restores the PackageReference way already, its
    /// package references. Development dependencies and <c>PrivateAssets="all"</c> stay put. A
    /// packages.config project that stays legacy has no entry: it passes nothing on.
    /// </summary>
    private static Dictionary<string, List<FlowingPackage>> FlowingPackages(ModernizeRequest request)
    {
        var converted = request.Projects.Where(p => !p.SdkStyle).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, List<FlowingPackage>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in request.Model.Projects.Where(p => !p.PackagesConfig || converted.Contains(p.Id)))
        {
            IEnumerable<(string Id, string? Version)> packages = project.PackagesConfig
                ? ReadPackagesConfig(RepoPaths.ToAbsolute(request.RepositoryRoot, PackagesConfigPath(project))).Where(p => !p.DevelopmentDependency).Select(p => (p.Id, (string?)p.Version))
                : project.PackageReferences.Where(p => !string.Equals(p.PrivateAssets, "all", StringComparison.OrdinalIgnoreCase)).Select(p => (p.Id, p.Version));
            result[project.Id] = [.. packages
                .Select(p => (p.Id, p.Version, Parsed: NuGetVersion.TryParse(p.Version, out var parsed) ? parsed : null))
                .Where(p => p.Parsed is not null)
                .Select(p => new FlowingPackage(p.Id, p.Version!, p.Parsed!))];
        }

        return result;
    }

    /// <summary>A packages.config entry raised to the version a referenced project passes on.</summary>
    private sealed record RaisedPackage(PackagesConfigEntry Entry, FlowingPackage To, string From);

    /// <summary>
    /// The project's packages.config entries, each raised to the highest version a project in its
    /// ProjectReference closure passes on: PackageReference would bring that version in, and the lower
    /// direct one would be a package downgrade (NU1605, an error). Returns the raised ones too.
    /// </summary>
    private static (List<PackagesConfigEntry> Packages, List<RaisedPackage> Raised) Raise(WorkspaceModel model, ProjectInfo project, List<PackagesConfigEntry> packages,
        Dictionary<string, List<FlowingPackage>> flowing)
    {
        var raised = new List<RaisedPackage>();
        if (packages.Count == 0)
        {
            return (packages, raised);
        }

        var highest = new Dictionary<string, (FlowingPackage Package, string From)>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Closure(model, project, flowing))
        {
            foreach (var package in flowing.GetValueOrDefault(id) ?? [])
            {
                if (!highest.TryGetValue(package.Id, out var current) || package.Parsed > current.Package.Parsed)
                {
                    highest[package.Id] = (package, id);
                }
            }
        }

        var result = new List<PackagesConfigEntry>();
        foreach (var entry in packages)
        {
            if (entry.DevelopmentDependency || !highest.TryGetValue(entry.Id, out var higher)
                || !NuGetVersion.TryParse(entry.Version, out var own) || own >= higher.Package.Parsed)
            {
                result.Add(entry);
                continue;
            }

            raised.Add(new RaisedPackage(entry, higher.Package, higher.From));
            result.Add(entry with { Version = higher.Package.Version });
        }

        return (result, raised);
    }

    /// <summary><c>OFR4307</c> for each raised package of a project the conversion converts.</summary>
    private static void ReportRaised(ModernizeRequest request, ProjectInfo project, List<RaisedPackage> raised)
    {
        foreach (var (entry, to, from) in raised)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4307,
                $"{project.Id} asks for {entry.Id} {entry.Version}, and {from} brings {to.Version}. With PackageReference the higher version flows in, and the lower direct one would be a package downgrade (NU1605), so the converted project asks for {to.Version}.",
                new DiagnosticLocation(project.Id),
                [
                    KeyValuePair.Create<string, JsonNode?>("package", entry.Id),
                    KeyValuePair.Create<string, JsonNode?>("from", entry.Version),
                    KeyValuePair.Create<string, JsonNode?>("to", to.Version),
                    KeyValuePair.Create<string, JsonNode?>("source", from),
                ]);
        }
    }

    /// <summary>
    /// The projects a project's packages can come from, in order: its ProjectReference closure through
    /// projects that pass packages on (a packages.config project that stays legacy ends the chain).
    /// </summary>
    private static List<string> Closure(WorkspaceModel model, ProjectInfo project, Dictionary<string, List<FlowingPackage>> flowing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.Id };
        var order = new List<string>();
        var queue = new Queue<string>(project.ProjectReferences.Order(StringComparer.Ordinal));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id) || Find(model, id) is not { } referenced)
            {
                continue;
            }

            order.Add(referenced.Id);
            if (!flowing.ContainsKey(referenced.Id))
            {
                continue;
            }

            foreach (var next in referenced.ProjectReferences.Order(StringComparer.Ordinal))
            {
                queue.Enqueue(next);
            }
        }

        return order;
    }

    private static ProjectInfo? Find(WorkspaceModel model, string id) =>
        model.Projects.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string PackagesConfigPath(ProjectInfo project) =>
        Path.Combine(Path.GetDirectoryName(project.Id) ?? "", "packages.config").Replace('\\', '/');

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
