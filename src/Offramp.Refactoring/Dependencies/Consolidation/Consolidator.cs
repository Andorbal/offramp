using System.Text.Json.Nodes;
using NuGet.Frameworks;
using NuGet.Versioning;
using Offramp.Core.Caching;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Progress;
using Offramp.NuGet.Feeds;
using Offramp.NuGet.Inspection;
using Offramp.Refactoring.ChangeSets;

namespace Offramp.Refactoring.Dependencies.Consolidation;

public sealed record ConsolidateRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required OfframpConfig Config { get; init; }

    public required IPackageFeeds Feeds { get; init; }

    public required ICache Cache { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary>Consolidate this package (and its family).</summary>
    public string? Package { get; init; }

    /// <summary>Consolidate the packages whose id starts with this prefix.</summary>
    public string? Family { get; init; }

    /// <summary><c>lowest</c> or <c>newest</c>.</summary>
    public required string Prefer { get; init; }

    /// <summary>Move versions into a central props file (<c>--cpm</c>).</summary>
    public bool Cpm { get; init; }

    /// <summary>The props file that opts projects in to a non-default central file (<c>--opt-in-via</c>), repository-relative.</summary>
    public string? OptInVia { get; init; }
}

/// <summary>The decisions, and the change set that writes them (null when nothing changes).</summary>
public sealed record ConsolidationPlan(ConsolidateResult Result, ChangeSet? ChangeSet);

/// <summary>
/// <c>offramp deps consolidate</c> (docs/spec/commands/deps.md): one version per package across
/// the solution. Constraints come from direct references, every resolved package's dependency
/// ranges, the selected versions' own dependencies, and pins; the version is the lowest (or
/// newest) published one that satisfies them and supports every target framework of the
/// projects using it; families share the highest member version. Deciding is not resolving:
/// NuGet's own restore verifies the result (<see cref="RestoreVerifier"/>).
/// </summary>
public static class Consolidator
{
    private const int MaxRounds = 8;

