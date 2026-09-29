using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Json;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;
using Offramp.NuGet.Rules;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;
using Offramp.Workspace.Verification;

namespace Offramp.Refactoring.Dependencies.Resolution;

[JsonConverter(typeof(CamelCaseEnumConverter<DllResolutionKind>))]
public enum DllResolutionKind
{
    /// <summary>The DLL is another project's output: reference the project.</summary>
    Project,

    /// <summary>A package supplies the assembly: reference the package.</summary>
    Package,

    /// <summary>
    /// The project's packages.config installs the package the DLL comes from (the <c>HintPath</c>
    /// goes through its <c>packages/&lt;Id&gt;.&lt;Version&gt;/</c> folder): NuGet manages the reference
    /// already, so nothing changes; <c>csproj modernize</c> converts it with the rest.
    /// </summary>
    PackagesConfig,

    /// <summary>Nothing does; the metadata is reported for a person to decide.</summary>
    None,
}

/// <summary>How a package was matched to a DLL, from the strongest evidence to the weakest (ADR 0042).</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<DllMatch>))]
public enum DllMatch
{
    /// <summary>The HintPath goes through the package's folder in the NuGet global packages folder, which names the package and version.</summary>
    Path,

    /// <summary>The package ships the same file, byte for byte (SHA-256).</summary>
    Identical,

    /// <summary>The package ships the assembly with the DLL's file version: the same build, other bytes.</summary>
    FileVersion,

    /// <summary>The package ships the assembly with the DLL's informational version.</summary>
    InformationalVersion,

    /// <summary>The package ships the assembly version, but not this build: the closest one, the lowest package version with it.</summary>
    AssemblyVersion,

    /// <summary>No package ships the assembly version; the lowest with a higher one is an upgrade.</summary>
    Newer,
}

public sealed record DllResolution
{
    public required DllResolutionKind Kind { get; init; }

    public string? Project { get; init; }

    public string? Package { get; init; }

    public string? Version { get; init; }

    /// <summary>For a package: what matched (<see cref="DllMatch"/>); null otherwise.</summary>
    public DllMatch? Match { get; init; }

    public required string Reason { get; init; }
}

/// <summary>A <c>Reference</c> with a <c>HintPath</c>, what the DLL is, and what replaces it.</summary>
public sealed record LooseDll
{
    public required string Name { get; init; }

    public required string HintPath { get; init; }

    public string? AssemblyVersion { get; init; }

    /// <summary>The DLL's <c>AssemblyFileVersionAttribute</c>, or null.</summary>
    public string? FileVersion { get; init; }

    public string? TargetFramework { get; init; }

    public string? PublicKeyToken { get; init; }

    public required DllResolution Resolution { get; init; }

    /// <summary>A .NET Framework DLL nothing replaces: it blocks the move to the target (<c>OFR1404</c>).</summary>
    public bool Blocker { get; init; }
}

public sealed record ProjectDlls(string Project, IReadOnlyList<LooseDll> References);

public sealed record ResolveDllsSummary(int Project, int Package, int Unmatched, int Blockers, int PackagesConfig);

/// <summary>The <c>result</c> of <c>offramp deps resolve-dlls</c> (<c>schemas/v1/deps-resolve-dlls.json</c>).</summary>
public sealed record ResolveDllsResult
{
    public required IReadOnlyList<ProjectDlls> Projects { get; init; }

    public required ResolveDllsSummary Summary { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }

    /// <summary>The verification after <c>--apply</c>; null in a dry run or with <c>--verify none</c>.</summary>
    public VerifyResult? Verify { get; init; }

    /// <summary>True when verification failed and the edits were restored from the journal (<c>OFR1408</c>).</summary>
    public bool RolledBack { get; init; }
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

    /// <summary><c>deps.assemblyPackages</c>: packages that ship an assembly under another name, before rules/assembly-packages.yml.</summary>
    public IReadOnlyList<AssemblyPackageEntry> AssemblyPackages { get; init; } = [];

