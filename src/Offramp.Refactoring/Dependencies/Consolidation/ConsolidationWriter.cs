using NuGet.Versioning;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Cpm;

namespace Offramp.Refactoring.Dependencies.Consolidation;

/// <summary>
/// Writes consolidation decisions into project files: versions edited in place; or, under
/// central package management (already in use, or <c>--cpm</c>), <c>PackageVersion</c> items
/// in the central file, references without versions, and <c>VersionOverride</c> for pinned
/// projects and packages outside the selection whose projects disagree.
/// </summary>
internal sealed class ConsolidationWriter(ConsolidateRequest request)
{
    private const string DefaultCentralFile = "Directory.Packages.props";

    private readonly SortedDictionary<string, (byte[]? Before, ProjectFileEditor Editor)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ConsolidationChange>> _changes = new(StringComparer.OrdinalIgnoreCase);

    private string Root => request.RepositoryRoot;

    private WorkspaceModel Model => request.Model;

    public IReadOnlyList<ConsolidationChange> ChangesFor(string id) =>
        _changes.TryGetValue(id, out var changes) ? [.. changes.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Kind)] : [];

    public (ChangeSet ChangeSet, CentralPackageManagement? Cpm, IReadOnlyList<CpmFinding> Hazards) Write(IReadOnlyList<PackageConsolidation> packages)
    {
        var projects = Model.Projects.Where(p => !p.PackagesConfig && p.PackageReferences.Count > 0).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var central = projects.Count > 0 && projects.All(p => p.Properties.TryGetValue("ManagePackageVersionsCentrally", out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        CentralPackageManagement? cpm = null;
        IReadOnlyList<CpmFinding> hazards = [];
        if (central)
        {
            cpm = WriteExisting(packages, projects);
        }
        else if (request.Cpm)
        {
            (cpm, hazards) = WriteConversion(packages, projects);
        }
        else
        {
            WriteInPlace(packages);
        }

        var changeSet = new ChangeSet();
        foreach (var (path, (before, editor)) in _files)
        {
            var after = editor.Save();
            if (before is null)
            {
                changeSet.Create(path, System.Text.Encoding.UTF8.GetString(after));
            }
            else
            {
                changeSet.Edit(path, before, after);
            }
        }

        return (changeSet, cpm, hazards);
    }

    /// <summary>Each project's own PackageReference Version becomes the selected version.</summary>
    private void WriteInPlace(IReadOnlyList<PackageConsolidation> packages)
    {
        foreach (var package in packages.Where(p => p.Selected is not null))
        {
            var pinned = package.Pinned.SelectMany(p => p.Projects).ToHashSet(StringComparer.Ordinal);
            foreach (var (version, projects) in package.Current)
            {
                if (version == package.Selected)
                {
                    continue;
                }

                foreach (var project in projects.Where(p => !pinned.Contains(p)))
                {
                    if (Editor(project).SetMetadata("PackageReference", package.Id, "Version", package.Selected) > 0)
                    {
                        Record(package.Id, project, ConsolidationChangeKind.SetVersion, version, package.Selected);
                    }
                }
            }
        }
    }

    /// <summary>The central file's PackageVersion becomes the selected version; pins get VersionOverride.</summary>
    private CentralPackageManagement WriteExisting(IReadOnlyList<PackageConsolidation> packages, List<ProjectInfo> projects)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var package in packages.Where(p => p.Selected is not null))
        {
            var users = package.Current.SelectMany(c => c.Projects).Concat(package.Pinned.SelectMany(p => p.Projects)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var file in users.Select(CentralFileOf).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                files.Add(file);
                var editor = Editor(file);
                var before = package.Current.Select(c => c.Version).FirstOrDefault();
                if (editor.SetMetadata("PackageVersion", package.Id, "Version", package.Selected) == 0)
                {
                    editor.AddPackageVersion(package.Id, package.Selected!);
                }

                Record(package.Id, file, ConsolidationChangeKind.PackageVersion, before, package.Selected);
            }

            foreach (var project in users)
            {
                var pin = package.Pinned.FirstOrDefault(p => p.Projects.Contains(project))?.Version;
                if (Editor(project).SetMetadata("PackageReference", package.Id, "VersionOverride", pin) > 0 && pin is not null)
                {
                    Record(package.Id, project, ConsolidationChangeKind.VersionOverride, null, pin);
                }
            }
        }

        return new CentralPackageManagement { Mode = "existing", File = files.FirstOrDefault() ?? DefaultCentralFile };
    }

    /// <summary>
    /// <c>--cpm</c>: every direct package of the PackageReference projects gets a PackageVersion
    /// (selected packages at their selected version; others at their version, or the highest with
    /// VersionOverride for projects that differ), references lose their versions, and a
    /// non-default central file is opted into.
    /// </summary>
    private (CentralPackageManagement, IReadOnlyList<CpmFinding>) WriteConversion(IReadOnlyList<PackageConsolidation> packages, List<ProjectInfo> projects)
    {
        var solutionDirectory = Model.Solution is { } solution && solution.Contains('/', StringComparison.Ordinal) ? solution[..solution.LastIndexOf('/')] : "";
        var directory = request.Config.Deps.Cpm.Scope == "repo" ? "" : solutionDirectory;
        var found = CpmHazards.Find(Root, [.. Model.Projects.Select(p => p.Id)]);
        var hazards = found.Select(h => new CpmFinding(h.Descriptor.Code, h.Path, h.Message)).ToList();
        foreach (var hazard in found)
        {
            request.Diagnostics.Report(hazard.Descriptor, hazard.Message, new DiagnosticLocation(hazard.Path.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? hazard.Path : null, hazard.Path));
        }

        // A default-named central file would reach projects outside the solution (OFR1301): name it after the solution and opt in.
        var name = request.Config.Deps.Cpm.File;
        if (name == DefaultCentralFile && found.Any(h => h.Descriptor == DiagnosticCatalog.OFR1301))
        {
            name = Path.GetFileNameWithoutExtension(Model.Solution ?? "Solution") + ".Packages.props";
        }

        var file = directory.Length == 0 ? name : directory + "/" + name;
        var central = Editor(file, create: true);
        central.SetProperty("ManagePackageVersionsCentrally", "true");

        var selected = packages.Where(p => p.Selected is not null).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var ids = projects.SelectMany(p => p.PackageReferences.Select(r => r.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var usage = Model.Packages.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
            var versions = usage?.Versions ?? [];
            var pinned = selected.TryGetValue(id, out var decision) ? decision.Pinned.SelectMany(p => p.Projects).ToHashSet(StringComparer.Ordinal) : [];
            var centralVersion = decision?.Selected
                ?? versions.Keys.Select(NuGetVersion.Parse).Max()?.ToNormalizedString();
            if (centralVersion is null)
            {
                continue;
            }

            central.AddPackageVersion(id, centralVersion);
            central.SetMetadata("PackageVersion", id, "Version", centralVersion);
            Record(id, file, ConsolidationChangeKind.PackageVersion, null, centralVersion);
            foreach (var (version, users) in versions)
            {
                foreach (var project in users.Where(u => projects.Any(p => p.Id == u)))
                {
                    var editor = Editor(project);
                    editor.SetMetadata("PackageReference", id, "Version", null);
                    var keep = pinned.Contains(project) ? decision!.Pinned.First(p => p.Projects.Contains(project)).Version
                        : decision is null && version != centralVersion ? version
                        : null;
                    editor.SetMetadata("PackageReference", id, "VersionOverride", keep);
                    Record(id, project, keep is null ? ConsolidationChangeKind.RemoveVersion : ConsolidationChangeKind.VersionOverride, version, keep);
                }
            }
        }

        // A non-default central file is opted into. A shared props file imported before the SDK's own
        // (--opt-in-via) names it with DirectoryPackagesPropsPath; a project body comes too late for that
        // property, so each project imports the file itself.
        var optIn = new List<string>();
        if (name != DefaultCentralFile)
        {
            if (request.OptInVia is { } via)
            {
                var shared = Editor(via, create: true);
                shared.SetProperty("ManagePackageVersionsCentrally", "true");
                shared.SetProperty("DirectoryPackagesPropsPath", "$(MSBuildThisFileDirectory)" + Relative(via, file));
                optIn.Add(via);
            }
            else
            {
                foreach (var project in projects)
                {
                    var editor = Editor(project.Id);
                    editor.SetProperty("ManagePackageVersionsCentrally", "true");
                    editor.AddImport("$(MSBuildThisFileDirectory)" + Relative(project.Id, file));
                    optIn.Add(project.Id);
                }
            }
        }

        return (new CentralPackageManagement { Mode = "convert", File = file, OptIn = optIn }, hazards);
    }

    /// <summary>The nearest Directory.Packages.props at or above a project's folder, repository-relative.</summary>
    private string? CentralFileOf(string project)
    {
        var directory = project.Contains('/', StringComparison.Ordinal) ? project[..project.LastIndexOf('/')] : "";
        while (true)
        {
            var candidate = directory.Length == 0 ? DefaultCentralFile : directory + "/" + DefaultCentralFile;
            if (File.Exists(RepoPaths.ToAbsolute(Root, candidate)))
            {
                return candidate;
            }

            if (directory.Length == 0)
            {
                return null;
            }

            directory = directory.Contains('/', StringComparison.Ordinal) ? directory[..directory.LastIndexOf('/')] : "";
        }
    }

    private ProjectFileEditor Editor(string path, bool create = false)
    {
        if (_files.TryGetValue(path, out var entry))
        {
            return entry.Editor;
        }

        var absolute = RepoPaths.ToAbsolute(Root, path);
        byte[]? before = File.Exists(absolute) ? File.ReadAllBytes(absolute) : null;
        if (before is null && !create)
        {
            throw new FileNotFoundException($"{path} does not exist.", absolute);
        }

        var editor = ProjectFileEditor.Load(before ?? "<Project>\n</Project>\n"u8.ToArray());
        _files[path] = (before, editor);
        return editor;
    }

    private void Record(string id, string file, ConsolidationChangeKind kind, string? from, string? to)
    {
        if (!_changes.TryGetValue(id, out var changes))
        {
            changes = [];
            _changes[id] = changes;
        }

        if (!changes.Contains(new ConsolidationChange(file, kind, from, to)))
        {
            changes.Add(new ConsolidationChange(file, kind, from, to));
        }
    }

    /// <summary>A repository path relative to a file's folder, with backslashes as MSBuild files write them.</summary>
    private static string Relative(string fromFile, string to)
    {
        var slash = fromFile.LastIndexOf('/');
        var fromParts = slash < 0 ? [] : fromFile[..slash].Split('/');
        var toParts = to.Split('/');
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length - 1 && string.Equals(fromParts[common], toParts[common], StringComparison.Ordinal))
        {
            common++;
        }

        return string.Join('\\', Enumerable.Repeat("..", fromParts.Length - common).Concat(toParts.Skip(common)));
    }
}
