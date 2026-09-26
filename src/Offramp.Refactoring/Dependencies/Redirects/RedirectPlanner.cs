using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Serialization;
using NuGet.Configuration;
using NuGet.Frameworks;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;

namespace Offramp.Refactoring.Dependencies.Redirects;

[JsonConverter(typeof(CamelCaseEnumConverter<RedirectAction>))]
public enum RedirectAction
{
    Unchanged,
    Added,
    Changed,
    Pruned,

    /// <summary>For an assembly no package in the graph provides; kept (pass <c>--prune</c>).</summary>
    Stale,
}

public sealed record RedirectEntry
{
    public required string Assembly { get; init; }

    public string? PublicKeyToken { get; init; }

    public string? Culture { get; init; }

    /// <summary>The range after the sync (for pruned and stale entries, as it is).</summary>
    public string? OldVersion { get; init; }

    public string? NewVersion { get; init; }

    public required RedirectAction Action { get; init; }

    /// <summary>For a changed entry, the version it redirected to before.</summary>
    public string? Was { get; init; }

    /// <summary>The versions of the assembly that assemblies in the graph reference, plus the one deployed.</summary>
    public IReadOnlyList<string> Referenced { get; init; } = [];
}

public sealed record AppRedirects
{
    public required string Project { get; init; }

    /// <summary>The configuration file, repository-relative; null when the project has none.</summary>
    public string? ConfigFile { get; init; }

    public required string TargetFramework { get; init; }

    public required IReadOnlyList<RedirectEntry> Redirects { get; init; }

    /// <summary>Why the project was left alone, or null.</summary>
    public string? Skipped { get; init; }
}

public sealed record RedirectSummary(int Added, int Changed, int Pruned, int Stale, int Unchanged);

/// <summary>The <c>result</c> of <c>offramp redirects sync</c> (<c>schemas/v1/redirects-sync.json</c>).</summary>
public sealed record RedirectsResult
{
    public required IReadOnlyList<AppRedirects> Apps { get; init; }

