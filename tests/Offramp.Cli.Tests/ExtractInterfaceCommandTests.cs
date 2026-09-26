using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>extract interface</c> on the <c>seams</c> fixture.</summary>
public sealed class ExtractInterfaceCommandTests
{
    private const string Config = "version: 1\nseams:\n  unportableSources: [ list ]\n  unportableSymbols: [ System.DirectoryServices ]\n";

    [Fact]
    [ProducesDiagnostic("OFR4010")]
    [ProducesDiagnostic("OFR4003")]
    public async Task From_seams_extracts_the_interface_retypes_injection_and_builds()
    {
        var fixture = await ScannedFixtures.ScanAsync("seams");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", Config);
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var seams = await cli.RunAsync("seams", "--project", "Accounts", "--out", "seams.json");
        Assert.Equal(0, seams.ExitCode);

        var dryRun = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--from-seams", "seams.json#seam-1", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "extract-interface");
        Assert.Contains("OFR4010", dryRun.Out, StringComparison.Ordinal);
        Assert.Contains("OFR4003", dryRun.Out, StringComparison.Ordinal);
        Assert.False(repository.Directory.Exists("src/Accounts/Directory/IDirectoryLookup.cs"));
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");

        var applied = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--from-seams", "seams.json#seam-1", "--apply", "--json");

        Assert.Equal(0, applied.ExitCode);
        Assert.Contains("public interface IDirectoryLookup", repository.Directory.Read("src/Accounts/Directory/IDirectoryLookup.cs"), StringComparison.Ordinal);
        Assert.Contains("public class DirectoryLookup : IDirectoryLookup", repository.Directory.Read("src/Accounts/Directory/DirectoryLookup.cs"), StringComparison.Ordinal);
        var userService = repository.Directory.Read("src/Accounts/Users/UserService.cs");
        Assert.Contains("private readonly IDirectoryLookup _lookup;", userService, StringComparison.Ordinal);
        Assert.Contains("public UserService(IDirectoryLookup lookup)", userService, StringComparison.Ordinal);
        Assert.Contains("new DirectoryLookup()", repository.Directory.Read("src/Accounts/Auth/LoginHandler.cs"), StringComparison.Ordinal);

        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "Seams.sln", "-nologo", "-v:q"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(5),
        });
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
    }

    [Fact]
    public async Task Members_limit_the_interface_and_keep_callers_that_need_more()
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--type", "Accounts.Directory.DirectoryLookup", "--name", "IUserFinder", "--members", "FindUser", "--di", "autofac", "--json");

        Assert.Equal(0, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        Assert.Equal(["Accounts.Directory.DirectoryEntryInfo FindUser(string samAccountName)"], result["members"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Empty(result["callers"]!.AsArray());
        Assert.Equal("builder.RegisterType<Accounts.Directory.DirectoryLookup>().As<Accounts.Directory.IUserFinder>();", result["registration"]!.GetValue<string>());
        Assert.Equal("src/Accounts/Directory/IUserFinder.cs", result["file"]!.GetValue<string>());
    }

    [Fact]
    [ProducesDiagnostic("OFR4011")]
    public async Task A_type_the_project_does_not_declare_is_a_usage_error()
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var run = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--type", "Accounts.Directory.Missing", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR4011", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4013")]
    public async Task An_unknown_seam_is_a_usage_error()
    {
        var fixture = await ScannedFixtures.GetAsync("seams");
        fixture.Repository.Directory.Write("not-seams.json", "{ \"graph\": [] }");
        using var cli = new CliHarness(fixture.Repository.Directory);

        var missing = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--from-seams", "not-seams.json#seam-9", "--json");

        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("OFR4013", missing.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4012")]
    public async Task An_extraction_that_would_not_compile_writes_nothing()
    {
        // A class already named IDirectoryLookup: the generated interface would be a duplicate definition.
        var fixture = await ScannedFixtures.ScanAsync("seams", (root, request) =>
        {
            File.WriteAllText(Path.Combine(root, "src/Accounts/Directory/Names.cs"), "namespace Accounts.Directory\n{\n    public class IDirectoryLookup\n    {\n    }\n}\n");
            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory);
        var before = repository.Directory.Read("src/Accounts/Users/UserService.cs");

        var run = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--type", "Accounts.Directory.DirectoryLookup", "--apply", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("OFR4012", run.Out, StringComparison.Ordinal);
        Assert.Contains("CS0101", run.Out, StringComparison.Ordinal);
        Assert.False(repository.Directory.Exists("src/Accounts/Directory/IDirectoryLookup.cs"));
        Assert.Equal(before, repository.Directory.Read("src/Accounts/Users/UserService.cs"));
    }
}