    /// <summary>
    /// Whether Offramp runs on Windows. Elsewhere a legacy (non-SDK) project gets no
    /// <c>PackageReference</c>: the .NET SDK restores it but never passes its assemblies to the
    /// compiler (only Visual Studio's <c>Microsoft.NuGet.targets</c> does).
    /// </summary>
    public bool OnWindows { get; init; } = OperatingSystem.IsWindows();
}

public sealed record ResolveDllsPlan(ResolveDllsResult Result, ChangeSet? ChangeSet);

/// <summary>
/// <c>offramp deps resolve-dlls</c> (docs/spec/commands/deps.md): turns loose assembly references
/// (a <c>HintPath</c> to a DLL) into project or package references. A DLL named like a project's
/// assembly is that project's output. Otherwise the package named like the assembly is searched,
/// by inspecting its versions' assets for the assembly with the same public key, for every
/// target framework of the project, and ranked by what matches (ADR 0042): the same file, the
/// same file version, the same informational version, the assembly version (the lowest package
/// version shipping it), and last a higher assembly version (an upgrade).
/// </summary>
public static class DllResolver
{
    public static async Task<ResolveDllsPlan> PlanAsync(ResolveDllsRequest request, CancellationToken cancellationToken)
    {
        var lookup = new Lookup(new PackageInspections(request.Feeds, request.Cache), new AssemblyPackages(request.AssemblyPackages));
        var changeSet = new ChangeSet();
        var results = new List<ProjectDlls>();
        var declaredElsewhere = new SortedDictionary<(string Name, string? DeclaredIn), List<string>>(DeclarationOrder.Instance);
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
                dlls.Add(await ResolveAsync(request, lookup, project, reference, cancellationToken));
            }

