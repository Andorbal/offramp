using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Audits.Matchers;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Core.Progress;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Audits;

/// <summary>What to audit and how.</summary>
public sealed record AuditRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    public required AuditKind Audit { get; init; }

    public required int TargetMajor { get; init; }

    /// <summary>Project ids or names; empty for every C# project.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary><c>--pack</c>: only these packs (overrides <see cref="DisabledPacks"/>).</summary>
    public IReadOnlyList<string> Packs { get; init; } = [];

    /// <summary><c>rules.packs.disable</c> from offramp.yml.</summary>
    public IReadOnlyList<string> DisabledPacks { get; init; } = [];

    /// <summary>Per-rule severity overrides from offramp.yml (<c>none</c> disables a rule).</summary>
    public IReadOnlyDictionary<string, SeverityOverride> Overrides { get; init; } = new Dictionary<string, SeverityOverride>();

    public required DiagnosticBag Diagnostics { get; init; }

    public IProgressSink Progress { get; init; } = NullProgressSink.Instance;

    /// <summary><c>audit api</c>: resolves the target's reference assemblies. Without it, OFR3001/OFR3002 do not run.</summary>
    public TargetReferenceResolver? References { get; init; }
}

/// <summary>
/// Runs one audit over the workspace: each C# project's recorded compilation (its .NET
/// Framework target when it has one) goes through the rule engine; <c>audit api</c> also
/// compiles each .NET Framework project against the target. Findings are sorted; each rule
/// with findings in a project is one diagnostic, so <c>--fail-on</c> sees the audit.
/// </summary>
public static class AuditRunner
{
    private static readonly Dictionary<string, IAuditMatcher> Matchers = new IAuditMatcher[]
    {
        new TargetCompilationMatcher(), new CultureSensitiveStringMatcher(), new EncodingCodePageMatcher(), new WindowsPathMatcher(),
        new WindowsTimeZoneMatcher(), new FloatingPointToStringMatcher(), new RegexWithoutTimeoutMatcher(), new DelegateBeginInvokeMatcher(),
        new ConfigRuntimeSettingsMatcher(), new SerializationFlowMatcher(), new NativeImportsMatcher(), new ComReferencesMatcher(),
    }.ToDictionary(m => m.Name, StringComparer.Ordinal);

    private static readonly SerializableUnusedMatcher SerializableUnused = new();

