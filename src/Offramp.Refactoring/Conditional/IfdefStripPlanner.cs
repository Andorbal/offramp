using Offramp.Analysis.Conditional;
using Offramp.Core.Diagnostics;
using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;

namespace Offramp.Refactoring.Conditional;

public sealed record IfdefStripRequest
{
    public required string RepositoryRoot { get; init; }

    public required WorkspaceModel Model { get; init; }

    /// <summary>The symbol whose regions go (<c>NETFRAMEWORK</c>).</summary>
    public required string Symbol { get; init; }

    /// <summary>Whether the symbol counts as defined: false keeps the branches for targets without it.</summary>
    public required bool Keep { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>A chain resolved: which branch stayed (without its directives) and how many lines went.</summary>
public sealed record IfdefStripped(string File, int Line, string Condition, string Kept, int RemovedLines);

/// <summary>A chain left alone because its outcome depends on other symbols (OFR3603).</summary>
public sealed record IfdefNotStripped(string File, int Line, string Condition, string Reason);

/// <summary>The <c>result</c> of <c>offramp ifdef strip</c> (<c>schemas/v1/ifdef-strip.json</c>).</summary>
public sealed record IfdefStripResult
{
    public required string Symbol { get; init; }

    public required bool Keep { get; init; }

    public required IReadOnlyList<IfdefStripped> Stripped { get; init; }

    public required IReadOnlyList<IfdefNotStripped> NotStripped { get; init; }

    /// <summary>Files that change, repository-relative.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    public bool Applied { get; init; }

    public string? Journal { get; init; }

    public string? Preview { get; init; }
}

public sealed record IfdefStripPlan(IfdefStripResult Result, ChangeSet? ChangeSet);

/// <summary>
/// <c>ifdef strip</c>: removes the conditional regions that name a symbol, keeping the branch
/// selected when the symbol is (or is not) defined. A chain is resolved only when that one
/// symbol decides it; one that also depends on other symbols stays (OFR3603). Only whole lines
/// are removed: directive lines and the branches not taken.
/// </summary>
public static class IfdefStripPlanner
{
    public static IfdefStripPlan Plan(IfdefStripRequest request)
    {
        var stripped = new List<IfdefStripped>();
        var notStripped = new List<IfdefNotStripped>();
        var changeSet = new ChangeSet();
        foreach (var file in SourceFiles(request.Model))
        {
            var path = RepoPaths.ToAbsolute(request.RepositoryRoot, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var source = SourceLines.Read(path);
            var removed = new SortedSet<int>();
            foreach (var chain in ConditionalDirectives.Chains(source.Text).Where(c => c.Symbols.Contains(request.Symbol, StringComparer.Ordinal)))
            {
                if (removed.Contains(chain.IfLine))
                {
                    continue; // inside a branch that goes
                }

                var (decided, kept) = Decide(chain, request.Symbol, request.Keep);
                if (!decided)
                {
                    var reason = $"#if {chain.ConditionText} also depends on other symbols.";
                    notStripped.Add(new IfdefNotStripped(file, chain.IfLine + 1, chain.ConditionText, reason));
                    request.Diagnostics.Report(DiagnosticCatalog.OFR3603, $"The region at line {chain.IfLine + 1} stays: {reason}", new DiagnosticLocation(null, file, chain.IfLine + 1));
                    continue;
                }

                var before = removed.Count;
                foreach (var branch in chain.Branches)
                {
                    removed.Add(branch.DirectiveLine);
                    if (branch != kept)
                    {
                        for (var line = branch.FirstLine; line <= branch.LastLine; line++)
                        {
                            removed.Add(line);
                        }
                    }
                }

                removed.Add(chain.EndifLine);
                var keptKind = kept is null ? "none" : kept.Condition is null ? "else" : kept == chain.Branches[0] ? "if" : "elif";
                stripped.Add(new IfdefStripped(file, chain.IfLine + 1, chain.ConditionText, keptKind, removed.Count - before));
            }

            if (removed.Count > 0)
            {
                changeSet.Edit(file, source.Bytes, source.Encode(source.Lines.Where((_, i) => !removed.Contains(i))));
            }
        }

        var result = new IfdefStripResult
        {
            Symbol = request.Symbol,
            Keep = request.Keep,
            Stripped = stripped,
            NotStripped = notStripped,
            Files = [.. changeSet.Edits.Select(e => e.Path).Order(StringComparer.Ordinal)],
            Preview = changeSet.IsEmpty ? null : changeSet.Preview(),
        };
        return new IfdefStripPlan(result, changeSet.IsEmpty ? null : changeSet);
    }

    /// <summary>The branch the preprocessor takes when only the symbol is known; undecided when an earlier branch depends on others.</summary>
    internal static (bool Decided, DirectiveBranch? Kept) Decide(DirectiveChain chain, string symbol, bool defined)
    {
        foreach (var branch in chain.Branches)
        {
            var value = branch.Condition is null ? true : ConditionalDirectives.Evaluate(branch.Condition, symbol, defined);
            switch (value)
            {
                case true:
                    return (true, branch);
                case null:
                    return (false, null);
            }
        }

        return (true, null);
    }

    /// <summary>Every C# file of every project, once, in ordinal order.</summary>
    internal static IEnumerable<string> SourceFiles(WorkspaceModel model) =>
        model.Projects
            .Where(p => p.Language == "csharp")
            .SelectMany(p => p.Compile)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
}
