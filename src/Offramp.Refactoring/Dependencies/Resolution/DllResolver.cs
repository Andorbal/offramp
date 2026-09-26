using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;

namespace Offramp.Refactoring.Dependencies.Resolution;

[JsonConverter(typeof(CamelCaseEnumConverter<DllResolutionKind>))]
public enum DllResolutionKind
{
    /// <summary>The DLL is another project's output: reference the project.</summary>
    Project,

    /// <summary>A package supplies the assembly: reference the package.</summary>
    Package,

    /// <summary>Nothing does; the metadata is reported for a person to decide.</summary>
    None,
}

public sealed record DllResolution
{
    public required DllResolutionKind Kind { get; init; }

    public string? Project { get; init; }

    public string? Package { get; init; }

    public string? Version { get; init; }

    public required string Reason { get; init; }
}

/// <summary>A <c>Reference</c> with a <c>HintPath</c>, what the DLL is, and what replaces it.</summary>
public sealed record LooseDll
{
    public required string Name { get; init; }

    public required string HintPath { get; init; }

    public string? AssemblyVersion { get; init; }

    public string? TargetFramework { get; init; }

    public string? PublicKeyToken { get; init; }

    public required DllResolution Resolution { get; init; }

    /// <summary>A .NET Framework DLL nothing replaces: it blocks the move to the target (<c>OFR1404</c>).</summary>
    public bool Blocker { get; init; }
}

public sealed record ProjectDlls(string Project, IReadOnlyList<LooseDll> References);

public sealed record ResolveDllsSummary(int Project, int Package, int Unmatched, int Blockers);

/// <summary>The <c>result</c> of <c>offramp deps resolve-dlls</c> (<c>schemas/v1/deps-resolve-dlls.json</c>).</summary>
public sealed record ResolveDllsResult
{
    public required IReadOnlyList<ProjectDlls> Projects { get; init; }

