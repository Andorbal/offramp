using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Analysis.Audits;

/// <summary>
/// Builds each .NET Framework project's sources against the target (<c>audit api</c>), on demand
/// and once per project, so project references can use each other's target compilations. The
/// sources come from an <see cref="ICompilationSource"/>: the compiler log as recorded, or the
/// editor's text laid over it.
/// </summary>
public sealed class TargetCompilationBuilder(
    string repositoryRoot, WorkspaceModel model, int targetMajor, TargetReferenceResolver? references, DiagnosticBag diagnostics, ICompilationSource compilations)
{
    private readonly Dictionary<string, TargetCompilation?> _built = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataReference> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetadataReference?> _recorded = new(StringComparer.Ordinal);

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
        if (references is null || compilations.LoadPreferred(project) is not CSharpCompilation recorded)
        {
            return null;
        }

        var desktop = project.Kind is ProjectKind.Winforms or ProjectKind.Wpf;
        var tfm = $"net{targetMajor}.0" + (desktop ? "-windows" : "");
        var frameworks = new List<string>();
        if (project.Kind == ProjectKind.Web)
        {
            frameworks.Add("Microsoft.AspNetCore.App");
        }

        if (desktop)
        {
            frameworks.Add("Microsoft.WindowsDesktop.App");
        }

        var resolved = await references.ResolveAsync(new TargetReferenceRequest
        {
            TargetFramework = tfm,
            Frameworks = frameworks,
            Packages = DirectPackages(project),
        }, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is { } error)
        {
            diagnostics.Report(DiagnosticCatalog.OFR3010,
                $"{project.Id} was not compiled against {tfm}, so missing and Windows-only APIs are not reported for it: {error}",
                new DiagnosticLocation(project.Id));
            skipped.Add($"{project.Id}: not compiled against {tfm} (OFR3010).");
            return null;
        }

        if (resolved.DroppedPackages.Count > 0)
        {
            diagnostics.Report(DiagnosticCatalog.OFR3011,
                $"{string.Join(", ", resolved.DroppedPackages)} {(resolved.DroppedPackages.Count == 1 ? "does" : "do")} not support {tfm}; the APIs used from {(resolved.DroppedPackages.Count == 1 ? "it" : "them")} are reported as missing.",
                new DiagnosticLocation(project.Id),
                [KeyValuePair.Create<string, JsonNode?>("packages", new JsonArray([.. resolved.DroppedPackages.Select(p => (JsonNode?)p)]))]);
        }

        var metadata = resolved.Paths.Select(File).ToList();
        foreach (var reference in project.ProjectReferences.Order(StringComparer.Ordinal))
        {
            if (model.Projects.FirstOrDefault(p => p.Id == reference) is { } dependency && await ReferenceAsync(dependency, skipped, cancellationToken).ConfigureAwait(false) is { } dependencyReference)
            {
                metadata.Add(dependencyReference);
            }
        }

        foreach (var loose in project.AssemblyReferences.Where(a => a.Kind == AssemblyReferenceKind.File && a.HintPath is not null))
        {
            var path = RepoPaths.ToAbsolute(repositoryRoot, loose.HintPath!);
            if (System.IO.File.Exists(path))
            {
                metadata.Add(File(path));
            }
        }

        return TargetCompilation.Create(recorded, tfm, targetMajor, metadata);
    }

    /// <summary>
    /// A referenced project as the target sees it: its own target compilation, or its recorded
    /// modern or standard build. A .NET Framework project that cannot be compiled against the
    /// target here (a Visual Basic project) is referenced as recorded, the way a DLL is, so
    /// its types are not reported as missing from every project that uses them.
    /// </summary>
    private async Task<MetadataReference?> ReferenceAsync(ProjectInfo dependency, List<string> skipped, CancellationToken cancellationToken)
    {
        if (dependency.FrameworkClass == FrameworkClass.Framework)
        {
            return (await BuildAsync(dependency, skipped, cancellationToken).ConfigureAwait(false))?.Compilation.ToMetadataReference()
                ?? Recorded(dependency);
        }

        var modern = dependency.CompilerCalls.Keys.Where(t => !t.StartsWith("net4", StringComparison.Ordinal)).Order(StringComparer.Ordinal).LastOrDefault();
        return modern is not null && compilations.LoadForProject(dependency, modern) is { } compilation ? compilation.ToMetadataReference() : null;
    }

    /// <summary>The recorded compilation of a project that is not C#, emitted once as an in-memory assembly (Roslyn cannot reference another language's compilation).</summary>
    private MetadataReference? Recorded(ProjectInfo project)
    {
        if (_recorded.TryGetValue(project.Id, out var known))
        {
            return known;
        }

        MetadataReference? reference = null;
        if (compilations.LoadPreferred(project) is { } recorded and not CSharpCompilation)
        {
            using var image = new MemoryStream();
            if (recorded.Emit(image).Success)
            {
                reference = MetadataReference.CreateFromImage(image.ToArray());
            }
        }

        return _recorded[project.Id] = reference;
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