    public static async Task<ConsolidationPlan?> PlanAsync(ConsolidateRequest request, CancellationToken cancellationToken)
    {
        var model = request.Model;
        var ignored = request.Config.Deps.Ignore.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = model.Packages.Keys.Where(k => !ignored.Contains(k)).ToList();
        if (request.Package is { } asked && !referenced.Contains(asked, StringComparer.OrdinalIgnoreCase))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR1200, $"No project references {asked}.",
                data: [KeyValuePair.Create<string, JsonNode?>("package", asked)]);
            return null;
        }

        var families = new FamilyTable(request.Config.Deps.Families);
        var selection = Select(request, referenced, families);
        var graph = new ResolvedGraph(model);
        var context = new Context(request, graph, families);

        var decisions = new SortedDictionary<string, Decision>(StringComparer.OrdinalIgnoreCase);
        using (var phase = request.Progress.BeginPhase("Choosing versions", 1, 1))
        {
            var selected = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            for (var round = 0; round < MaxRounds; round++)
            {
                decisions.Clear();
                foreach (var id in selection)
                {
                    phase.Report(decisions.Count, selection.Count, id);
                    decisions[id] = await context.DecideAsync(id, await context.SelectedConstraintsAsync(id, selected, cancellationToken), cancellationToken);
                }

                Context.AlignFamilies(decisions);
                await context.CheckFamiliesAsync(decisions, cancellationToken);
                var next = decisions.Where(d => d.Value.Version is not null).ToDictionary(d => d.Key, d => d.Value.Version!, StringComparer.OrdinalIgnoreCase);
                if (next.Count == selected.Count && next.All(n => selected.TryGetValue(n.Key, out var v) && v == n.Value))
                {
                    break;
                }

                selected = next;
            }
        }

        var packages = new List<PackageConsolidation>();
        var unsatisfiable = new List<UnsatisfiableConsolidation>();
        foreach (var decision in decisions.Values)
        {
            if (decision.MissingFamilyVersion is { } missing)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR1220,
                    $"{decision.Id} has no version {missing.ToNormalizedString()}, the {decision.Family}* family's; it keeps its own.",
                    data: [KeyValuePair.Create<string, JsonNode?>("package", decision.Id)]);
            }

            context.ReportPins(decision);
            if (decision.Version is null)
            {
                unsatisfiable.Add(context.Unsatisfiable(decision));
            }

            packages.Add(decision.ToResult());
        }

        var writer = new ConsolidationWriter(request);
        var (changeSet, cpm, hazards) = writer.Write(packages);
        packages = [.. packages.Select(p => p with { Changes = writer.ChangesFor(p.Id) })];
        var result = new ConsolidateResult
        {
            Target = request.Config.TargetFramework,
            Prefer = request.Prefer,
            Packages = packages,
            Unsatisfiable = unsatisfiable,
            Cpm = cpm,
            Hazards = hazards,
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new ConsolidationPlan(result, changeSet.IsEmpty ? null : changeSet);
    }

    /// <summary>The packages to consolidate: all, one (with its family), or a family by prefix; sorted.</summary>
    private static SortedSet<string> Select(ConsolidateRequest request, List<string> referenced, FamilyTable families)
    {
        var selection = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in referenced)
        {
            var include = request.Package is null && request.Family is null
                || string.Equals(id, request.Package, StringComparison.OrdinalIgnoreCase)
                || (request.Family is not null && id.StartsWith(request.Family, StringComparison.OrdinalIgnoreCase));
            if (include)
            {
                selection.Add(id);
            }
        }

        // A family moves together.
        foreach (var id in selection.ToList())
        {
            if (families.KeyOf(id) is { } key)
            {
                selection.UnionWith(referenced.Where(r => families.KeyOf(r) == key));
            }
        }

        return selection;
    }

    /// <summary>A package's decision while choosing.</summary>
    internal sealed class Decision
    {
        public required string Id { get; init; }

        public string? Family { get; init; }

        public required IReadOnlyList<VersionInUse> Current { get; init; }

        public required List<VersionConstraint> Constraints { get; init; }

        public required IReadOnlyList<(string Project, NuGetVersion Version, string? Reason)> ProjectPins { get; init; }

        public NuGetVersion? GlobalPin { get; init; }

        public NuGetVersion? Version { get; set; }

        public string Reason { get; set; } = "";

        public VersionConstraint? Deciding { get; set; }

        /// <summary>The family version this member has not published (<c>OFR1220</c>), or null.</summary>
        public NuGetVersion? MissingFamilyVersion { get; set; }

        /// <summary>The code and chain when no version works.</summary>
        public (string Code, string Message, IReadOnlyList<string> Chain)? Failure { get; set; }

        public PackageConsolidation ToResult() => new()
        {
            Id = Id,
            Family = Family,
            Current = Current,
            Selected = Version?.ToNormalizedString(),
            Reason = Reason,
            Deciding = Deciding,
            Constraints = [.. Constraints.OrderBy(c => c.Kind).ThenBy(c => c.Project ?? "", StringComparer.Ordinal).ThenBy(c => c.Range, StringComparer.Ordinal)],
            Pinned = [.. ProjectPins.GroupBy(p => p.Version.ToNormalizedString()).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new VersionInUse(g.Key, [.. g.Select(p => p.Project).Order(StringComparer.Ordinal)]))],
        };
    }

    private sealed class Context(ConsolidateRequest request, ResolvedGraph graph, FamilyTable families)
    {
        private readonly Dictionary<string, PackageVersions> _versions = new(StringComparer.OrdinalIgnoreCase);
        private readonly PackageInspections _inspections = new(request.Feeds, request.Cache);

        private WorkspaceModel Model => request.Model;

        public async Task<Decision> DecideAsync(string id, List<VersionConstraint> selectedConstraints, CancellationToken cancellationToken)
        {
            var usage = Model.Packages.First(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
            var pins = request.Config.Deps.Pins.Where(p => string.Equals(p.Package, id, StringComparison.OrdinalIgnoreCase)).ToList();
            var projectPins = pins.Where(p => p.Project is not null && NuGetVersion.TryParse(p.Version, out _))
                .Select(p => (Project: p.Project!.Replace('\\', '/'), Version: NuGetVersion.Parse(p.Version), p.Reason))
                .OrderBy(p => p.Project, StringComparer.Ordinal)
                .ToList();
            var globalPin = pins.FirstOrDefault(p => p.Project is null) is { } global && NuGetVersion.TryParse(global.Version, out var pinned) ? pinned : null;
            var pinnedProjects = projectPins.Select(p => p.Project).ToHashSet(StringComparer.Ordinal);

            var constraints = new List<VersionConstraint>();
            foreach (var (version, projects) in usage.Versions)
            {
                foreach (var project in projects.Where(p => !pinnedProjects.Contains(p)))
                {
                    constraints.Add(new VersionConstraint { Kind = ConstraintKind.Direct, Project = project, Range = $"[{version}, )", Chain = [$"{project} references {id} {version}"] });
                }
            }

            constraints.AddRange(graph.TransitiveConstraints(id).Where(c => !pinnedProjects.Contains(c.Project!)));
            constraints.AddRange(selectedConstraints);
            var decision = new Decision
            {
                Id = id,
                Family = families.KeyOf(id),
                Current = [.. usage.Versions.Select(v => new VersionInUse(v.Key, v.Value))],
                Constraints = constraints,
                ProjectPins = projectPins,
                GlobalPin = globalPin,
            };
            if (globalPin is not null)
            {
                constraints.Add(new VersionConstraint { Kind = ConstraintKind.Pin, Range = $"[{globalPin.ToNormalizedString()}]", Chain = [$"offramp.yml pins {id} to {globalPin.ToNormalizedString()}"] });
            }

            var tfms = Tfms(id, pinnedProjects);
            var chosen = await ChooseAsync(id, constraints, tfms, request.Prefer == "newest", cancellationToken);
            if (chosen is null)
            {
                decision.Failure = Failure(id, constraints, globalPin);
                decision.Reason = decision.Failure.Value.Message;
                return decision;
            }

            decision.Version = chosen;
            decision.Deciding = Deciding(constraints, chosen);
            decision.Reason = Reason(chosen, decision.Deciding, tfms);
            return decision;
        }

        /// <summary>Ranges the selected versions of the other consolidated packages ask for this one.</summary>
        public async Task<List<VersionConstraint>> SelectedConstraintsAsync(string id, Dictionary<string, NuGetVersion> selected, CancellationToken cancellationToken)
        {
            var constraints = new List<VersionConstraint>();
            foreach (var (other, version) in selected.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(other, id, StringComparison.OrdinalIgnoreCase) || await InspectAsync(other, version, cancellationToken) is not { } inspection)
                {
                    continue;
                }

                foreach (var tfm in Tfms(other, new HashSet<string>(StringComparer.Ordinal)))
                {
                    var dependency = TargetSupport.Dependencies(inspection, tfm).FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (dependency is not null && !constraints.Any(c => c.Range == dependency.Range && c.Chain[0].StartsWith(other + " ", StringComparison.Ordinal)))
                    {
                        constraints.Add(new VersionConstraint
                        {
                            Kind = ConstraintKind.Selected,
                            Range = dependency.Range,
                            Chain = [$"{other} {version.ToNormalizedString()}", $"{id} {Describe(dependency.Range)}"],
                        });
                    }
                }
            }

            return constraints;
        }

        /// <summary>Family members share the highest member version, where every member can take it.</summary>
        public static void AlignFamilies(SortedDictionary<string, Decision> decisions)
        {
            foreach (var family in decisions.Values.Where(d => d.Family is not null && d.Version is not null).GroupBy(d => d.Family!, StringComparer.Ordinal))
            {
                var leader = family.OrderByDescending(d => d.Version).ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase).First();
                foreach (var member in family.Where(m => m.Version! < leader.Version!))
                {
                    member.Version = leader.Version;
                    member.Reason = $"Aligned with the {family.Key}* family at {leader.Version!.ToNormalizedString()}, which {leader.Id} needs: {leader.Reason}";
                    member.Deciding = leader.Deciding;
                }
            }
        }

        /// <summary>A member whose family version is not published, or does not satisfy it, keeps its own version (<c>OFR1220</c>).</summary>
        public async Task CheckFamiliesAsync(SortedDictionary<string, Decision> decisions, CancellationToken cancellationToken)
        {
            foreach (var member in decisions.Values.Where(d => d.Family is not null && d.Version is not null))
            {
                var available = await VersionsAsync(member.Id, cancellationToken);
                if (available.Versions.Any(v => v.Version == member.Version))
                {
                    continue;
                }

                member.MissingFamilyVersion = member.Version;
                var own = await ChooseAsync(member.Id, member.Constraints, Tfms(member.Id, member.ProjectPins.Select(p => p.Project).ToHashSet(StringComparer.Ordinal)), request.Prefer == "newest", cancellationToken);
                member.Version = own;
                member.Deciding = own is null ? null : Deciding(member.Constraints, own);
                member.Reason = own is null ? "No version satisfies every constraint." : Reason(own, member.Deciding, []);
            }
        }

        /// <summary>Pins below the selected version (<c>OFR1203</c>), and pins a project's own graph cannot take (<c>OFR1210</c>).</summary>
        public void ReportPins(Decision decision)
        {
            foreach (var (project, version, reason) in decision.ProjectPins)
            {
                var conflict = graph.TransitiveConstraints(decision.Id)
                    .Where(c => c.Project == project && !VersionRange.Parse(c.Range).Satisfies(version))
                    .OrderByDescending(c => VersionRange.Parse(c.Range).MinVersion)
                    .FirstOrDefault();
                if (conflict is not null)
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR1210,
                        $"The pin keeps {decision.Id} at {version.ToNormalizedString()} in {project}, but {string.Join(" → ", conflict.Chain)}. Isolate {project} from that dependency, or add NoWarn NU1605 there with a recorded reason.",
                        new DiagnosticLocation(project),
                        [KeyValuePair.Create<string, JsonNode?>("chain", new JsonArray([.. conflict.Chain.Select(c => (JsonNode?)c)]))]);
                }
                else if (decision.Version is not null && version < decision.Version)
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR1203,
                        $"Stays on {decision.Id} {version.ToNormalizedString()} (pinned{(reason is null ? "" : ": " + reason)}); the rest move to {decision.Version.ToNormalizedString()}.",
                        new DiagnosticLocation(project));
                }
            }
        }

        public UnsatisfiableConsolidation Unsatisfiable(Decision decision)
        {
            var (code, message, chain) = decision.Failure!.Value;
            if (code == DiagnosticCatalog.OFR1210.Code)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR1210, message, data: [KeyValuePair.Create<string, JsonNode?>("chain", new JsonArray([.. chain.Select(c => (JsonNode?)c)]))]);
            }
            else
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR1212, message, data: [KeyValuePair.Create<string, JsonNode?>("package", decision.Id)]);
            }

            return new UnsatisfiableConsolidation { Id = decision.Id, Code = code, Message = message, Chain = chain };
        }

        private static (string Code, string Message, IReadOnlyList<string> Chain) Failure(string id, List<VersionConstraint> constraints, NuGetVersion? globalPin)
        {
            if (globalPin is not null)
            {
                var violated = constraints.Where(c => c.Kind != ConstraintKind.Pin && !VersionRange.Parse(c.Range).Satisfies(globalPin))
                    .OrderByDescending(c => VersionRange.Parse(c.Range).MinVersion).FirstOrDefault();
                if (violated is not null)
                {
                    var where = violated.Project is null ? "" : violated.Project + ": ";
                    return (DiagnosticCatalog.OFR1210.Code,
                        $"offramp.yml pins {id} to {globalPin.ToNormalizedString()}, but {where}{string.Join(" → ", violated.Chain)}. Isolate that project from the dependency, or add NoWarn NU1605 there with a recorded reason.",
                        violated.Chain);
                }
            }

            var floor = constraints.Where(c => VersionRange.Parse(c.Range).HasLowerBound).OrderByDescending(c => VersionRange.Parse(c.Range).MinVersion).FirstOrDefault();
            return (DiagnosticCatalog.OFR1212.Code,
                $"No published version of {id} satisfies every constraint and supports every target framework of the projects using it{(floor is null ? "" : $" (the highest lower bound: {string.Join(" → ", floor.Chain)})")}.",
                floor?.Chain ?? []);
        }

        private async Task<NuGetVersion?> ChooseAsync(string id, IReadOnlyList<VersionConstraint> constraints, IReadOnlyList<NuGetFramework> tfms, bool newest, CancellationToken cancellationToken)
        {
            var ranges = constraints.Select(c => VersionRange.Parse(c.Range)).ToList();
            var available = await VersionsAsync(id, cancellationToken);
            var inUse = Model.Packages.First(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value.Versions.Keys.Select(NuGetVersion.Parse).ToHashSet();
            var candidates = available.Versions
                .Where(v => inUse.Contains(v.Version) || (v.Listed && (request.Config.Deps.IncludePrerelease || !v.Version.IsPrerelease)))
                .Select(v => v.Version)
                .Where(v => ranges.All(r => r.Satisfies(v)));
            foreach (var version in newest ? candidates.OrderDescending() : candidates.Order())
            {
                if (await InspectAsync(id, version, cancellationToken) is { } inspection && tfms.All(t => TargetSupport.Supports(inspection, t)))
                {
                    return version;
                }
            }

            return null;
        }

        /// <summary>The target frameworks of the projects referencing a package (pinned ones aside).</summary>
        private List<NuGetFramework> Tfms(string id, HashSet<string> pinnedProjects)
        {
            var usage = Model.Packages.First(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
            var projects = usage.Versions.Values.SelectMany(p => p).Where(p => !pinnedProjects.Contains(p)).ToHashSet(StringComparer.Ordinal);
            return [.. Model.Projects.Where(p => projects.Contains(p.Id)).SelectMany(p => p.TargetFrameworks)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(NuGetFramework.Parse)];
        }

        private async Task<PackageVersions> VersionsAsync(string id, CancellationToken cancellationToken)
        {
            if (!_versions.TryGetValue(id, out var versions))
            {
                versions = await request.Feeds.GetVersionsAsync(id, cancellationToken);
                _versions[id] = versions;
            }

            return versions;
        }

        private Task<PackageInspection?> InspectAsync(string id, NuGetVersion version, CancellationToken cancellationToken) =>
            _inspections.GetAsync(id, version, cancellationToken);

        private static VersionConstraint? Deciding(List<VersionConstraint> constraints, NuGetVersion chosen) =>
            constraints.Where(c => VersionRange.Parse(c.Range).HasLowerBound)
                .OrderByDescending(c => VersionRange.Parse(c.Range).MinVersion)
                .ThenBy(c => c.Kind)
                .ThenBy(c => c.Project ?? "", StringComparer.Ordinal)
                .FirstOrDefault(c => VersionRange.Parse(c.Range).MinVersion! <= chosen);

        private string Reason(NuGetVersion chosen, VersionConstraint? deciding, List<NuGetFramework> tfms)
        {
            var targets = tfms.Count == 0 ? "" : " that supports " + string.Join(", ", tfms.Select(t => t.GetShortFolderName()));
            var which = request.Prefer == "newest" ? "The newest version" : "The lowest version";
            if (deciding is null)
            {
                return $"{which}{targets}.";
            }

            var floor = VersionRange.Parse(deciding.Range).MinVersion!;
            var because = deciding.Kind switch
            {
                ConstraintKind.Direct => string.Join(" → ", deciding.Chain),
                ConstraintKind.Pin => string.Join(" → ", deciding.Chain),
                _ => (deciding.Project is null ? "" : deciding.Project + ": ") + string.Join(" → ", deciding.Chain),
            };
            return floor == chosen
                ? $"{because}."
                : $"{which} from {floor.ToNormalizedString()}{targets} ({because}).";
        }
    }

    internal static string Describe(string range)
    {
        var parsed = VersionRange.Parse(range);
        return parsed.HasLowerBound && !parsed.HasUpperBound && parsed.IsMinInclusive ? ">= " + parsed.MinVersion!.ToNormalizedString() : parsed.PrettyPrint();
    }
}

