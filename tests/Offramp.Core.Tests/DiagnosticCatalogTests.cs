using System.Text.RegularExpressions;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;

namespace Offramp.Core.Tests;

public sealed partial class DiagnosticCatalogTests
{
    [Fact]
    public void Codes_are_unique_well_formed_and_documented()
    {
        var codes = DiagnosticCatalog.All.Select(d => d.Code).ToList();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        foreach (var d in DiagnosticCatalog.All)
        {
            Assert.Matches(CodePattern(), d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Meaning), d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Cause), d.Code);
            Assert.False(string.IsNullOrWhiteSpace(d.Fix), d.Code);
            Assert.False(d.Title.EndsWith('.'), $"{d.Code} title ends with a period");
            Assert.False(char.IsUpper(d.Title[0]) && char.IsLower(d.Title[1]), $"{d.Code} title starts with a capitalized word");
            Assert.Equal($"https://offramp.dev/diagnostics/{d.Code}", d.HelpUri);
        }
    }

    [Fact]
    public void Emitted_codes_are_not_also_reserved()
    {
        var reserved = ReservedCodes();
        var clash = DiagnosticCatalog.All.Select(d => d.Code).Where(reserved.Contains).ToList();
        Assert.Empty(clash);
    }

    [Fact]
    public void Diagnostics_document_is_generated_from_the_catalog()
    {
        var path = RepositoryFiles.Path("docs", "diagnostics.md");
        var expected = DiagnosticsDocument.Render();
        if (Environment.GetEnvironmentVariable("OFFRAMP_REGENERATE") == "1")
        {
            File.WriteAllText(path, expected);
        }

        var actual = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(expected == actual,
            "docs/diagnostics.md is out of date with DiagnosticCatalog. Run eng/gen-diagnostics.sh (or eng/gen-diagnostics.ps1) and commit the result.");
    }

    /// <summary>CLAUDE.md non-negotiable 6: every code has a test that produces it.</summary>
    [Fact]
    public void Every_code_has_a_test_that_produces_it()
    {
        var produced = Directory.EnumerateFiles(RepositoryFiles.Path("tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => ProducesPattern().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var untested = DiagnosticCatalog.All.Select(d => d.Code).Where(c => !produced.Contains(c)).ToList();

        Assert.True(untested.Count == 0, "Codes without a [ProducesDiagnostic] test: " + string.Join(", ", untested));
    }

    /// <summary>Guards against ad-hoc codes: every OFR#### in src/ is catalogued or reserved.</summary>
    [Fact]
    public void Source_mentions_only_known_codes()
    {
        var known = DiagnosticCatalog.All.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);
        known.UnionWith(ReservedCodes());
        var unknown = Directory.EnumerateFiles(RepositoryFiles.Path("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "DiagnosticsDocument.cs") // range bounds, not codes
            .SelectMany(f => CodePatternInText().Matches(File.ReadAllText(f)).Select(m => $"{m.Value} in {Path.GetFileName(f)}"))
            .Where(s => !known.Contains(s[..7]))
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0, "Unknown codes in src/: " + string.Join(", ", unknown));
    }

    private static HashSet<string> ReservedCodes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ReservedDiagnostics.All)
        {
            foreach (var part in entry.Code.Split(',', StringSplitOptions.TrimEntries))
            {
                var match = RangePattern().Match(part);
                if (!match.Success)
                {
                    continue;
                }

                var start = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var end = match.Groups[2].Success
                    ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : start;
                for (var i = start; i <= end; i++)
                {
                    codes.Add($"OFR{i:D4}");
                }
            }
        }

        return codes;
    }

    [GeneratedRegex(@"^OFR\d{4}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"OFR\d{4}")]
    private static partial Regex CodePatternInText();

    [GeneratedRegex(@"ProducesDiagnostic\(""(OFR\d{4})""\)")]
    private static partial Regex ProducesPattern();

    [GeneratedRegex(@"^OFR(\d{4})(?:–(\d{4}))?$")]
    private static partial Regex RangePattern();
}