    public required RedirectSummary Summary { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record RedirectsRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary>Application projects to sync; null for every .NET Framework application.</summary>
    public IReadOnlyList<string>? Apps { get; init; }

    public bool Prune { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

public sealed record RedirectsPlan(RedirectsResult Result, ChangeSet? ChangeSet);

/// <summary>
/// <c>offramp redirects sync</c> (docs/spec/commands/deps.md): binding redirects for .NET
/// Framework applications computed from the resolved graph. An assembly needs one when the
/// assemblies of the application's packages reference a version of it other than the one
/// deployed; the redirect sends every version up to the highest to the deployed one. The
/// configuration file changes only where an entry does.
/// </summary>
public static class RedirectPlanner
{
    private static readonly HashSet<ProjectKind> Applications = [ProjectKind.Console, ProjectKind.Service, ProjectKind.Web, ProjectKind.Test, ProjectKind.Winforms, ProjectKind.Wpf];

    public static RedirectsPlan Plan(RedirectsRequest request)
    {
        var packagesFolder = GlobalPackagesFolder(request.RepositoryRoot);
        var apps = new List<AppRedirects>();
        var changeSet = new ChangeSet();
        var projects = request.Model.Projects
            .Where(p => request.Apps is null ? Applications.Contains(p.Kind) : request.Apps.Contains(p.Id))
            .Where(p => p.TargetFrameworks.Any(IsFramework))
            .OrderBy(p => p.Id, StringComparer.Ordinal);
        foreach (var project in projects)
        {
            apps.Add(Sync(request, project, packagesFolder, changeSet));
        }

        var all = apps.SelectMany(a => a.Redirects).ToList();
        var result = new RedirectsResult
        {
            Apps = apps,
            Summary = new RedirectSummary(
                all.Count(r => r.Action == RedirectAction.Added), all.Count(r => r.Action == RedirectAction.Changed),
                all.Count(r => r.Action == RedirectAction.Pruned), all.Count(r => r.Action == RedirectAction.Stale),
                all.Count(r => r.Action == RedirectAction.Unchanged)),
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new RedirectsPlan(result, changeSet.IsEmpty ? null : changeSet);
    }

    private static AppRedirects Sync(RedirectsRequest request, ProjectInfo project, string? packagesFolder, ChangeSet changeSet)
    {
        var tfm = project.TargetFrameworks.First(IsFramework);
        var folder = project.Id.Contains('/', StringComparison.Ordinal) ? project.Id[..project.Id.LastIndexOf('/')] : "";
        var name = project.Kind == ProjectKind.Web ? "web.config" : "app.config";
        var config = FindConfig(request.RepositoryRoot, folder, name);
        if (config is null)
        {
            return new AppRedirects { Project = project.Id, TargetFramework = tfm, Redirects = [], Skipped = $"no {name}" + (project.Kind == ProjectKind.Web ? "" : " (the SDK generates redirects for the output)") };
        }

        var graph = AssemblyGraph(project, tfm, packagesFolder);
        var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, config));
        var bindings = ConfigBindings.Load(bytes);
        var entries = new List<RedirectEntry>();
        var existing = bindings.Redirects.ToDictionary(r => r.Assembly, StringComparer.OrdinalIgnoreCase);

        foreach (var (assembly, needed) in graph.Needed.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase))
        {
            var oldVersion = "0.0.0.0-" + needed.Highest;
            if (!existing.TryGetValue(assembly, out var present))
            {
                bindings.Add(assembly, needed.PublicKeyToken, needed.Culture, oldVersion, needed.Deployed);
                entries.Add(Entry(assembly, needed, oldVersion, RedirectAction.Added, null));
                Report(request, DiagnosticCatalog.OFR1501, config, $"Adds a redirect for {assembly}: {oldVersion} → {needed.Deployed} (referenced as {string.Join(", ", needed.Referenced)}).");
            }
            else if (present.OldVersion != oldVersion || present.NewVersion != needed.Deployed)
            {
                bindings.Change(present, oldVersion, needed.Deployed);
                entries.Add(Entry(assembly, needed, oldVersion, RedirectAction.Changed, present.NewVersion));
                Report(request, DiagnosticCatalog.OFR1502, config, $"Changes the redirect for {assembly} from {present.OldVersion} → {present.NewVersion} to {oldVersion} → {needed.Deployed}.");
            }
            else
            {
                entries.Add(Entry(assembly, needed, oldVersion, RedirectAction.Unchanged, null));
            }
        }

        foreach (var present in bindings.Redirects.Where(r => !graph.Needed.ContainsKey(r.Assembly)).OrderBy(r => r.Assembly, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new RedirectEntry
            {
                Assembly = present.Assembly, PublicKeyToken = present.PublicKeyToken, Culture = present.Culture,
                OldVersion = present.OldVersion, NewVersion = present.NewVersion, Action = RedirectAction.Unchanged,
            };
            if (graph.Deployed.ContainsKey(present.Assembly))
            {
                // One version in the graph: the redirect is harmless and stays.
                entries.Add(entry);
            }
            else if (request.Prune)
            {
                bindings.Remove(present);
                entries.Add(entry with { Action = RedirectAction.Pruned });
                Report(request, DiagnosticCatalog.OFR1503, config, $"Removes the redirect for {present.Assembly}: no package in the graph provides it.");
            }
            else
            {
                entries.Add(entry with { Action = RedirectAction.Stale });
                Report(request, DiagnosticCatalog.OFR1504, config, $"Redirects {present.Assembly} to {present.NewVersion}, but no package in the graph provides it; `--prune` removes it.");
            }
        }

        changeSet.Edit(config, bytes, bindings.Save());
        return new AppRedirects { Project = project.Id, ConfigFile = config, TargetFramework = tfm, Redirects = [.. entries.OrderBy(e => e.Assembly, StringComparer.OrdinalIgnoreCase)] };
    }

    private static RedirectEntry Entry(string assembly, NeededRedirect needed, string oldVersion, RedirectAction action, string? was) => new()
    {
        Assembly = assembly,
        PublicKeyToken = needed.PublicKeyToken,
        Culture = needed.Culture,
        OldVersion = oldVersion,
        NewVersion = needed.Deployed,
        Action = action,
        Was = was,
        Referenced = needed.Referenced,
    };

    private static void Report(RedirectsRequest request, DiagnosticDescriptor descriptor, string config, string message) =>
        request.Diagnostics.Report(descriptor, message, new DiagnosticLocation(null, config));

    private sealed record DeployedAssembly(string Name, Version Version, string? PublicKeyToken, string Culture, IReadOnlyList<(string Name, Version Version)> References);

    private sealed record NeededRedirect(string Deployed, string Highest, string PublicKeyToken, string Culture, IReadOnlyList<string> Referenced);

    private sealed record Graph(Dictionary<string, DeployedAssembly> Deployed, Dictionary<string, NeededRedirect> Needed);

    /// <summary>The assemblies the application's packages deploy for its .NET Framework target, and the redirects they need.</summary>
    private static Graph AssemblyGraph(ProjectInfo project, string tfm, string? packagesFolder)
    {
        var deployed = new Dictionary<string, DeployedAssembly>(StringComparer.OrdinalIgnoreCase);
        var target = NuGetFramework.Parse(tfm);
        foreach (var package in project.Resolved.GetValueOrDefault(tfm)?.Packages ?? [])
        {
            foreach (var assembly in PackageAssemblies(packagesFolder, package, target))
            {
                if (!deployed.TryGetValue(assembly.Name, out var known) || known.Version < assembly.Version)
                {
                    deployed[assembly.Name] = assembly;
                }
            }
        }

        var needed = new Dictionary<string, NeededRedirect>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, assembly) in deployed)
        {
            var referenced = deployed.Values.SelectMany(d => d.References).Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).Select(r => r.Version).ToHashSet();
            if (assembly.PublicKeyToken is null || referenced.All(v => v == assembly.Version))
            {
                continue;
            }

            var versions = referenced.Append(assembly.Version).Distinct().Order().ToList();
            needed[name] = new NeededRedirect(assembly.Version.ToString(), versions[^1].ToString(), assembly.PublicKeyToken, assembly.Culture, [.. versions.Select(v => v.ToString())]);
        }