/// <summary>Families from <c>deps.families</c>: the longest matching prefix names a package's family.</summary>
internal sealed class FamilyTable(IReadOnlyList<PackageFamily> families)
{
    public string? KeyOf(string id) =>
        families.Where(f => f.Prefix.Length > 0 && id.StartsWith(f.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Prefix.Length)
            .Select(f => f.Family ?? f.Prefix)
            .FirstOrDefault();
}

/// <summary>Every project's resolved packages (project.assets.json, as scanned), for dependency ranges and their chains.</summary>
internal sealed class ResolvedGraph(WorkspaceModel model)
{
    private readonly Dictionary<string, List<VersionConstraint>> _transitive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each resolved package's range for <paramref name="id"/>, in every project and target framework, with the path from a direct reference.</summary>
    public IReadOnlyList<VersionConstraint> TransitiveConstraints(string id)
    {
        if (_transitive.TryGetValue(id, out var known))
        {
            return known;
        }

        var constraints = new List<VersionConstraint>();
        foreach (var project in model.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            foreach (var (_, framework) in project.Resolved.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                var byId = framework.Packages.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var package in framework.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
                {
                    var dependency = package.Dependencies.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (dependency is null)
                    {
                        continue;
                    }

                    var chain = PathTo(package, byId).Select(p => $"{p.Id} {p.Version}").Append($"{id} {Consolidator.Describe(dependency.Range)}").ToList();
                    if (!constraints.Any(c => c.Project == project.Id && c.Range == dependency.Range && c.Chain.SequenceEqual(chain)))
                    {
                        constraints.Add(new VersionConstraint { Kind = ConstraintKind.Transitive, Project = project.Id, Range = dependency.Range, Chain = chain });
                    }
                }
            }
        }

        _transitive[id] = constraints;
        return constraints;
    }

    /// <summary>The shortest path from a direct reference to a package, both included.</summary>
    private static List<ResolvedPackage> PathTo(ResolvedPackage target, Dictionary<string, ResolvedPackage> byId)
    {
        var previous = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ResolvedPackage>(byId.Values.Where(p => p.Direct).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase));
        var seen = queue.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (string.Equals(next.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            foreach (var dependency in next.Dependencies.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (byId.TryGetValue(dependency.Id, out var resolved) && seen.Add(resolved.Id))
                {
                    previous[resolved.Id] = next;
                    queue.Enqueue(resolved);
                }
            }
        }

        var path = new List<ResolvedPackage> { target };
        for (var node = target; previous.TryGetValue(node.Id, out var back); node = back)
        {
            path.Insert(0, back);
        }

        return path;
    }
}
