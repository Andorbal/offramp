using Offramp.Analysis.Compilations;
using Offramp.Analysis.Seams;
using Offramp.Core.Diagnostics;
using Offramp.Fixtures;
using Offramp.Workspace.Store;

namespace Offramp.Analysis.Tests;

public sealed class SeamsTests
{
    [Fact]
    [ProducesDiagnostic("OFR4002")]
    [ProducesDiagnostic("OFR4003")]
    public async Task The_directory_lookup_is_the_articulation_point()
    {
        var (result, bag) = await AnalyzeAsync(["System.DirectoryServices"]);

        Assert.Equal(
            ["Accounts.Directory.CachedDirectoryLookup", "Accounts.Directory.DirectoryCache", "Accounts.Directory.DirectoryLookup"],
            result.Tainted.Select(t => t.Type));
        Assert.Equal(["inherits Accounts.Directory.DirectoryLookup"], result.Tainted[0].Reason);
        Assert.Contains("System.DirectoryServices.DirectorySearcher", result.Tainted[2].Reason);

        var seam = Assert.Single(result.Seams);
        Assert.Equal(("seam-1", "Accounts.Directory.DirectoryLookup", "IDirectoryLookup", true), (seam.Id, seam.BoundaryType, seam.ProposedInterface, seam.ArticulationPoint));
        Assert.Equal(["Accounts.Auth.LoginHandler", "Accounts.Users.UserService"], seam.Callers);
        Assert.Equal(
            [
                ("Accounts.Directory.DirectoryEntryInfo FindUser(string samAccountName)", 2, true, false),
                ("System.Collections.Generic.List<string> GroupsOf(string samAccountName)", 1, true, false),
                ("static bool IsAvailable()", 1, true, true),
                ("void Watch(System.Action<string> onChange)", 1, false, false),
            ],
            seam.Members.Select(m => (m.Signature, m.CallSites, m.WireFriendly, m.Static)));
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR4002" && d.Message.Contains("Watch", StringComparison.Ordinal));
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR4003" && d.Message.Contains("IsAvailable", StringComparison.Ordinal));
        Assert.Equal("Accounts.Windows", result.Extraction!.MoveToProject);
        Assert.Contains(result.Edges, e => e.From == "Accounts.Users.UserService" && e.To == "Accounts.Directory.DirectoryLookup" && e.Cut);
        Assert.DoesNotContain(result.Edges, e => e.From == "Accounts.Reports.ReportService" && e.Cut);
    }

    [Fact]
    [ProducesDiagnostic("OFR4001")]
    public async Task Nothing_unportable_means_no_seam()
    {
        var (result, bag) = await AnalyzeAsync(["System.Messaging"]);

        Assert.Empty(result.Tainted);
        Assert.Empty(result.Seams);
        Assert.True(bag.Contains("OFR4001"));
    }

    [Fact]
    public async Task A_boundary_wider_than_max_cut_is_refused()
    {
        var (result, bag) = await AnalyzeAsync(["System.DirectoryServices"], maxCut: 1);

        Assert.Empty(result.Seams);
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR4001" && d.Message.Contains("--max-cut 1", StringComparison.Ordinal));
    }

    private static async Task<(SeamsResult Result, DiagnosticBag Diagnostics)> AnalyzeAsync(string[] symbols, int? maxCut = null)
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        var model = WorkspaceStore.Read(fixture.WorkspacePath);
        var project = model.Projects.Single(p => p.Name == "Accounts");
        using var loader = new CompilationLoader(fixture.Root);
        var bag = new DiagnosticBag();
        var result = SeamsAnalyzer.Analyze(new SeamsRequest
        {
            RepositoryRoot = fixture.Root,
            Project = project,
            UnportableFrom = "list",
            Symbols = symbols,
            MaxCut = maxCut,
            Diagnostics = bag,
        }, loader.LoadForProject(project)!);
        return (result!, bag);
    }
}