    public static async Task<AuditResult> RunAsync(AuditRequest request, CancellationToken cancellationToken = default)
    {
        var rules = ActiveRules(request);
        var (projects, skipped) = Select(request);
        var target = $"net{request.TargetMajor}.0";
        var run = new AuditRunState();
        var raw = new List<(ProjectInfo Project, RawFinding Finding)>();
        var files = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        using var loader = new CompilationLoader(request.RepositoryRoot);
        var targets = new TargetBuilds(request, loader);
        var contexts = new List<AuditMatchContext>();
        using (var phase = request.Progress.BeginPhase("audit " + Wire(request.Audit), 1, 1))
        {
            for (var i = 0; i < projects.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var project = projects[i];
                phase.Report(i, projects.Count, project.Id);
                if (loader.LoadForProject(project) is not CSharpCompilation compilation)
                {
                    skipped.Add($"{project.Id}: the compiler log has no compilation for it (run `offramp scan`).");
                    continue;
                }

                var trial = request.Audit == AuditKind.Api && project.FrameworkClass == FrameworkClass.Framework && rules.Any(r => r.Matcher == "target-compilation")
                    ? await targets.BuildAsync(project, skipped, cancellationToken).ConfigureAwait(false)
                    : null;
                var trees = AuditEngine.Sources(compilation);
                files[project.Id] = [.. trees.Select(t => AuditEngine.Position(request.RepositoryRoot, Location.Create(t, default)).File)];
                var context = new AuditMatchContext
                {
                    RepositoryRoot = request.RepositoryRoot,
                    Project = project,
                    Compilation = compilation,
                    Trees = trees,
                    Rules = rules,
                    TargetMajor = request.TargetMajor,
                    Target = trial,
                    Run = run,
                };
                contexts.Add(context);
                raw.AddRange(AuditEngine.Run(context, Matchers).Select(f => (project, f)));
            }

            phase.Report(projects.Count, projects.Count);
        }

        // Serializable-but-unused needs every project's serialized types first.
        foreach (var context in contexts)
        {
            raw.AddRange(SerializableUnused.Run(context).Select(f => (context.Project, f)));
        }

        var findings = raw
            .Select(r => ToFinding(request, r.Project, r.Finding))
            .OfType<AuditFinding>()
            .OrderBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => f.Project, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ThenBy(f => f.Symbol, StringComparer.Ordinal)
            .ToList();
        Report(request, findings);

        return new AuditResult
        {
            Audit = request.Audit,
            Target = target,
            Projects = [.. contexts.Select(c => c.Project.Id)],
            Rules = [.. rules.Select(r => r.Id)],
            Summary = Summary(request, rules, findings),
            Findings = findings,
            Ledger = request.Audit == AuditKind.Api ? Ledger(files, findings) : [],
            TopNamespaces = request.Audit == AuditKind.Api ? TopNamespaces(findings) : [],
            Skipped = [.. skipped.Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>The audit's rules minus disabled packs and rules configured to <c>none</c>.</summary>
    public static IReadOnlyList<AuditRule> ActiveRules(AuditRequest request) =>
        [.. AuditRules.For(request.Audit)
            .Where(r => request.Packs.Count > 0
                ? request.Packs.Contains(r.Pack, StringComparer.OrdinalIgnoreCase)
                : !request.DisabledPacks.Contains(r.Pack, StringComparer.OrdinalIgnoreCase))
            .Where(r => !(request.Overrides.TryGetValue(r.Id, out var o) && o.Severity is null))];

    private static (List<ProjectInfo> Projects, List<string> Skipped) Select(AuditRequest request)
    {
        var skipped = new List<string>();
        var projects = new List<ProjectInfo>();
        foreach (var project in request.Model.Projects.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (request.Projects.Count > 0 && !request.Projects.Any(p => Matches(project, p)))
            {
                continue;
            }

            if (project.Config.Excluded)
            {
                continue;
            }

            if (project.Language != "csharp")
            {
                skipped.Add($"{project.Id}: audits read C# only.");
            }
            else if (project.CompilerCalls.Count == 0)
            {
                skipped.Add($"{project.Id}: no compiler call was recorded for it (run `offramp scan`).");
            }
            else
            {
                projects.Add(project);
            }
        }

        return (projects, skipped);
    }

    private static bool Matches(ProjectInfo project, string selector) =>
        string.Equals(project.Id, selector.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
        || string.Equals(project.Name, selector, StringComparison.OrdinalIgnoreCase);

    private static AuditFinding? ToFinding(AuditRequest request, ProjectInfo project, RawFinding raw)
    {
        var severity = raw.Rule.SeverityFor(request.TargetMajor);
        var overridden = false;
        if (request.Overrides.TryGetValue(raw.Rule.Id, out var o))
        {
            if (o.Severity is null)
            {
                return null;
            }

            severity = o.Severity.Value;
            overridden = true;
        }

        var (file, line, column) = raw.FileLocation is { } at ? (at.File, at.Line, 1) : AuditEngine.Position(request.RepositoryRoot, raw.Location);
        return new AuditFinding
        {
            Rule = raw.Rule.Id,
            Severity = severity,
            Overridden = overridden,
            Project = project.Id,
            File = file,
            Line = line,
            Column = column,
            Symbol = raw.Symbol,
            Namespace = raw.Namespace,
            Category = raw.Rule.Category,
            Message = raw.Message ?? $"{Capitalize(raw.Rule.Title)}: {raw.Symbol}.",
            Recommendation = raw.Rule.Recommendation,
            Details = raw.Details is null ? new SortedDictionary<string, string>(StringComparer.Ordinal) : new SortedDictionary<string, string>(raw.Details.ToDictionary(), StringComparer.Ordinal),
        };
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>One diagnostic per rule and project: the count, located at the first finding.</summary>
    private static void Report(AuditRequest request, List<AuditFinding> findings)
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

    private static List<AuditRuleCount> Summary(AuditRequest request, IReadOnlyList<AuditRule> rules, List<AuditFinding> findings) =>
        [.. rules
            .Select(r => (Rule: r, Findings: findings.Where(f => f.Rule == r.Id).ToList()))
            .Where(r => r.Findings.Count > 0)
            .Select(r => new AuditRuleCount(
                r.Rule.Id,
                r.Rule.Title,
                r.Findings[0].Severity,
                r.Findings.Count,
                r.Findings.Select(f => f.Project).Distinct(StringComparer.Ordinal).Count()))];

    private static List<ProjectPortability> Ledger(SortedDictionary<string, IReadOnlyList<string>> files, List<AuditFinding> findings)
    {
        var ledger = new List<ProjectPortability>();
        foreach (var (project, sources) in files)
        {
            var mine = findings.Where(f => f.Project == project).ToList();
            var blocked = mine.Where(f => f.Severity == Severity.Error).Select(f => f.File).ToHashSet(StringComparer.Ordinal);
            var portable = sources.Count(s => !blocked.Contains(s));
            ledger.Add(new ProjectPortability
            {
                Project = project,
                Files = sources.Count,
                PortableFiles = portable,
                Portability = sources.Count == 0 ? 1.0 : Math.Round((double)portable / sources.Count, 3),
                ByCategory = Counts(mine.Select(f => f.Category)),
                ByNamespace = Counts(mine.Select(f => f.Namespace).OfType<string>()),
            });
        }

        return ledger;
    }

    private static SortedDictionary<string, int> Counts(IEnumerable<string> keys)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private static List<NamespaceCount> TopNamespaces(List<AuditFinding> findings) =>
        [.. findings
            .Select(f => f.Namespace)
            .OfType<string>()
            .GroupBy(n => n, StringComparer.Ordinal)
            .Select(g => new NamespaceCount(g.Key, g.Count()))
            .OrderByDescending(n => n.Findings)
            .ThenBy(n => n.Namespace, StringComparer.Ordinal)
            .Take(20)];

    public static string Wire(AuditKind audit) => audit switch
    {
        AuditKind.Api => "api",
        AuditKind.Behavior => "behavior",
        AuditKind.Serialization => "serialization",
        _ => "native",
    };

    /// <summary>Target compilations of the run, built on demand so project references can use each other's.</summary>
    private sealed class TargetBuilds(AuditRequest request, CompilationLoader loader)
    {
        private readonly Dictionary<string, TargetCompilation?> _built = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MetadataReference> _files = new(StringComparer.Ordinal);

        public async Task<TargetCompilation?> BuildAsync(ProjectInfo project, List<string> skipped, CancellationToken cancellationToken)
        {
            if (_built.TryGetValue(project.Id, out var known))
            {
                return known;
            }

            _built[project.Id] = null; // a cycle ends here
            var built = await BuildUncachedAsync(project, skipped, cancellationToken).ConfigureAwait(false);
            _built[project.Id] = built;
            return built;
        }

        private async Task<TargetCompilation?> BuildUncachedAsync(ProjectInfo project, List<string> skipped, CancellationToken cancellationToken)
        {
            if (request.References is null || loader.LoadForProject(project) is not CSharpCompilation recorded)
            {
                return null;
            }

            var desktop = project.Kind is ProjectKind.Winforms or ProjectKind.Wpf;
            var tfm = $"net{request.TargetMajor}.0" + (desktop ? "-windows" : "");
            var frameworks = new List<string>();
            if (project.Kind == ProjectKind.Web)
            {
                frameworks.Add("Microsoft.AspNetCore.App");
            }

            if (desktop)
            {
                frameworks.Add("Microsoft.WindowsDesktop.App");
            }

            var resolved = await request.References.ResolveAsync(new TargetReferenceRequest
            {
                TargetFramework = tfm,
                Frameworks = frameworks,
                Packages = DirectPackages(project),
            }, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is { } error)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3010,
                    $"{project.Id} was not compiled against {tfm}, so missing and Windows-only APIs are not reported for it: {error}",
                    new DiagnosticLocation(project.Id));
                skipped.Add($"{project.Id}: not compiled against {tfm} (OFR3010).");
                return null;
            }

            if (resolved.DroppedPackages.Count > 0)
            {
                request.Diagnostics.Report(DiagnosticCatalog.OFR3011,
                    $"{string.Join(", ", resolved.DroppedPackages)} {(resolved.DroppedPackages.Count == 1 ? "does" : "do")} not support {tfm}; the APIs used from {(resolved.DroppedPackages.Count == 1 ? "it" : "them")} are reported as missing.",
                    new DiagnosticLocation(project.Id),
                    [KeyValuePair.Create<string, JsonNode?>("packages", new JsonArray([.. resolved.DroppedPackages.Select(p => (JsonNode?)p)]))]);
            }

            var references = resolved.Paths.Select(File).ToList();
            foreach (var reference in project.ProjectReferences.Order(StringComparer.Ordinal))
            {
                if (request.Model.Projects.FirstOrDefault(p => p.Id == reference) is { } dependency && await ReferenceAsync(dependency, skipped, cancellationToken).ConfigureAwait(false) is { } dependencyReference)
                {
                    references.Add(dependencyReference);
                }
            }

            foreach (var loose in project.AssemblyReferences.Where(a => a.Kind == AssemblyReferenceKind.File && a.HintPath is not null))
            {
                var path = RepoPaths.ToAbsolute(request.RepositoryRoot, loose.HintPath!);
                if (System.IO.File.Exists(path))
                {
                    references.Add(File(path));
                }
            }

            return TargetCompilation.Create(recorded, tfm, request.TargetMajor, references);
        }

        /// <summary>A referenced project as the target sees it: its own target compilation, or its recorded modern or standard build.</summary>
        private async Task<MetadataReference?> ReferenceAsync(ProjectInfo dependency, List<string> skipped, CancellationToken cancellationToken)
        {
            if (dependency.FrameworkClass == FrameworkClass.Framework)
            {
                return (await BuildAsync(dependency, skipped, cancellationToken).ConfigureAwait(false))?.Compilation.ToMetadataReference();
            }

            var modern = dependency.CompilerCalls.Keys.Where(t => !t.StartsWith("net4", StringComparison.Ordinal)).Order(StringComparer.Ordinal).LastOrDefault();
            return modern is not null && loader.LoadForProject(dependency, modern) is { } compilation ? compilation.ToMetadataReference() : null;
        }

        private MetadataReference File(string path) =>
            _files.TryGetValue(path, out var reference) ? reference : _files[path] = MetadataReference.CreateFromFile(path);

        private static List<(string Id, string Version)> DirectPackages(ProjectInfo project)
        {
            var target = CompilationLoader.PreferredTarget(project);
            if (target is not null && project.Resolved.TryGetValue(target, out var resolved))
            {
                return [.. resolved.Packages.Where(p => p.Direct).Select(p => (p.Id, p.Version))];
            }

            return [.. project.PackageReferences.Where(p => p.Version is not null).Select(p => (p.Id, p.Version!))];
        }
    }
}
