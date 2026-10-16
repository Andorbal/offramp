using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Versioning;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Progress;
using Graph = Offramp.Analysis.CallGraph.CallGraph;

namespace Offramp.Analysis.Newtonsoft;

public sealed record NewtonsoftScanRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary>The release the projects are judged against.</summary>
    public required NuGetVersion Target { get; init; }

    /// <summary>The target's .nupkg, for the trial compilation; null when it could not be had (OFR3725).</summary>
    public byte[]? TargetPackage { get; init; }

    /// <summary>Project ids to scan; empty for every project that uses Newtonsoft.Json.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>Include projects outside deployed application closures, as with <c>--all-projects</c> or <c>newtonsoft.scope: all</c>.</summary>
    public bool AllProjects { get; init; }

    public IReadOnlyDictionary<string, SeverityOverride> Overrides { get; init; } = new Dictionary<string, SeverityOverride>();

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary>How many callers up the entry-point search goes (<c>newtonsoft.tests.entryDepth</c>).</summary>
    public int EntryDepth { get; init; } = 4;

    /// <summary>Documentation IDs added to <c>rules/newtonsoft-hermetic.yml</c> (<c>newtonsoft.hermetic.nonHermetic</c>).</summary>
    public IReadOnlyList<string> NonHermetic { get; init; } = [];

    /// <summary>Where compilations come from; null for the recorded compiler logs.</summary>
    public ICompilationSource? Compilations { get; init; }

    /// <summary>
    /// False when only the sites and their entry points are wanted (<c>newtonsoft tests</c>): the behavior detectors
    /// and the trial compilation against the target, whose findings would be thrown away, are not run (issue #54).
    /// </summary>
    internal bool Findings { get; init; } = true;
}

/// <summary>A scanned project with what <c>newtonsoft tests</c> generates probes from.</summary>
internal sealed record ScannedProject(ProjectInfo Project, NewtonsoftReference Reference, CSharpCompilation Compilation, IReadOnlyList<FoundSite> Sites)
{
    public DeploymentScope Deployment { get; init; } = new(Project.Id, [], true, []);
}

/// <summary><c>offramp newtonsoft scan</c> (<c>docs/spec/commands/newtonsoft.md</c>).</summary>
public static class NewtonsoftScanner
{
    public static NewtonsoftScanResult Scan(NewtonsoftScanRequest request, CancellationToken cancellationToken = default) =>
        Scan(request, out _, cancellationToken);

    /// <summary>The scan, with each project's compilation and sites for <c>newtonsoft tests</c>.</summary>
    internal static NewtonsoftScanResult Scan(NewtonsoftScanRequest request, out IReadOnlyList<ScannedProject> scanned, CancellationToken cancellationToken = default)
    {
        var projectsScanned = new List<ScannedProject>();
        scanned = projectsScanned;
        using var owned = request.Compilations is null ? new CompilationLoader(request.RepositoryRoot) : null;

        // The call graph loads every project's compilation; the scan of each project reuses it (issue #54).
        var compilations = SharedCompilations.Over(request.Compilations ?? owned!);
        var scope = NewtonsoftDeploymentScope.Compute(request.Model, request.Target);
        var allCandidates = request.Model.Projects
            .Where(p => p.Language == "csharp" && (request.Projects.Count == 0 || request.Projects.Contains(p.Id, StringComparer.Ordinal)))
            .Select(p => (Project: p, Tfm: CompilationLoader.PreferredTarget(p)))
            .Where(p => p.Tfm is not null)
            .Select(p => (p.Project, Tfm: p.Tfm!, Reference: NewtonsoftReferences.For(p.Project, p.Tfm!)))
            .Where(p => p.Reference is not null)
            .OrderBy(p => p.Project.Id, StringComparer.Ordinal)
            .ToList();
        var candidates = allCandidates.Where(p => request.AllProjects || scope[p.Project.Id].InScope).ToList();
        var outOfScope = allCandidates.Where(p => !request.AllProjects && !scope[p.Project.Id].InScope).ToList();
        if (outOfScope.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3726,
                $"{outOfScope.Count} project{(outOfScope.Count == 1 ? " is" : "s are")} out of scope: no deployed application runs a Newtonsoft.Json release below {request.Target}; use --all-projects to include them.",
                new DiagnosticLocation(outOfScope[0].Project.Id),
                [KeyValuePair.Create<string, JsonNode?>("projects", new JsonArray([.. outOfScope.Select(p => (JsonNode?)p.Project.Id)]))]);
        }

