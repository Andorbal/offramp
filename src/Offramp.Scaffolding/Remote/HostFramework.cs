using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Remote;

/// <summary>What the net10.0-windows trial compilation found.</summary>
internal sealed record HostTrial(bool Compiles, string Reason, IReadOnlyList<SyntaxTree> Sources, IReadOnlyList<(string Id, string Version)> Packages);

/// <summary>
/// The host framework (docs/spec/commands/seams.md#remote): net10.0-windows when the
/// implementation and everything it uses in its project compile for it with
/// Microsoft.Windows.Compatibility, checked by compiling those files in memory against the real
/// reference assemblies; otherwise the net48 fallback.
/// </summary>
internal static class HostFramework
{
    public const string Modern = "net10.0-windows";

    public const string Legacy = "net48";

    /// <summary>The source files the implementation needs from its project: its own, and those of every project type it uses, transitively.</summary>
    public static List<SyntaxTree> Closure(Compilation compilation, params INamedTypeSymbol[] roots)
    {
        var sources = AuditEngine.Sources(compilation).ToHashSet();
        var trees = new HashSet<SyntaxTree>();
        var queue = new Queue<SyntaxTree>();
        void Add(ITypeSymbol? type)
        {
            for (var current = type as INamedTypeSymbol; current is not null; current = current.ContainingType)
            {
                foreach (var reference in current.OriginalDefinition.DeclaringSyntaxReferences)
                {
                    if (sources.Contains(reference.SyntaxTree) && trees.Add(reference.SyntaxTree))
                    {
                        queue.Enqueue(reference.SyntaxTree);
                    }
                }

                foreach (var argument in current.TypeArguments)
                {
                    Add(argument);
                }
            }
        }

        foreach (var root in roots)
        {
            Add(root);
        }

        // Global usings apply to every file.
        foreach (var tree in sources.Where(t => t.GetCompilationUnitRoot().Usings.Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))))
        {
            if (trees.Add(tree))
            {
                queue.Enqueue(tree);
            }
        }

        while (queue.Count > 0)
        {
            var tree = queue.Dequeue();
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var symbol = model.GetSymbolInfo(name).Symbol;
                Add(symbol as ITypeSymbol ?? symbol?.ContainingType);
                Add(model.GetTypeInfo(name).Type);
            }
        }

        return [.. trees.OrderBy(t => t.FilePath, StringComparer.Ordinal)];
    }

    /// <summary>Compiles the closure for net10.0-windows with Microsoft.Windows.Compatibility and the project's own packages.</summary>
    public static async Task<HostTrial> TryModernAsync(
        ProjectInfo project, CSharpCompilation recorded, IReadOnlyList<SyntaxTree> sources, TargetReferenceResolver? resolver, CancellationToken cancellationToken)
    {
        if (resolver is null)
        {
            return new HostTrial(false, "no reference resolver", sources, []);
        }

        var packages = DirectPackages(project);
        packages.Add(("Microsoft.Windows.Compatibility", ScaffoldPackages.Version("Microsoft.Windows.Compatibility")));
        var references = await resolver.ResolveAsync(new TargetReferenceRequest { TargetFramework = Modern, Packages = packages }, cancellationToken).ConfigureAwait(false);
        if (references.Error is { } error)
        {
            return new HostTrial(false, $"the {Modern} references could not be resolved: {error}", sources, []);
        }

        var kept = packages.Where(p => !references.DroppedPackages.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var trees = sources
            .Select(t => CSharpSyntaxTree.ParseText(t.GetText(), TargetCompilation.Options((CSharpParseOptions)t.Options, 10, windows: true).WithLanguageVersion(LanguageVersion.Default), t.FilePath))
            .ToList();
        var options = recorded.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary).WithMainTypeName(null).WithNullableContextOptions(NullableContextOptions.Disable);
        var compilation = CSharpCompilation.Create("host-trial", trees, references.Paths.Select(p => MetadataReference.CreateFromFile(p)), options);
        var errors = compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start).ToList();
        if (errors.Count > 0)
        {
            var first = string.Join("; ", errors.Take(3).Select(d => $"{d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}"));
            return new HostTrial(false, $"the implementation does not compile for {Modern} ({errors.Count} error{(errors.Count == 1 ? "" : "s")}: {first})", sources, kept);
        }

        return new HostTrial(true, $"the implementation and the {sources.Count} file{(sources.Count == 1 ? "" : "s")} it uses compile for {Modern} with Microsoft.Windows.Compatibility", sources, kept);
    }

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
