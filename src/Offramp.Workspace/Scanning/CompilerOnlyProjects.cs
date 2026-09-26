using Offramp.Core.Configuration;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Ingest;
using Offramp.Workspace.Model;

namespace Offramp.Workspace.Scanning;

/// <summary>
/// Builds projects from compiler calls alone (<c>scan --complog</c>): what the
/// compiler saw (targets, sources, defines, options, project-to-project
/// references). Evaluation-only facts stay at their defaults (OFR0103).
/// </summary>
public static class CompilerOnlyProjects
{
    public static List<ProjectInfo> Build(
        IReadOnlyList<CompiledProject> compiled, CapturePathMapper mapper, OfframpConfig config, string complogRelative)
    {
        var byIndex = compiled.ToDictionary(c => c.Call.Index);
        var excluded = new PathGlobs(config.Paths.Exclude);
        var projects = new List<ProjectInfo>();
        foreach (var group in compiled.GroupBy(c => c.Call.ProjectFile, StringComparer.Ordinal))
        {
            var id = mapper.ToRelative(group.Key);
            if (id is null)
            {
                continue;
            }

            var calls = group.Where(c => c.Call.TargetFramework is not null).OrderBy(c => c.Call.TargetFramework, StringComparer.Ordinal).ToList();
            var first = calls.FirstOrDefault() ?? group.First();
            var tfms = Tfm.Sort(calls.Select(c => c.Call.TargetFramework!));
            var compile = group.SelectMany(c => c.Sources)
                .Select(mapper.ToRelative)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var references = group.SelectMany(c => c.ReferencedCalls)
                .Where(byIndex.ContainsKey)
                .Select(i => mapper.ToRelative(byIndex[i].Call.ProjectFile))
                .OfType<string>()
                .Where(r => r != id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var fileReferences = group.SelectMany(c => c.ReferencedFiles)
                .Select(f => (Path: f, Relative: mapper.ToRelative(f)))
                .Where(f => f.Relative is not null && !f.Relative.Contains("/obj/", StringComparison.Ordinal))
                .GroupBy(f => Path.GetFileNameWithoutExtension(f.Path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
                .Select(g => new AssemblyReferenceInfo
                {
                    Name = g.Key,
                    HintPath = g.First().Relative,
                    Kind = AssemblyReferenceKind.File,
                    Metadata = AssemblyFileInspector.Inspect(RepoPaths.ToAbsolute(mapper.RepositoryRoot, g.First().Relative!)),
                })
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var (kind, evidence) = ProjectKindDetector.Detect(new ProjectFacts { OutputType = first.OutputType });
            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (first.LangVersion is not null) properties["LangVersion"] = first.LangVersion;
            if (first.Nullable is not null) properties["Nullable"] = first.Nullable;

            var callRefs = new SortedDictionary<string, CompilerCallRef>(StringComparer.Ordinal);
            var defines = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                callRefs[call.Call.TargetFramework!] = new CompilerCallRef(complogRelative, call.Call.Index);
                defines[call.Call.TargetFramework!] = call.Defines;
            }

            projects.Add(new ProjectInfo
            {
                Id = id,
                Name = Path.GetFileNameWithoutExtension(id),
                AssemblyName = first.AssemblyName,
                RootNamespace = first.BuildProperties.GetValueOrDefault("RootNamespace"),
                Language = ProjectModelBuilder.Language(id),
                Kind = kind,
                KindEvidence = evidence,
                TargetFrameworks = tfms,
                FrameworkClass = Tfm.Classify(tfms),
                OutputType = first.OutputType,
                Properties = properties,
                DefineConstants = defines,
                Compile = compile,
                ProjectReferences = references,
                AssemblyReferences = fileReferences,
                CompilerCalls = callRefs,
                Loc = ProjectModelBuilder.CountLines(compile, mapper.RepositoryRoot),
                Config = new ProjectConfigState { Excluded = excluded.Matches(id) },
            });
        }

        projects.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return projects;
    }
}
