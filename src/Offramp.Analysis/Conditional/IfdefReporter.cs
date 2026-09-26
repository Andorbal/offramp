using Offramp.Core.Model;
using Offramp.Core.Paths;

namespace Offramp.Analysis.Conditional;

/// <summary>Conditional regions naming one symbol.</summary>
/// <param name="Symbol">The preprocessor symbol.</param>
/// <param name="Regions"><c>#if</c> chains whose conditions name it.</param>
/// <param name="Lines">Lines inside those chains, directives excluded.</param>
/// <param name="Files">Files with at least one such chain.</param>
public sealed record IfdefSymbolCount(string Symbol, int Regions, int Lines, int Files);

public sealed record IfdefProjectReport(string Project, IReadOnlyList<IfdefSymbolCount> Symbols);

/// <summary>The <c>result</c> of <c>offramp ifdef report</c> (<c>schemas/v1/ifdef-report.json</c>).</summary>
public sealed record IfdefReportResult
{
    /// <summary>The <c>--symbol</c> filter, or null for every symbol.</summary>
    public string? Symbol { get; init; }

    /// <summary>Projects with conditional regions, by id.</summary>
    public required IReadOnlyList<IfdefProjectReport> Projects { get; init; }

    /// <summary>Per symbol across the solution; a file shared by projects counts once.</summary>
    public required IReadOnlyList<IfdefSymbolCount> Totals { get; init; }
}

/// <summary><c>ifdef report</c>: how much code sits behind each preprocessor symbol, per project.</summary>
public static class IfdefReporter
{
    public static IfdefReportResult Report(string repositoryRoot, WorkspaceModel model, string? symbol)
    {
        var chainsByFile = new Dictionary<string, IReadOnlyList<DirectiveChain>>(StringComparer.Ordinal);
        IReadOnlyList<DirectiveChain> ChainsOf(string file)
        {
            if (!chainsByFile.TryGetValue(file, out var chains))
            {
                var path = RepoPaths.ToAbsolute(repositoryRoot, file);
                chains = File.Exists(path) ? ConditionalDirectives.Chains(File.ReadAllText(path)) : [];
                chainsByFile[file] = chains;
            }

            return chains;
        }

        var projects = new List<IfdefProjectReport>();
        foreach (var project in model.Projects.Where(p => p.Language == "csharp").OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var files = project.Compile.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var counts = Count(files.Select(f => (f, ChainsOf(f))), symbol);
            if (counts.Count > 0)
            {
                projects.Add(new IfdefProjectReport(project.Id, counts));
            }
        }

        var all = chainsByFile.Keys.Order(StringComparer.Ordinal).Select(f => (f, chainsByFile[f]));
        return new IfdefReportResult { Symbol = symbol, Projects = projects, Totals = Count(all, symbol) };
    }

    private static List<IfdefSymbolCount> Count(IEnumerable<(string File, IReadOnlyList<DirectiveChain> Chains)> files, string? only)
    {
        var regions = new SortedDictionary<string, (int Regions, int Lines, HashSet<string> Files)>(StringComparer.Ordinal);
        foreach (var (file, chains) in files)
        {
            foreach (var chain in chains)
            {
                foreach (var symbol in chain.Symbols.Where(s => only is null || s == only))
                {
                    var (count, lines, inFiles) = regions.TryGetValue(symbol, out var existing) ? existing : (0, 0, new HashSet<string>(StringComparer.Ordinal));
                    inFiles.Add(file);
                    regions[symbol] = (count + 1, lines + chain.GuardedLines, inFiles);
                }
            }
        }

        return [.. regions.Select(r => new IfdefSymbolCount(r.Key, r.Value.Regions, r.Value.Lines, r.Value.Files.Count))];
    }
}