            results.Add(new ProjectDlls(project.Id, dlls));
            foreach (var declaration in Edit(request, project, dlls, changeSet))
            {
                if (!declaredElsewhere.TryGetValue(declaration, out var projects))
                {
                    declaredElsewhere[declaration] = projects = [];
                }

                projects.Add(project.Id);
            }
        }

        ReportDeclaredElsewhere(request, declaredElsewhere);

        var all = results.SelectMany(r => r.References).ToList();
        var result = new ResolveDllsResult
        {
            Projects = results,
            Summary = new ResolveDllsSummary(
                all.Count(d => d.Resolution.Kind == DllResolutionKind.Project), all.Count(d => d.Resolution.Kind == DllResolutionKind.Package),
                all.Count(d => d.Resolution.Kind == DllResolutionKind.None), all.Count(d => d.Blocker),
                all.Count(d => d.Resolution.Kind == DllResolutionKind.PackagesConfig)),
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new ResolveDllsPlan(result, changeSet.IsEmpty ? null : changeSet);
    }

    /// <summary>Where packages are looked up: the inspections (cached) and the package ids to try for an assembly.</summary>
    private sealed record Lookup(PackageInspections Inspections, AssemblyPackages Packages);

    private static async Task<LooseDll> ResolveAsync(ResolveDllsRequest request, Lookup lookup, ProjectInfo project, AssemblyReferenceInfo reference, CancellationToken cancellationToken)
    {
        // The file as it is now; the model's metadata when it cannot be read.
        var facts = AssemblyFacts.ReadFile(RepoPaths.ToAbsolute(request.RepositoryRoot, reference.HintPath!));
        var metadata = reference.Metadata;
        var dll = new LooseDll
        {
            Name = reference.Name,
            HintPath = reference.HintPath!,
            AssemblyVersion = facts?.Version ?? metadata?.AssemblyVersion,
            FileVersion = facts?.FileVersion,
            TargetFramework = facts is null ? metadata?.TargetFramework : facts.InferredFramework,
            PublicKeyToken = facts is null ? metadata?.PublicKeyToken : facts.PublicKeyToken,
            Resolution = new DllResolution { Kind = DllResolutionKind.None, Reason = "" },
        };
        var location = new DiagnosticLocation(project.Id, reference.HintPath);

        var owner = request.Model.Projects.FirstOrDefault(p => p.Id != project.Id && string.Equals(p.AssemblyName ?? p.Name, reference.Name, StringComparison.OrdinalIgnoreCase));
        if (owner is not null)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1401, $"{reference.HintPath} is the output of {owner.Id}; reference the project instead.", location);
            return dll with { Resolution = new DllResolution { Kind = DllResolutionKind.Project, Project = owner.Id, Reason = $"{owner.Id} builds {reference.Name}." } };
        }

        if (InstalledPackage.For(project, reference.HintPath!) is { } installed)
        {
            var version = NuGetVersion.TryParse(installed.Version, out var parsed) ? parsed.ToNormalizedString() : installed.Version;
            return dll with
            {
                Resolution = new DllResolution
                {
                    Kind = DllResolutionKind.PackagesConfig,
                    Package = installed.Id,
                    Version = version,
                    Reason = $"packages.config installs {installed.Id} {installed.Version}, which ships it; NuGet manages the reference.",
                },
            };
        }

        if (await FromGlobalPackagesFolderAsync(lookup, dll, cancellationToken) is { } fromPath)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1402, Matched(dll, fromPath), location,
                [KeyValuePair.Create<string, JsonNode?>("package", fromPath.Package), KeyValuePair.Create<string, JsonNode?>("version", fromPath.Version.ToNormalizedString()),
                 KeyValuePair.Create<string, JsonNode?>("match", Wire(fromPath.Match))]);
            return dll with { Resolution = fromPath.ToResolution(dll, project) };
        }

        var found = await FindPackageAsync(request, lookup, project, dll, facts, cancellationToken);
        if (found is not null && (dll.PublicKeyToken is not null || found.Match <= DllMatch.InformationalVersion))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1402, Matched(dll, found), location,
                [KeyValuePair.Create<string, JsonNode?>("package", found.Package), KeyValuePair.Create<string, JsonNode?>("version", found.Version.ToNormalizedString()),
                 KeyValuePair.Create<string, JsonNode?>("match", Wire(found.Match))]);
            return dll with { Resolution = found.ToResolution(dll, project) };
        }

        var inferred = facts is { TargetFramework: null, FrameworkCorlib: { } corlib } ? $" (it references mscorlib {corlib})" : "";
        var described = $"{reference.Name} {dll.AssemblyVersion ?? "(no version)"} for {dll.TargetFramework ?? "no recorded framework"}{inferred}{(dll.PublicKeyToken is null ? "" : $", public key token {dll.PublicKeyToken}")}";
        var reason = found is null
            ? $"No project builds it and no package ships it (searched {string.Join(", ", lookup.Packages.For(reference.Name))})."
            : $"No project builds it. It is unsigned, and package {found.Package} ships an assembly of that name but not this file or file version; a name alone does not identify it.";
        var none = new DllResolution { Kind = DllResolutionKind.None, Reason = reason };
        if (facts?.ImportedFromTypeLib is { } typeLibrary)
        {
            // tlbimp's output: COM interop for Windows, which no package replaces and modern .NET on Windows can still use.
            request.Diagnostics.Report(DiagnosticCatalog.OFR1405,
                $"{reference.HintPath} ({described}) is a COM interop assembly generated from the type library {typeLibrary}; COM works on Windows only. Keep it with a net10.0-windows target, or reference the type library with a COMReference.",
                location, [KeyValuePair.Create<string, JsonNode?>("assembly", reference.Name), KeyValuePair.Create<string, JsonNode?>("typeLibrary", typeLibrary)]);
            return dll with { Resolution = none with { Reason = $"A COM interop assembly generated from the type library {typeLibrary}: no package ships it, and it works on Windows only." } };
        }

        if (dll.TargetFramework?.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1404, $"{reference.HintPath} ({described}) is built for .NET Framework and nothing replaces it; it blocks the move to the target.", location,
                [KeyValuePair.Create<string, JsonNode?>("assembly", reference.Name)]);
            return dll with { Resolution = none, Blocker = true };
        }

        var unsigned = found is null ? "" : $": it is unsigned, and package {found.Package} ships an assembly of that name but not this file or file version; a name alone does not identify it";
        request.Diagnostics.Report(DiagnosticCatalog.OFR1403, $"{reference.HintPath} ({described}) matches no project or package{unsigned}.", location,
            [KeyValuePair.Create<string, JsonNode?>("assembly", reference.Name)]);
        return dll with { Resolution = none };
    }

    /// <summary>The <c>OFR1402</c> message, worded by what matched.</summary>
    private static string Matched(LooseDll dll, PackageCandidate found)
    {
        var package = $"package {found.Package} {found.Version.ToNormalizedString()}{(found.Listed ? "" : " (unlisted)")}";
        return found.Match switch
        {
            DllMatch.Path => $"{dll.HintPath} is {dll.Name} {dll.AssemblyVersion ?? ""} from {package}: its path in the NuGet global packages folder names them.".Replace("  ", " ", StringComparison.Ordinal),
            DllMatch.Identical => $"{dll.HintPath} is {dll.Name} {dll.FileVersion ?? dll.AssemblyVersion} from {package}: the same file, byte for byte.",
            DllMatch.FileVersion => $"{dll.HintPath} is {dll.Name} {dll.FileVersion} from {package}: the same file version, not the same bytes.",
            DllMatch.InformationalVersion => $"{dll.HintPath} is {dll.Name} {found.InformationalVersion} from {package}: the same informational version, not the same bytes.",
            DllMatch.AssemblyVersion => $"{dll.HintPath} ({dll.Name} {dll.AssemblyVersion}{(dll.FileVersion is null ? "" : $", file version {dll.FileVersion}")}): no package ships this build; the closest build is in {package}, the lowest with assembly version {dll.AssemblyVersion}.",
            _ => $"{dll.HintPath} ({dll.Name} {dll.AssemblyVersion}): no package ships this version; {package} is newer, with {dll.Name} {found.ShippedVersion}: an upgrade.",
        };
    }

    private static string Wire(DllMatch match) => char.ToLowerInvariant(match.ToString()[0]) + match.ToString()[1..];

    /// <summary>
    /// The package and version a <c>$(NuGetPackageRoot)&lt;id&gt;/&lt;version&gt;/...</c> HintPath goes
    /// through (a legacy project outside Windows can reference a package's DLL no other way); the
    /// id is spelled as the package's nuspec spells it when the package can be inspected. Null for
    /// any other HintPath.
    /// </summary>
    private static async Task<PackageCandidate?> FromGlobalPackagesFolderAsync(Lookup lookup, LooseDll dll, CancellationToken cancellationToken)
    {
        const string Root = "$(NuGetPackageRoot)";
        var segments = dll.HintPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? dll.HintPath[Root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries) : [];
        if (segments.Length < 3 || !NuGetVersion.TryParse(segments[1], out var version))
        {
            return null;
        }

        var id = (await lookup.Inspections.GetAsync(segments[0], version, cancellationToken))?.Id ?? segments[0];
        return new PackageCandidate(id, version, true, DllMatch.Path, dll.AssemblyVersion ?? "", null);
    }

    /// <summary>A package version that ships the DLL's assembly, and how well it matches.</summary>
    private sealed record PackageCandidate(string Package, NuGetVersion Version, bool Listed, DllMatch Match, string ShippedVersion, string? InformationalVersion)
    {
        /// <summary>Stronger evidence first; within the same evidence a listed version before an unlisted one; else the earlier (lower) one stays.</summary>
        public bool Beats(PackageCandidate? other) =>
            other is null || Match < other.Match || (Match == other.Match && Listed && !other.Listed);

        public DllResolution ToResolution(LooseDll dll, ProjectInfo project) => new()
        {
            Kind = DllResolutionKind.Package,
            Package = Package,
            Version = Version.ToNormalizedString(),
            Match = Match,
            Reason = Match switch
            {
                DllMatch.Path => $"The HintPath goes through {Package} {Version.ToNormalizedString()} in the NuGet global packages folder.",
                DllMatch.Identical => $"{Package} {Version.ToNormalizedString()} ships this file, byte for byte, for {string.Join(", ", project.TargetFrameworks)}.",
                DllMatch.FileVersion => $"{Package} {Version.ToNormalizedString()} ships {dll.Name} file version {dll.FileVersion} for {string.Join(", ", project.TargetFrameworks)}.",
                DllMatch.InformationalVersion => $"{Package} {Version.ToNormalizedString()} ships {dll.Name} {InformationalVersion} for {string.Join(", ", project.TargetFrameworks)}.",
                DllMatch.AssemblyVersion => $"{Package} {Version.ToNormalizedString()} is the lowest version that ships {dll.Name} {dll.AssemblyVersion} for {string.Join(", ", project.TargetFrameworks)}; no version ships this build.",
                _ => $"No version ships {dll.Name} {dll.AssemblyVersion}; {Package} {Version.ToNormalizedString()} is the lowest with a higher one ({ShippedVersion}) for {string.Join(", ", project.TargetFrameworks)}: an upgrade.",
            } + (Listed ? "" : " The version is unlisted on the feed."),
        };
    }

    /// <summary>
    /// The best package version for the DLL across the package ids the assembly may ship in
    /// (<see cref="AssemblyPackages"/>): the strongest match wins, and on a tie the id that comes first.
    /// </summary>
    private static async Task<PackageCandidate?> FindPackageAsync(
        ResolveDllsRequest request, Lookup lookup, ProjectInfo project, LooseDll dll, AssemblyFacts? facts, CancellationToken cancellationToken)
    {
        if (!Version.TryParse(dll.AssemblyVersion, out var referenced))
        {
            return null;
        }

        PackageCandidate? best = null;
        foreach (var id in lookup.Packages.For(dll.Name))
        {
            var candidate = await FindInPackageAsync(request, lookup.Inspections, id, referenced, project, dll, facts, cancellationToken);
            if (candidate is not null && candidate.Beats(best))
            {
                best = candidate;
            }

            if (best?.Match == DllMatch.Identical)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// The best version of one package: one that ships the assembly with
    /// the same public key token (none for an unsigned DLL), at the referenced version or higher,
    /// for every target framework of the project, ranked by <see cref="DllMatch"/>. Unlisted versions
    /// count when they ship the referenced assembly version (a checked-in DLL is often of a version
    /// its authors unlisted later), never as an upgrade. Null when none ships it.
    /// </summary>
    private static async Task<PackageCandidate?> FindInPackageAsync(
        ResolveDllsRequest request, PackageInspections inspections, string id, Version referenced, ProjectInfo project, LooseDll dll, AssemblyFacts? facts, CancellationToken cancellationToken)
    {
        var available = await request.Feeds.GetVersionsAsync(id, cancellationToken);
        var tfms = project.TargetFrameworks.Select(NuGetFramework.Parse).ToList();
        PackageCandidate? best = null;
        var exactSeen = false;
        foreach (var info in available.Versions.Where(v => request.IncludePrerelease || !v.Version.IsPrerelease).OrderBy(v => v.Version))
        {
            if (await inspections.GetAsync(id, info.Version, cancellationToken) is not { } inspection || !tfms.All(t => TargetSupport.Supports(inspection, t)))
            {
                continue;
            }

            var shipped = inspection.Assemblies
                .Where(a => string.Equals(a.Name, dll.Name, StringComparison.OrdinalIgnoreCase) && Version.TryParse(a.Version, out var v) && v >= referenced)
                .Where(a => string.Equals(a.PublicKeyToken, dll.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Path, StringComparer.Ordinal)
                .ToList();
            if (shipped.Count == 0)
            {
                continue;
            }

            var exact = shipped.Where(a => Version.Parse(a.Version!) == referenced).ToList();
            if (exactSeen && exact.Count == 0)
            {
                // Past the versions that ship the referenced assembly version: the rest can only be upgrades.
                break;
            }

            exactSeen |= exact.Count > 0;
            var candidate = Candidate(id, info, shipped, exact, facts);
            if (candidate is not null && candidate.Beats(best))
            {
                best = candidate;
            }

            if (best?.Match == DllMatch.Identical)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>How one package version's assets match the DLL; null for an unlisted version that is only an upgrade.</summary>
    private static PackageCandidate? Candidate(string id, PackageVersionInfo info, List<InspectedAssembly> shipped, List<InspectedAssembly> exact, AssemblyFacts? facts)
    {
        var match = exact.Count == 0 ? DllMatch.Newer
            : facts is not null && shipped.Any(a => string.Equals(a.Sha256, facts.Sha256, StringComparison.Ordinal)) ? DllMatch.Identical
            : facts?.FileVersion is { } file && exact.Any(a => a.FileVersion == file) ? DllMatch.FileVersion
            : facts?.InformationalVersion is { } informational && exact.Any(a => a.InformationalVersion == informational) ? DllMatch.InformationalVersion
            : DllMatch.AssemblyVersion;
        if (match == DllMatch.Newer && !info.Listed)
        {
            return null;
        }

        var shippedVersion = shipped.Select(a => Version.Parse(a.Version!)).Min()!.ToString();
        return new PackageCandidate(id, info.Version, info.Listed, match, shippedVersion, facts?.InformationalVersion);
    }

    /// <summary>
    /// Replaces each resolved Reference with a ProjectReference or PackageReference. A packages.config
    /// project gets no PackageReference (NuGet does not mix the two in one project): its packages
    /// change with <c>csproj modernize</c>. A Reference the project file does not declare (it comes
    /// from an imported file, such as Directory.Build.props) is left alone: editing the project would
    /// add a second reference and remove none. Returns those, with the file that declares each when
    /// one is found.
    /// </summary>
    private static List<(string Name, string? DeclaredIn)> Edit(ResolveDllsRequest request, ProjectInfo project, List<LooseDll> dlls, ChangeSet changeSet)
    {
        var elsewhere = new List<(string, string?)>();
        var legacyOutsideWindows = !project.SdkStyle && !project.PackagesConfig && !request.OnWindows;
        if (legacyOutsideWindows)
        {
            ReportLegacyOutsideWindows(request, project, [.. dlls.Where(d => d.Resolution.Kind == DllResolutionKind.Package)]);
        }

        var resolved = dlls.Where(d => d.Resolution.Kind == DllResolutionKind.Project
            || (d.Resolution.Kind == DllResolutionKind.Package && !project.PackagesConfig && !legacyOutsideWindows)).ToList();
        if (resolved.Count == 0)
        {
            return elsewhere;
        }

        var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(request.RepositoryRoot, project.Id));
        var editor = ProjectFileEditor.Load(bytes);
        var central = project.Properties.TryGetValue("ManagePackageVersionsCentrally", out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        foreach (var dll in resolved)
        {
            if (!editor.DeclaresReference(dll.Name))
            {
                elsewhere.Add((dll.Name, DeclaringFile(request.RepositoryRoot, project.Id, editor, dll.Name)));
                continue;
            }

            // In place, keeping the Reference's condition and its item group's, when it has one.
            if (dll.Resolution.Kind == DllResolutionKind.Project)
            {
                editor.ReplaceReference(dll.Name, "ProjectReference", Relative(project.Id, dll.Resolution.Project!), null);
            }
            else
            {
                editor.ReplaceReference(dll.Name, "PackageReference", dll.Resolution.Package!, central ? null : dll.Resolution.Version);
            }
        }

        if (elsewhere.Count < resolved.Count)
        {
            changeSet.Edit(project.Id, bytes, editor.Save());
        }

        return elsewhere;
    }

    /// <summary>
    /// <c>OFR1407</c>: outside Windows, the .NET SDK restores a legacy project's <c>PackageReference</c>
    /// items but resolves no assemblies from them (<c>ResolveNuGetPackageAssets</c> is in Visual
    /// Studio's <c>Microsoft.NuGet.targets</c>), so replacing the References would break the build
    /// (NHibernate: 2,505 errors).
    /// </summary>
    private static void ReportLegacyOutsideWindows(ResolveDllsRequest request, ProjectInfo project, List<LooseDll> packages)
    {
        if (packages.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", packages.Select(p => $"{p.Resolution.Package} {p.Resolution.Version}"));
        request.Diagnostics.Report(DiagnosticCatalog.OFR1407,
            $"{project.Id} is a legacy (non-SDK) project, and outside Windows the .NET SDK gives the compiler none of a PackageReference's assemblies in such a project, so its References stay ({names}). Convert it with `offramp csproj modernize` first, or apply on Windows.",
            new DiagnosticLocation(project.Id),
            [KeyValuePair.Create<string, JsonNode?>("packages", new JsonArray([.. packages.Select(p => (JsonNode?)$"{p.Resolution.Package} {p.Resolution.Version}")]))]);
    }

    /// <summary>
    /// The repository file that declares a Reference the project file does not: a
    /// Directory.Build.props or .targets in the project's folder or above, or a file the project
    /// imports by a literal path; null when none is found (an import through properties).
    /// </summary>
    private static string? DeclaringFile(string root, string projectId, ProjectFileEditor project, string assembly)
    {
        var folder = projectId.Contains('/', StringComparison.Ordinal) ? projectId[..projectId.LastIndexOf('/')] : "";
        var candidates = new List<string>();
        foreach (var import in project.Imports)
        {
            var path = import.Replace("$(MSBuildThisFileDirectory)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildProjectDirectory)\\", "", StringComparison.OrdinalIgnoreCase)
                .Replace("$(MSBuildProjectDirectory)/", "", StringComparison.OrdinalIgnoreCase)
                .Replace('\\', '/');
            if (!path.Contains("$(", StringComparison.Ordinal) && !Path.IsPathRooted(path))
            {
                candidates.Add(Normalize(folder.Length == 0 ? path : folder + "/" + path));
            }
        }

        for (var current = folder; ; current = current.Contains('/', StringComparison.Ordinal) ? current[..current.LastIndexOf('/')] : "")
        {
            candidates.Add(current.Length == 0 ? "Directory.Build.props" : current + "/Directory.Build.props");
            candidates.Add(current.Length == 0 ? "Directory.Build.targets" : current + "/Directory.Build.targets");
            if (current.Length == 0)
            {
                break;
            }
        }

        foreach (var candidate in candidates.Where(c => !c.StartsWith("../", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            var file = RepoPaths.ToAbsolute(root, candidate);
            try
            {
                if (File.Exists(file) && ProjectFileEditor.Load(File.ReadAllBytes(file)).DeclaresReference(assembly))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is Microsoft.Build.Exceptions.InvalidProjectFileException or System.Xml.XmlException or IOException)
            {
                // Not an MSBuild file this can read; look further.
            }
        }

        return null;
    }

    /// <summary>"a/b/../c" → "a/c".</summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>One <c>OFR1406</c> per assembly and declaring file, with the projects it reaches.</summary>
    private static void ReportDeclaredElsewhere(ResolveDllsRequest request, SortedDictionary<(string Name, string? DeclaredIn), List<string>> declared)
    {
        foreach (var ((name, declaredIn), projects) in declared)
        {
            var where = declaredIn is null ? "a file the project imports" : declaredIn;
            var count = projects.Count == 1 ? projects[0] : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{projects.Count} projects");
            request.Diagnostics.Report(DiagnosticCatalog.OFR1406,
                $"The {name} reference of {count} is declared in {where}, not in the project file, so it was left alone; change it there.",
                new DiagnosticLocation(projects.Count == 1 ? projects[0] : null, declaredIn),
                [KeyValuePair.Create<string, JsonNode?>("assembly", name), KeyValuePair.Create<string, JsonNode?>("declaredIn", declaredIn),
                 KeyValuePair.Create<string, JsonNode?>("projects", new JsonArray([.. projects.Order(StringComparer.Ordinal).Select(p => (JsonNode?)p)]))]);
        }
    }

    /// <summary>Sorts (assembly, declaring file) keys: by name, then file, with "unknown" last.</summary>
    private sealed class DeclarationOrder : IComparer<(string Name, string? DeclaredIn)>
    {
        public static readonly DeclarationOrder Instance = new();

        public int Compare((string Name, string? DeclaredIn) x, (string Name, string? DeclaredIn) y)
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
            return byName != 0 ? byName
                : x.DeclaredIn is null ? (y.DeclaredIn is null ? 0 : 1)
                : y.DeclaredIn is null ? -1
                : StringComparer.Ordinal.Compare(x.DeclaredIn, y.DeclaredIn);
        }
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