    public required ResolveDllsSummary Summary { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record ResolveDllsRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required IPackageFeeds Feeds { get; init; }

    public required ICache Cache { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    /// <summary>Only this project (a model id); null for all.</summary>
    public string? Project { get; init; }

    public bool IncludePrerelease { get; init; }
}

public sealed record ResolveDllsPlan(ResolveDllsResult Result, ChangeSet? ChangeSet);

/// <summary>
/// <c>offramp deps resolve-dlls</c> (docs/spec/commands/deps.md): turns loose assembly references
/// (a <c>HintPath</c> to a DLL) into project or package references. A DLL named like a project's
/// assembly is that project's output. Otherwise the package named like the assembly is searched,
/// by inspecting its versions' assets: the lowest version shipping the assembly at the referenced
/// version or higher, with the same public key, for every target framework of the project.
/// </summary>
public static class DllResolver
{
    public static async Task<ResolveDllsPlan> PlanAsync(ResolveDllsRequest request, CancellationToken cancellationToken)
    {
        var inspections = new PackageInspections(request.Feeds, request.Cache);
        var changeSet = new ChangeSet();
        var results = new List<ProjectDlls>();
        foreach (var project in request.Model.Projects.Where(p => request.Project is null || p.Id == request.Project).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var loose = project.AssemblyReferences.Where(r => r.Kind == AssemblyReferenceKind.File && r.HintPath is not null).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (loose.Count == 0)
            {
                continue;
            }

            var dlls = new List<LooseDll>();
            foreach (var reference in loose)
            {
                dlls.Add(await ResolveAsync(request, inspections, project, reference, cancellationToken));
            }

            results.Add(new ProjectDlls(project.Id, dlls));
            Edit(request, project, dlls, changeSet);
        }

        var all = results.SelectMany(r => r.References).ToList();
        var result = new ResolveDllsResult
        {
            Projects = results,
            Summary = new ResolveDllsSummary(
                all.Count(d => d.Resolution.Kind == DllResolutionKind.Project), all.Count(d => d.Resolution.Kind == DllResolutionKind.Package),
                all.Count(d => d.Resolution.Kind == DllResolutionKind.None), all.Count(d => d.Blocker)),
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new ResolveDllsPlan(result, changeSet.IsEmpty ? null : changeSet);
    }

    private static async Task<LooseDll> ResolveAsync(ResolveDllsRequest request, PackageInspections inspections, ProjectInfo project, AssemblyReferenceInfo reference, CancellationToken cancellationToken)
    {
        var metadata = reference.Metadata;
        var dll = new LooseDll
        {
            Name = reference.Name,
            HintPath = reference.HintPath!,
            AssemblyVersion = metadata?.AssemblyVersion,
            TargetFramework = metadata?.TargetFramework,
            PublicKeyToken = metadata?.PublicKeyToken,
            Resolution = new DllResolution { Kind = DllResolutionKind.None, Reason = "" },
        };
        var location = new DiagnosticLocation(project.Id, reference.HintPath);

        var owner = request.Model.Projects.FirstOrDefault(p => p.Id != project.Id && string.Equals(p.AssemblyName ?? p.Name, reference.Name, StringComparison.OrdinalIgnoreCase));
        if (owner is not null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1401, $"{reference.HintPath} is the output of {owner.Id}; reference the project instead.", location);
            return dll with { Resolution = new DllResolution { Kind = DllResolutionKind.Project, Project = owner.Id, Reason = $"{owner.Id} builds {reference.Name}." } };
        }

        if (await FindPackageAsync(request, inspections, project, dll, cancellationToken) is { } found)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1402, $"{reference.HintPath} is {reference.Name} {dll.AssemblyVersion} from package {found.Package} {found.Version}.", location);
            return dll with { Resolution = found };
        }

        var described = $"{reference.Name} {dll.AssemblyVersion ?? "(no version)"} for {dll.TargetFramework ?? "no recorded framework"}{(dll.PublicKeyToken is null ? "" : $", public key token {dll.PublicKeyToken}")}";
        var isFramework = dll.TargetFramework?.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase) == true;
        var none = new DllResolution { Kind = DllResolutionKind.None, Reason = $"No project builds it and no package named {reference.Name} ships it." };
        if (isFramework)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1404, $"{reference.HintPath} ({described}) is built for .NET Framework and nothing replaces it; it blocks the move to the target.", location,
                [KeyValuePair.Create<string, JsonNode?>("assembly", reference.Name)]);
            return dll with { Resolution = none, Blocker = true };
        }

        request.Diagnostics.Report(DiagnosticCatalog.OFR1403, $"{reference.HintPath} ({described}) matches no project or package.", location,
            [KeyValuePair.Create<string, JsonNode?>("assembly", reference.Name)]);
        return dll with { Resolution = none };
    }

    /// <summary>
    /// The lowest version of the package named like the assembly that ships it at the referenced
    /// version or higher (an exact version first), with the same public key token, for every
    /// target framework of the project; null when there is none.
    /// </summary>
    private static async Task<DllResolution?> FindPackageAsync(ResolveDllsRequest request, PackageInspections inspections, ProjectInfo project, LooseDll dll, CancellationToken cancellationToken)
    {
        var available = await request.Feeds.GetVersionsAsync(dll.Name, cancellationToken);
        var referenced = Version.TryParse(dll.AssemblyVersion, out var parsed) ? parsed : new Version(0, 0);
        var tfms = project.TargetFrameworks.Select(NuGetFramework.Parse).ToList();
        (NuGetVersion Version, bool Exact)? best = null;
        foreach (var version in available.Versions.Where(v => v.Listed && (request.IncludePrerelease || !v.Version.IsPrerelease)).Select(v => v.Version).Order())
        {
            if (await inspections.GetAsync(dll.Name, version, cancellationToken) is not { } inspection || !tfms.All(t => TargetSupport.Supports(inspection, t)))
            {
                continue;
            }

            var shipped = inspection.Assemblies
                .Where(a => string.Equals(a.Name, dll.Name, StringComparison.OrdinalIgnoreCase) && Version.TryParse(a.Version, out var v) && v >= referenced)
                .Where(a => dll.PublicKeyToken is null || string.Equals(a.PublicKeyToken, dll.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (shipped.Count == 0)
            {
                continue;
            }

            var exact = shipped.Any(a => Version.Parse(a.Version!) == referenced);
            if (best is null || (exact && !best.Value.Exact))
            {
                best = (version, exact);
            }

            if (exact)
            {
                break;
            }
        }

        return best is { } chosen
            ? new DllResolution
            {
                Kind = DllResolutionKind.Package,
                Package = dll.Name,
                Version = chosen.Version.ToNormalizedString(),
                Reason = $"{dll.Name} {chosen.Version.ToNormalizedString()} ships {dll.Name} {(chosen.Exact ? dll.AssemblyVersion : "at or above " + dll.AssemblyVersion)} for {string.Join(", ", project.TargetFrameworks)}.",
            }
            : null;
    }

    /// <summary>Replaces each resolved Reference with a ProjectReference or PackageReference.</summary>
    private static void Edit(ResolveDllsRequest request, ProjectInfo project, List<LooseDll> dlls, ChangeSet changeSet)
    {
        var resolved = dlls.Where(d => d.Resolution.Kind != DllResolutionKind.None).ToList();
        if (resolved.Count == 0)
        {
            return;
        }

        var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id));
        var editor = ProjectFileEditor.Load(bytes);
        var central = project.Properties.TryGetValue("ManagePackageVersionsCentrally", out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        foreach (var dll in resolved)
        {
            editor.RemoveReference(dll.Name);
            if (dll.Resolution.Kind == DllResolutionKind.Project)
            {
                editor.AddProjectReference(Relative(project.Id, dll.Resolution.Project!));
            }
            else
            {
                editor.AddPackageReference(dll.Resolution.Package!, central ? null : dll.Resolution.Version);
            }
        }

        changeSet.Edit(project.Id, bytes, editor.Save());
    }

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
