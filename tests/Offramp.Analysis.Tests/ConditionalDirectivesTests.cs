using Microsoft.CodeAnalysis.CSharp;
using Offramp.Analysis.Conditional;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Analysis.Tests;

public sealed class ConditionalDirectivesTests
{
    [Theory]
    [InlineData("NETFRAMEWORK", true, true)]
    [InlineData("NETFRAMEWORK", false, false)]
    [InlineData("!NETFRAMEWORK", true, false)]
    [InlineData("NETFRAMEWORK && DEBUG", false, false)]
    [InlineData("NETFRAMEWORK && DEBUG", true, null)]
    [InlineData("NETFRAMEWORK || DEBUG", true, true)]
    [InlineData("NETFRAMEWORK || DEBUG", false, null)]
    [InlineData("!(NETFRAMEWORK || NET48)", true, false)]
    [InlineData("DEBUG", true, null)]
    [InlineData("NETFRAMEWORK == true", false, false)]
    [InlineData("NETFRAMEWORK != DEBUG", true, null)]
    public void Conditions_are_decided_by_the_one_known_symbol(string condition, bool defined, bool? expected) =>
        Assert.Equal(expected, ConditionalDirectives.Evaluate(SyntaxFactory.ParseExpression(condition), "NETFRAMEWORK", defined));

    [Fact]
    public void Chains_carry_branch_lines_and_nested_chains_are_their_own()
    {
        const string Text = """
            class C
            {
            #if NETFRAMEWORK
                int a;
            #if DEBUG
                int b;
            #endif
            #elif NET10_0_OR_GREATER
                int c;
            #else
            #endif
            }
            """;

        var chains = ConditionalDirectives.Chains(Text);

        Assert.Equal(2, chains.Count);
        var outer = chains[0];
        Assert.Equal((2, 10), (outer.IfLine, outer.EndifLine));
        Assert.Equal(["NET10_0_OR_GREATER", "NETFRAMEWORK"], outer.Symbols);
        Assert.Equal([(2, 3, 6), (7, 8, 8), (9, 10, 9)], outer.Branches.Select(b => (b.DirectiveLine, b.FirstLine, b.LastLine)));
        Assert.Equal(5, outer.GuardedLines);
        Assert.Equal((4, 6), (chains[1].IfLine, chains[1].EndifLine));
    }

    [Fact]
    public async Task Report_counts_regions_and_guarded_lines_per_project()
    {
        var fixture = await ScannedFixtures.GetAsync("dual-target");
        var model = WorkspaceStore.Read(fixture.WorkspacePath);

        var report = IfdefReporter.Report(fixture.Root, model, null);
        var filtered = IfdefReporter.Report(fixture.Root, model, "DEBUG");

        var shared = Assert.Single(report.Projects);
        Assert.Equal("src/Shared/Shared.csproj", shared.Project);
        Assert.Equal([new IfdefSymbolCount("NETFRAMEWORK", 2, 4, 2)], shared.Symbols);
        Assert.Equal([new IfdefSymbolCount("NETFRAMEWORK", 2, 4, 2)], report.Totals);
        Assert.Empty(filtered.Projects);
        Assert.Empty(filtered.Totals);
    }
}