        return new Graph(deployed, needed);
    }

    /// <summary>The assemblies NuGet would deploy from a package for a framework: the nearest <c>lib/</c> folder's.</summary>
    private static IEnumerable<DeployedAssembly> PackageAssemblies(string? packagesFolder, ResolvedPackage package, NuGetFramework target)
    {
        if (packagesFolder is null)
        {
            yield break;
        }

        var lib = Path.Combine(packagesFolder, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant(), "lib");
        if (!Directory.Exists(lib))
        {
            yield break;
        }

        var folders = Directory.EnumerateDirectories(lib).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList();
        var nearest = NuGetFrameworkUtility.GetNearest(folders, target, f => NuGetFramework.ParseFolder(f));
        var directory = nearest is null ? lib : Path.Combine(lib, nearest);
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
        {
            if (Read(dll) is { } assembly)
            {
                yield return assembly;
            }
        }
    }

    private static DeployedAssembly? Read(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata)
            {
                return null;
            }

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
            {
                return null;
            }

            var definition = metadata.GetAssemblyDefinition();
            var references = metadata.AssemblyReferences
                .Select(h => metadata.GetAssemblyReference(h))
                .Select(r => (metadata.GetString(r.Name), r.Version))
                .ToList();
            var culture = metadata.GetString(definition.Culture);
            return new DeployedAssembly(
                metadata.GetString(definition.Name), definition.Version, Offramp.NuGet.Inspection.PackageInspector.PublicKeyToken(metadata.GetBlobBytes(definition.PublicKey)),
                culture.Length == 0 ? "neutral" : culture, references);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static string? FindConfig(string root, string folder, string name)
    {
        var directory = RepoPaths.ToAbsolute(root, folder.Length == 0 ? "." : folder);
        var file = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
            : null;
        return file is null ? null : RepoPaths.ToRepositoryRelative(root, file);
    }

    private static string? GlobalPackagesFolder(string root)
    {
        try
        {
            return SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(root));
        }
        catch (NuGetConfigurationException)
        {
            return null;
        }
    }

    private static bool IsFramework(string tfm) => NuGetFramework.Parse(tfm).Framework == FrameworkConstants.FrameworkIdentifiers.Net;
}
