using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Audits.Matchers;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
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
    /// <summary>The named matchers rule packs refer to (<c>matcher:</c>).</summary>
    public static IReadOnlyDictionary<string, IAuditMatcher> NamedMatchers => Matchers;

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
        var targets = new TargetCompilationBuilder(request.RepositoryRoot, request.Model, request.TargetMajor, request.References, request.Diagnostics, loader);
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
                    var reason = NoCompilation(project);
                    skipped.Add($"{project.Id}: {reason}");
                    request.Diagnostics.Report(DiagnosticCatalog.OFR3012, $"Not audited: {reason}", new DiagnosticLocation(project.Id));
                    continue;
                }

                if (project.Partial)
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR3016,
                        $"{project.Id} is partial: its build failed during `offramp scan`, so the compilation audited may lack sources or references and findings can be missing. Fix the build errors `scan` reported (OFR0130) and scan again.",
                        new DiagnosticLocation(project.Id));
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
        SerializableUnusedMatcher.ExtendToImplementations(contexts);
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

            var reason = project.Language != "csharp" ? "audits read C# only."
                : project.CompilerCalls.Count == 0 ? NoCompilation(project)
                : null;
            if (reason is null)
            {
                projects.Add(project);
                continue;
            }

            skipped.Add($"{project.Id}: {reason}");
            request.Diagnostics.Report(DiagnosticCatalog.OFR3012, $"Not audited: {reason}", new DiagnosticLocation(project.Id));
        }

        return (projects, skipped);
    }

    /// <summary>
    /// Why a C# project's compilation cannot be read (<c>OFR3012</c>). A project in the model has
    /// been scanned: without a compiler call, its build failed (the model marks it partial) or
    /// the compiler log could not be made from the build.
    /// </summary>
    public static string NoCompilation(ProjectInfo project) =>
        project.CompilerCalls.Count > 0 ? "the compiler log has no compilation for it (run `offramp scan` again)."
        : project.Partial ? "`offramp scan` recorded no compiler call for it: its build failed (OFR0130) or its compiler call could not be read (OFR0132). Fix what `scan` reported and scan again."
        : "no compiler call was recorded for it (run `offramp scan`).";

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
}