        if (request.TargetPackage is null && request.Findings && candidates.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3725,
                $"Newtonsoft.Json {request.Target} is in neither the global packages folder nor the configured feeds; API changes (OFR3701, OFR3702) were not computed.");
        }

        Versions(request, candidates.Select(c => (c.Project, c.Reference!)).ToList());

        var reports = new List<NewtonsoftProjectReport>();
        foreach (var candidate in outOfScope)
        {
            var deployment = scope[candidate.Project.Id];
            reports.Add(new NewtonsoftProjectReport
            {
                Project = candidate.Project.Id,
                TargetFramework = candidate.Tfm,
                Newtonsoft = candidate.Reference!,
                Redirects = NewtonsoftReferences.Redirects(request.RepositoryRoot, candidate.Project),
                DeployedReleases = deployment.DeployedReleases,
                InScope = false,
                DeployedBy = deployment.DeployedBy,
            });
        }
        var findings = new List<NewtonsoftFinding>();
        var skipped = new List<string>();
        EntryPointFinder entryPoints;
        using (request.Progress.BeginPhase("call graph", 1, 2))
        {
            entryPoints = new EntryPointFinder(request.RepositoryRoot, Graph.Build(Compilations(request, compilations)), request.Model, request.EntryDepth,
                NewtonsoftRules.NonHermetic.Concat(request.NonHermetic).ToHashSet(StringComparer.Ordinal));
        }

        using (var phase = request.Progress.BeginPhase("newtonsoft scan", 2, 2))
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (project, tfm, reference) = candidates[i];
                phase.Report(i, candidates.Count, project.Id);
                if (compilations.LoadForProject(project, tfm) is not CSharpCompilation compilation)
                {
                    skipped.Add($"{project.Id}: no compiler call for {tfm}; run `offramp scan` with a build that compiles it");
                    continue;
                }

                var (report, projectFindings, sites) = ScanProject(request, project, tfm, reference!, compilation, entryPoints, scope[project.Id]);
                reports.Add(report);
                projectsScanned.Add(new ScannedProject(project, reference!, compilation, sites) { Deployment = scope[project.Id] });
                findings.AddRange(projectFindings);
            }

            phase.Report(candidates.Count, candidates.Count);
        }

        findings = [.. findings
            .OrderBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.Project, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ThenBy(f => f.Case, StringComparer.Ordinal)
            .ThenBy(f => f.Symbol, StringComparer.Ordinal)];
        Report(request, findings);
        return new NewtonsoftScanResult
        {
            Target = request.Target.ToNormalizedString(),
            Projects = [.. reports.OrderBy(r => r.Project, StringComparer.Ordinal)],
            Findings = findings,
            Summary = new NewtonsoftScanSummary
            {
                Projects = reports.Count,
                Sites = reports.Sum(r => r.Sites.Count),
                Findings = new SortedDictionary<string, int>(StringComparer.Ordinal)
                {
                    ["error"] = findings.Count(f => f.Severity == Severity.Error),
                    ["warning"] = findings.Count(f => f.Severity == Severity.Warning),
                    ["info"] = findings.Count(f => f.Severity == Severity.Info),
                },
            },
            Skipped = skipped,
        };
    }

    /// <summary>Every C# project's compilation for its preferred target, test projects aside (their calls are not entry points).</summary>
    private static IEnumerable<(ProjectInfo, Compilation)> Compilations(NewtonsoftScanRequest request, SharedCompilations compilations)
    {
        foreach (var project in request.Model.Projects.Where(p => p.Language == "csharp" && !p.IsTestProject && p.Kind != ProjectKind.Test).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (CompilationLoader.PreferredTarget(project) is { } tfm && compilations.LoadForProject(project, tfm) is { } compilation)
            {
                yield return (project, compilation);
            }
        }
    }

    private static (NewtonsoftProjectReport, List<NewtonsoftFinding>, IReadOnlyList<FoundSite>) ScanProject(
        NewtonsoftScanRequest request, ProjectInfo project, string tfm, NewtonsoftReference reference, CSharpCompilation compilation, EntryPointFinder entryPoints,
        DeploymentScope deployment)
    {
        var finder = new NewtonsoftSiteFinder(request.RepositoryRoot, compilation);
        finder.Find();
        for (var i = 0; i < finder.Sites.Count; i++)
        {
            // A test project's code is not in the call graph: say why, not that the member is missing (issue #52).
            var (entry, uncovered) = project.IsTestProject || project.Kind == ProjectKind.Test
                ? (null, new SiteUncovered
                {
                    Reason = UncoveredReason.NotAccessible,
                    Detail = $"{project.Id} is a test project ({project.KindEvidence}), and test code is not searched for entry points; if it is production code, set its kind in offramp.yml",
                    Candidates = [],
                })
                : entryPoints.Find(finder.Sites[i]);
            finder.Sites[i] = finder.Sites[i] with { Site = finder.Sites[i].Site with { EntryPoint = entry, Uncovered = uncovered } };
        }

        var report = new NewtonsoftProjectReport
        {
            Project = project.Id,
            TargetFramework = tfm,
            Newtonsoft = reference,
            Redirects = NewtonsoftReferences.Redirects(request.RepositoryRoot, project),
            DeployedReleases = deployment.DeployedReleases,
            InScope = deployment.InScope,
            DeployedBy = deployment.DeployedBy,
            Sites = [.. finder.Sites.Select(s => s.Site)],
            Configuration = finder.Configuration,
        };

        if (request.Findings)
        {
            SiteDiagnostics(request, project, finder);
        }

        if (!request.Findings)
        {
            return (report, [], finder.Sites);
        }

        var current = NewtonsoftReferences.Release(reference);
        if (reference.Via == NewtonsoftReferenceKind.LooseDll)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3723,
                $"{project.Id} references {reference.HintPath}, which no package provides (assembly version {reference.Version}); the upgrade cannot edit it. Run `offramp deps resolve-dlls` first.",
                new DiagnosticLocation(project.Id));
            return (report, [], finder.Sites);
        }

        var behaviorVersions = request.AllProjects
            ? current is { } ownProjectRelease && ownProjectRelease < request.Target ? [ownProjectRelease] : []
            : deployment.DeployedReleases.Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
                .OfType<NuGetVersion>().Where(v => v < request.Target).DistinctBy(v => v.ToNormalizedString()).OrderBy(v => v, VersionComparer.VersionRelease).ToList();
        if (behaviorVersions.Count == 0 && current is { } own && own < request.Target)
        {
            behaviorVersions.Add(own);
        }

        if (current is null || (behaviorVersions.Count == 0 && current >= request.Target))
        {
            if (current == request.Target)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3724, $"{project.Id} already within the target range; left unchanged.", new DiagnosticLocation(project.Id));
            }

            return (report, [], finder.Sites);
        }

        var trees = AuditEngine.Sources(compilation);
        var raw = behaviorVersions.SelectMany(version => NewtonsoftDetectors.Run(new DetectorContext(compilation, trees, finder.Sites, finder.DefaultSettings, version, request.Target))
                .Select(finding => (Finding: finding, Version: version)))
            .ToList();
        var distinct = raw.DistinctBy(item => (item.Finding.Rule.Id, item.Finding.Case?.Name, item.Finding.Symbol,
            Label: item.Finding.Case?.LabelFor(item.Version))).ToList();
        var findings = distinct.Select(item => ToFinding(request, project, item.Finding, item.Version)).OfType<NewtonsoftFinding>().ToList();
        if (current is { } ownRelease && ownRelease < request.Target && request.TargetPackage is not null
            && TargetTrialCompile.Assembly(request.TargetPackage, tfm) is { } target)
        {
            findings.AddRange(TargetTrialCompile.Run(request.RepositoryRoot, compilation, target, trees)
                .Select(r => ToFinding(request, project, r, ownRelease)).OfType<NewtonsoftFinding>());
        }

        return (report, findings, finder.Sites);
    }

    /// <summary>OFR3720 for sites whose payload is unknown; OFR3721 for settings a test cannot recreate and for DefaultSettings.</summary>
    private static void SiteDiagnostics(NewtonsoftScanRequest request, ProjectInfo project, NewtonsoftSiteFinder finder)
    {
        var unknown = finder.Sites.Where(s => s.Site.Direction != SiteDirection.Dom && s.Site.PayloadTypes.Count == 0).Select(s => s.Site).ToList();
        if (unknown.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3720,
                $"The payload type of site {unknown[0].Id} is unknown" + (unknown.Count == 1 ? "." : $" ({unknown.Count} sites in the project; the first is shown)."),
                new DiagnosticLocation(project.Id, unknown[0].File, unknown[0].Line),
                [KeyValuePair.Create<string, JsonNode?>("sites", new JsonArray([.. unknown.Select(s => (JsonNode?)s.Id)]))]);
        }

        var irreproducible = finder.Sites.Where(s => s.Site.Settings.Kind == SettingsKind.Unknown).Select(s => s.Site).ToList();
        if (irreproducible.Count > 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3721,
                $"The settings of site {irreproducible[0].Id} cannot be recreated by a test" + (irreproducible.Count == 1 ? "." : $" ({irreproducible.Count} sites in the project; the first is shown)."),
                new DiagnosticLocation(project.Id, irreproducible[0].File, irreproducible[0].Line),
                [KeyValuePair.Create<string, JsonNode?>("sites", new JsonArray([.. irreproducible.Select(s => (JsonNode?)s.Id)]))]);
        }

        if (finder.Configuration.FirstOrDefault(c => c.Kind == ConfigurationKind.DefaultSettings) is { } defaults)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3721,
                "JsonConvert.DefaultSettings is assigned: every call without settings depends on whether this ran first, which probes cannot reproduce.",
                new DiagnosticLocation(project.Id, defaults.File, defaults.Line));
        }
    }

    /// <summary>OFR3722 when projects use different releases.</summary>
    private static void Versions(NewtonsoftScanRequest request, List<(ProjectInfo Project, NewtonsoftReference Reference)> projects)
    {
        var versions = projects.GroupBy(p => p.Reference.Version, StringComparer.OrdinalIgnoreCase).OrderBy(g => NuGetVersion.TryParse(g.Key, out var v) ? v : new NuGetVersion(0, 0, 0)).ToList();
        if (versions.Count > 1)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR3722,
                "Projects use Newtonsoft.Json " + string.Join(", ", versions.Select(g => $"{g.Key} ({g.Count()} project{(g.Count() == 1 ? "" : "s")})")) + "; each is judged against deployed releases.",
                new DiagnosticLocation(versions[0].First().Project.Id));
        }
    }

    private static NewtonsoftFinding? ToFinding(NewtonsoftScanRequest request, ProjectInfo project, RawNewtonsoftFinding raw, NuGetVersion current)
    {
        var severity = raw.Rule.Severity;
        var overridden = false;
        if (request.Overrides.TryGetValue(raw.Rule.Id, out var @override))
        {
            if (@override.Severity is not { } replaced)
            {
                return null;
            }

            overridden = replaced != severity;
            severity = replaced;
        }

        var (file, line, column) = AuditEngine.Position(request.RepositoryRoot, raw.Location);
        return new NewtonsoftFinding
        {
            Rule = raw.Rule.Id,
            Case = raw.Case?.Name,
            Severity = severity,
            Overridden = overridden,
            Project = project.Id,
            File = file,
            Line = line,
            Column = column,
            Symbol = raw.Symbol,
            Message = raw.Message,
            Recommendation = raw.Rule.Recommendation,
            Note = raw.Case?.Note,
            Current = raw.Case?.LabelFor(current),
            Target = raw.Case?.LabelFor(request.Target),
            Details = raw.Details,
        };
    }

    /// <summary>One diagnostic per rule and project, at the first finding, with the count.</summary>
    private static void Report(NewtonsoftScanRequest request, List<NewtonsoftFinding> findings)
    {
        foreach (var group in findings.GroupBy(f => (f.Rule, f.Project)))
        {
            var descriptor = DiagnosticCatalog.Find(group.Key.Rule) ?? throw new InvalidOperationException($"{group.Key.Rule} is not in the diagnostic catalog.");
            var first = group.First();
            var count = group.Count();
            var message = count == 1 ? first.Message : $"{first.Message} ({count} findings in the project; the first is shown.)";
            request.Diagnostics.Report(descriptor, message, new DiagnosticLocation(first.Project, first.File, first.Line, first.Column),
                [KeyValuePair.Create<string, JsonNode?>("findings", count), KeyValuePair.Create<string, JsonNode?>("symbol", first.Symbol)],
                first.Severity);
        }
    }
}
