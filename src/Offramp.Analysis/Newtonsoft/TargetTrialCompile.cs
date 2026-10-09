using System.IO.Compression;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Frameworks;
using Offramp.Analysis.Audits;

namespace Offramp.Analysis.Newtonsoft;

/// <summary>
/// <c>OFR3701</c>/<c>OFR3702</c>: the project's compilation with the target release's
/// Newtonsoft.Json.dll in place of its own; what the compiler says that it did not say before
/// (ADR 0066).
/// </summary>
internal static class TargetTrialCompile
{
    private static readonly HashSet<string> Obsolete = new(StringComparer.Ordinal) { "CS0618", "CS0612" };

    /// <summary>The assembly NuGet would pick from the target package for a framework, or null.</summary>
    public static MetadataReference? Assembly(byte[] package, string targetFramework)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var candidates = archive.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && e.Name.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)
                && e.FullName.Count(c => c == '/') == 2)
            .ToDictionary(e => NuGetFramework.ParseFolder(e.FullName.Split('/')[1]), e => e);
        if (NuGetFrameworkUtility.GetNearest(candidates.Keys, NuGetFramework.Parse(targetFramework), f => f) is not { } nearest)
        {
            return null;
        }

        using var stream = candidates[nearest].Open();
        using var image = new MemoryStream();
        stream.CopyTo(image);
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    public static IEnumerable<RawNewtonsoftFinding> Run(string repositoryRoot, Compilation original, MetadataReference target, IReadOnlyList<SyntaxTree> trees)
    {
        var current = original.References.FirstOrDefault(r => original.GetAssemblyOrModuleSymbol(r) is IAssemblySymbol { Name: NewtonsoftReferences.PackageId });
        if (current is null)
        {
            yield break;
        }

        var trial = original.ReplaceReference(current, target);
        var sources = trees.ToHashSet();
        var before = original.GetDiagnostics().Where(d => d.Location.SourceTree is { } t && sources.Contains(t)).Select(Key).ToHashSet(StringComparer.Ordinal);
        var added = trial.GetDiagnostics()
            .Where(d => d.Location.SourceTree is { } t && sources.Contains(t) && !before.Contains(Key(d))
                && (d.Severity == DiagnosticSeverity.Error || Obsolete.Contains(d.Id)))
            .ToList();

        foreach (var group in added
            .Select(d => (Diagnostic: d, Member: Member(original, d)))
            .GroupBy(d => (Obsolete: d.Diagnostic.Severity != DiagnosticSeverity.Error, d.Member))
            .OrderBy(g => g.Key.Obsolete).ThenBy(g => g.Key.Member, StringComparer.Ordinal))
        {
            var places = group.OrderBy(d => AuditEngine.Position(repositoryRoot, d.Diagnostic.Location)).ToList();
            var first = places[0].Diagnostic;
            var advice = NewtonsoftRules.Replacement(group.Key.Member);
            var name = group.Key.Member.Length > 2 && group.Key.Member[1] == ':' ? group.Key.Member[2..] : group.Key.Member;
            var message = (group.Key.Obsolete ? $"{name} is obsolete in the target" : $"{name} does not compile against the target")
                + $" ({first.Id}: {first.GetMessage(System.Globalization.CultureInfo.InvariantCulture)})"
                + (advice is null ? "." : $"; use {advice}.");
            yield return new RawNewtonsoftFinding(group.Key.Obsolete ? ObsoleteRule : RemovedRule, null, first.Location, group.Key.Member, message,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["compiler"] = first.Id,
                    ["places"] = places.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }

    private static readonly NewtonsoftRule RemovedRule = Rule("OFR3701");

    private static readonly NewtonsoftRule ObsoleteRule = Rule("OFR3702");

    private static NewtonsoftRule Rule(string id) => new()
    {
        Id = id,
        Severity = id == "OFR3701" ? Core.Diagnostics.Severity.Error : Core.Diagnostics.Severity.Warning,
        Title = id == "OFR3701" ? "API removed or changed in the target Newtonsoft.Json" : "API obsolete in the target Newtonsoft.Json",
        Detector = "trial-compilation",
        Recommendation = id == "OFR3701"
            ? "Replace the member before upgrading; the build fails on the target until then."
            : "Move to the replacement; with warnings as errors the upgrade does not build until then.",
        Cases = [],
    };

    /// <summary>The Newtonsoft.Json member the diagnostic is about: the one bound at its place in the original compilation, or the member an override there overrides.</summary>
    private static string Member(Compilation original, Diagnostic diagnostic)
    {
        var tree = diagnostic.Location.SourceTree!;
        var model = original.GetSemanticModel(tree);
        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var symbol = model.GetSymbolInfo(node).Symbol
            ?? (model.GetDeclaredSymbol(node) switch
            {
                IMethodSymbol { OverriddenMethod: { } overridden } => overridden,
                IPropertySymbol { OverriddenProperty: { } overridden } => overridden,
                var declared => declared,
            });
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        return symbol?.OriginalDefinition.GetDocumentationCommentId() ?? diagnostic.Id;
    }

    private static string Key(Diagnostic diagnostic) =>
        diagnostic.Id + " " + diagnostic.Location.SourceTree?.FilePath + ":" + diagnostic.Location.SourceSpan.Start;
}
