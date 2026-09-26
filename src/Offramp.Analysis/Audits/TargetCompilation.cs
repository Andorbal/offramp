using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Offramp.Analysis.Audits;

/// <summary>
/// A project's recorded sources compiled against the target (<c>audit api</c>): same text, same
/// options, the target's preprocessor symbols instead of the .NET Framework ones, and the
/// target's reference assemblies. Trees keep their paths, so a position in one maps to the same
/// position in the recorded compilation.
/// </summary>
public sealed class TargetCompilation
{
    private readonly Dictionary<string, SyntaxTree> _byPath;

    private TargetCompilation(CSharpCompilation compilation, string targetFramework, int major)
    {
        Compilation = compilation;
        TargetFramework = targetFramework;
        Major = major;
        _byPath = compilation.SyntaxTrees.GroupBy(t => t.FilePath, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public CSharpCompilation Compilation { get; }

    /// <summary><c>net10.0</c>, or <c>net10.0-windows</c> for desktop projects.</summary>
    public string TargetFramework { get; }

    public bool Windows => TargetFramework.EndsWith("-windows", StringComparison.Ordinal);

    /// <summary>The target's major version (10 for <c>net10.0</c>).</summary>
    public int Major { get; }

    /// <summary>The target tree for a recorded tree, or null.</summary>
    public SyntaxTree? TreeFor(SyntaxTree recorded) => _byPath.GetValueOrDefault(recorded.FilePath);

    public static TargetCompilation Create(CSharpCompilation recorded, string targetFramework, int major, IEnumerable<MetadataReference> references)
    {
        var windows = targetFramework.EndsWith("-windows", StringComparison.Ordinal);
        var trees = recorded.SyntaxTrees
            .Select(t => CSharpSyntaxTree.ParseText(t.GetText(), Options((CSharpParseOptions)t.Options, major, windows), t.FilePath))
            .ToList();
        var compilation = CSharpCompilation.Create(recorded.AssemblyName, trees, references, recorded.Options);
        return new TargetCompilation(compilation, targetFramework, major);
    }

    /// <summary>
    /// The same target compilation with the sources of <paramref name="source"/> (a recorded
    /// compilation with newer text laid over it): trees whose text changed are parsed again, new
    /// ones are added, and those it no longer has are removed. Unchanged trees are kept as they are.
    /// </summary>
    public TargetCompilation WithSources(CSharpCompilation source)
    {
        var options = (Func<CSharpParseOptions, CSharpParseOptions>)(o => Options(o, Major, Windows));
        var wanted = source.SyntaxTrees.GroupBy(t => t.FilePath, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var compilation = Compilation;
        foreach (var (path, tree) in _byPath)
        {
            if (!wanted.ContainsKey(path))
            {
                compilation = compilation.RemoveSyntaxTrees(tree);
            }
        }

        foreach (var (path, tree) in wanted)
        {
            if (_byPath.TryGetValue(path, out var existing))
            {
                if (!ReferenceEquals(existing.GetText(), tree.GetText()) && !existing.GetText().ContentEquals(tree.GetText()))
                {
                    compilation = compilation.ReplaceSyntaxTree(existing, CSharpSyntaxTree.ParseText(tree.GetText(), options((CSharpParseOptions)tree.Options), path));
                }
            }
            else
            {
                compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(tree.GetText(), options((CSharpParseOptions)tree.Options), path));
            }
        }

        return ReferenceEquals(compilation, Compilation) ? this : new TargetCompilation(compilation, TargetFramework, Major);
    }

    /// <summary>The recorded parse options with the target's framework symbols instead of .NET Framework's.</summary>
    public static CSharpParseOptions Options(CSharpParseOptions recorded, int major, bool windows)
    {
        var symbols = recorded.PreprocessorSymbolNames.Where(s => !IsFrameworkSymbol(s) && !IsModernSymbol(s)).ToList();
        symbols.AddRange(TargetSymbols(major, windows));
        return recorded.WithPreprocessorSymbols(symbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>The symbols the SDK defines for <c>netN.0</c> (and <c>-windows</c>).</summary>
    public static IEnumerable<string> TargetSymbols(int major, bool windows)
    {
        yield return "NET";
        yield return "NETCOREAPP";
        foreach (var core in new[] { "1_0", "1_1", "2_0", "2_1", "2_2", "3_0", "3_1" })
        {
            yield return $"NETCOREAPP{core}_OR_GREATER";
        }

        for (var version = 5; version <= major; version++)
        {
            yield return $"NET{version}_0_OR_GREATER";
        }

        yield return $"NET{major}_0";
        if (windows)
        {
            yield return "WINDOWS";
            yield return "WINDOWS7_0_OR_GREATER";
        }
    }

    /// <summary><c>NETFRAMEWORK</c>, <c>NET48</c>, <c>NET472_OR_GREATER</c>, and the like.</summary>
    private static bool IsFrameworkSymbol(string symbol)
    {
        if (symbol == "NETFRAMEWORK")
        {
            return true;
        }

        var version = symbol.StartsWith("NET", StringComparison.Ordinal) ? symbol[3..] : null;
        if (version is null)
        {
            return false;
        }

        if (version.EndsWith("_OR_GREATER", StringComparison.Ordinal))
        {
            version = version[..^"_OR_GREATER".Length];
        }

        return version.Length is >= 2 and <= 3 && version.All(char.IsAsciiDigit);
    }

    /// <summary>Symbols of another modern target (a dual-target project's recorded modern call).</summary>
    private static bool IsModernSymbol(string symbol) =>
        symbol is "NET" or "NETCOREAPP" or "WINDOWS"
        || (symbol.StartsWith("NETCOREAPP", StringComparison.Ordinal) && symbol.EndsWith("_OR_GREATER", StringComparison.Ordinal))
        || (symbol.StartsWith("WINDOWS", StringComparison.Ordinal) && symbol.EndsWith("_OR_GREATER", StringComparison.Ordinal))
        || (symbol.StartsWith("NET", StringComparison.Ordinal) && symbol.Length > 3 && char.IsAsciiDigit(symbol[3]) && symbol.Contains('_', StringComparison.Ordinal));
}
