using System.Globalization;
using System.Text;
using NuGet.Versioning;
using Offramp.Analysis.Newtonsoft;

namespace Offramp.Analysis.Tests.Newtonsoft;

/// <summary>
/// ADRs 0066 and 0068: every case of <c>rules/newtonsoft-breaking.yml</c> is a measured fact. Its
/// example runs under every release from 7.0.1 on, and the outputs must be equal exactly where
/// the case's segment labels are equal.
/// </summary>
public sealed class NewtonsoftDifferentialTests
{
    public static TheoryData<string, string> Cases =>
        [.. NewtonsoftRules.All.SelectMany(r => r.Cases.Select(c => (r.Id, c.Name)))];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Outputs_group_exactly_by_the_segment_labels(string rule, string name)
    {
        var @case = NewtonsoftRules.All.Single(r => r.Id == rule).Cases.Single(c => c.Name == name);
        var outputs = Run(@case);

        var violations = Violations(@case, outputs);

        Assert.True(violations.Count == 0, $"{rule}/{name}: {string.Join("; ", violations)}\n{Table(@case, outputs)}");
    }

    [Fact]
    public void A_boundary_one_release_off_is_caught()
    {
        // MaxDepth became 64 in 13.0.1; a rule that said 13.0.2 must fail the check.
        var @case = NewtonsoftRules.All.Single(r => r.Id == "OFR3709").Cases.Single();
        var shifted = @case with { Segments = [@case.Segments[0], @case.Segments[1] with { From = NuGetVersion.Parse("13.0.2") }] };

        Assert.NotEmpty(Violations(shifted, Run(@case)));
    }

    [Fact]
    public void The_camel_case_ports_match_every_release()
    {
        // OFR3703 judges names without running Newtonsoft.Json: its ports must say what each release says.
        string[] names = ["URL VALUE", "A B", "ABC DEF GHI", "URLValue", "ID", "IOStream", "XMLHttp Request", "Hello World", "ID2", "I", "LEGACY CODE", "A", "AB CD", "lower", "X Y Z", "SKUCode"];
        var @case = NewtonsoftRules.All.Single(r => r.Id == "OFR3703").Cases.Single();
        var example = """
            using System.Collections.Generic;
            using Newtonsoft.Json;
            using Newtonsoft.Json.Serialization;
            public static class Example
            {
                public static string Run()
                {
                    var keys = new Dictionary<string, int>();
                    foreach (var name in new[] { NAMES }) { keys[name] = 0; }
                    return string.Join("|", JsonConvert.DeserializeObject<Dictionary<string, int>>(
                        JsonConvert.SerializeObject(keys, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() })).Keys);
                }
            }
            """.Replace("NAMES", string.Join(", ", names.Select(n => "\"" + n + "\"")), StringComparison.Ordinal);

        foreach (var version in NewtonsoftReleases.All)
        {
            var expected = string.Join("|", names.Select(n => CamelCase.For(@case.LabelFor(version), n)));
            Assert.Equal(expected, NewtonsoftReleases.Run(example, version));
        }
    }

    [Fact]
    public void Every_rule_has_a_case_and_every_case_spans_the_supported_releases()
    {
        Assert.Equal(NewtonsoftRules.Measured, NewtonsoftReleases.All[^1]);
        Assert.NotEmpty(NewtonsoftRules.All);
        foreach (var rule in NewtonsoftRules.All)
        {
            Assert.NotEmpty(rule.Cases);
            foreach (var @case in rule.Cases)
            {
                Assert.All(@case.Segments.Skip(1), s => Assert.Contains(s.From!, NewtonsoftReleases.All));
            }
        }
    }

    /// <summary>Labels whose releases disagree, and labels whose outputs are the same as another's.</summary>
    private static List<string> Violations(NewtonsoftCase @case, IReadOnlyList<(NuGetVersion Version, string Output)> outputs)
    {
        var byLabel = outputs.GroupBy(o => @case.LabelFor(o.Version)).ToList();
        return
        [
            .. byLabel.Where(g => g.Select(o => o.Output).Distinct().Count() > 1).Select(g => $"outputs differ within '{g.Key}'"),
            .. byLabel.SelectMany(a => byLabel.Where(b => string.CompareOrdinal(a.Key, b.Key) < 0 && a.First().Output == b.First().Output)
                .Select(b => $"'{a.Key}' and '{b.Key}' have the same output")),
        ];
    }

    private static List<(NuGetVersion Version, string Output)> Run(NewtonsoftCase @case) =>
        [.. NewtonsoftReleases.All.Select(v => (v, NewtonsoftReleases.Run(@case.Example, v)))];

    private static string Table(NewtonsoftCase @case, IEnumerable<(NuGetVersion Version, string Output)> outputs)
    {
        var table = new StringBuilder();
        foreach (var (version, output) in outputs)
        {
            table.Append(CultureInfo.InvariantCulture, $"  {version,-8} {@case.LabelFor(version),-28} {output}\n");
        }

        return table.ToString();
    }
}
