using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary>
/// <c>remote</c> on the <c>seams</c> fixture after <c>extract interface</c>: generated projects
/// build on every OS, and a client call crosses the HTTP boundary to the host and back.
/// </summary>
public sealed class RemoteCommandTests
{
    private const string Interface = "Accounts.Directory.IDirectoryLookup";

    [Fact]
    [ProducesDiagnostic("OFR4020")]
    public async Task The_net10_host_contracts_and_client_build_and_a_call_round_trips()
    {
        using var repository = await ExtractedAsync();
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("remote", "--interface", Interface, "--skip-member", "Watch", "--container", "--async-variant", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "remote");
        var result = JsonNode.Parse(dryRun.Out)!["result"]!;
        Assert.Equal("net10.0-windows", result["hostFramework"]!.GetValue<string>());
        Assert.False(repository.Directory.Exists("src/Accounts.Windows.Host/Accounts.Windows.Host.csproj"));
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");

        var applied = await cli.RunAsync("remote", "--interface", Interface, "--skip-member", "Watch", "--container", "--async-variant", "--apply", "--json");

        Assert.Equal(0, applied.ExitCode);
        foreach (var project in new[] { "src/Accounts.Remote.Contracts", "src/Accounts.Remote", "src/Accounts.Windows.Host" })
        {
            var build = await DotnetAsync(repository, "build", project, "-nologo", "-v:q");
            Assert.True(build.ExitCode == 0, project + ":\n" + build.StandardOutput + build.StandardError);
        }

        // The host runs in-process (WebApplicationFactory) with a stand-in directory; the generated
        // client calls it over HTTP. The host targets net10.0-windows but needs no Windows API here.
        repository.Directory.Write("tests/RoundTrip/RoundTrip.csproj", RoundTripProject);
        repository.Directory.Write("tests/RoundTrip/RoundTripTests.cs", RoundTripTests);
        var test = await DotnetAsync(repository, "test", "tests/RoundTrip", "-nologo");
        Assert.True(test.ExitCode == 0, test.StandardOutput + test.StandardError);
        Assert.Contains("Passed:     1", test.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4022")]
    public async Task An_implementation_that_needs_system_web_gets_the_net48_host_which_builds()
    {
        using var repository = await ExtractedAsync(root =>
        {
            // HttpRuntime is in no modern .NET, even with Microsoft.Windows.Compatibility.
            var project = Path.Combine(root, "src/Accounts/Accounts.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<Reference Include=\"System.DirectoryServices\" />", "<Reference Include=\"System.DirectoryServices\" />\n    <Reference Include=\"System.Web\" />", StringComparison.Ordinal));
            var cache = Path.Combine(root, "src/Accounts/Directory/DirectoryCache.cs");
            File.WriteAllText(cache, File.ReadAllText(cache).Replace("public void Remember(", "public string Application => System.Web.HttpRuntime.AppDomainAppId;\n\n        public void Remember(", StringComparison.Ordinal));
        });
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("remote", "--interface", Interface, "--skip-member", "Watch", "--serializer", "newtonsoft", "--container", "--apply", "--json");

        Assert.Equal(0, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!;
        Assert.Equal("net48", result["result"]!["hostFramework"]!.GetValue<string>());
        Assert.Contains(result["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4022" && d["message"]!.GetValue<string>().Contains("HttpRuntime", StringComparison.Ordinal));
        Assert.Contains("FROM mcr.microsoft.com/dotnet/framework/aspnet:4.8", repository.Directory.Read("src/Accounts.Windows.Host/Dockerfile"), StringComparison.Ordinal);
        foreach (var project in new[] { "src/Accounts.Remote", "src/Accounts.Windows.Host" })
        {
            var build = await DotnetAsync(repository, "build", project, "-nologo", "-v:q");
            Assert.True(build.ExitCode == 0, project + ":\n" + build.StandardOutput + build.StandardError);
        }
    }

    [Fact]
    [ProducesDiagnostic("OFR4021")]
    public async Task Blocked_members_and_existing_directories_generate_nothing()
    {
        using var repository = await ExtractedAsync();
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var blocked = await cli.RunAsync("remote", "--interface", Interface, "--apply", "--json");
        repository.Directory.Write("src/Accounts.Remote/notes.txt", "taken\n");
        var taken = await cli.RunAsync("remote", "--interface", Interface, "--skip-member", "Watch", "--apply", "--json");
        var unknown = await cli.RunAsync("remote", "--interface", "Accounts.Directory.IMissing", "--json");

        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("\"code\": \"OFR4002\"", blocked.Out, StringComparison.Ordinal);
        Assert.Equal(1, taken.ExitCode);
        Assert.Contains("\"code\": \"OFR4021\"", taken.Out, StringComparison.Ordinal);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("\"code\": \"OFR4023\"", unknown.Out, StringComparison.Ordinal);
        Assert.False(repository.Directory.Exists("src/Accounts.Remote.Contracts/Accounts.Remote.Contracts.csproj"));
        Assert.False(repository.Directory.Exists("src/Accounts.Windows.Host/Program.cs"));
    }

    /// <summary>The seams fixture with IDirectoryLookup extracted and scanned again.</summary>
    private static async Task<FixtureRepository> ExtractedAsync(Action<string>? prepare = null)
    {
        var fixture = await ScannedFixtures.ScanAsync("seams", (root, request) =>
        {
            prepare?.Invoke(root);
            return request;
        });
        var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var extract = await cli.RunAsync("extract", "interface", "--project", "Accounts", "--type", "Accounts.Directory.DirectoryLookup", "--apply");
        Assert.True(extract.ExitCode == 0, extract.Out + extract.Error);
        var scan = await cli.RunAsync("scan");
        Assert.True(scan.ExitCode == 0, scan.Out + scan.Error);
        return repository;
    }

    private static Task<ProcessResult> DotnetAsync(FixtureRepository repository, params string[] arguments) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", arguments)
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(10),
        });

    private const string RoundTripProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <EnableWindowsTargeting>true</EnableWindowsTargeting>
            <OutputType>Exe</OutputType>
            <Nullable>disable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <IsPackable>false</IsPackable>
            <NoWarn>$(NoWarn);xUnit1051</NoWarn>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
            <PackageReference Include="xunit.v3" Version="3.2.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="..\..\src\Accounts.Windows.Host\Accounts.Windows.Host.csproj" />
            <!-- The client's code, compiled against the host's copy of the interface. -->
            <Compile Include="..\..\src\Accounts.Remote\*.cs" LinkBase="Client" />
          </ItemGroup>
        </Project>

        """;

    private const string RoundTripTests = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Accounts.Directory;
        using Accounts.Remote;
        using Accounts.Remote.Contracts;
        using Microsoft.AspNetCore.Mvc.Testing;
        using Microsoft.AspNetCore.TestHost;
        using Microsoft.Extensions.DependencyInjection;
        using Xunit;

        public sealed class RoundTripTests
        {
            [Fact]
            public async Task Calls_cross_the_boundary_and_come_back()
            {
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IDirectoryLookup, StandIn>()));
                var client = new RemoteDirectoryLookup(factory.CreateClient());

                Assert.Equal("J. Doe", client.FindUser("jdoe").DisplayName);
                Assert.Null(client.FindUser("nobody"));
                Assert.Equal(new List<string> { "CN=Admins" }, await client.GroupsOfAsync("jdoe"));
                var failure = await Assert.ThrowsAsync<RemoteInvocationException>(() => client.GroupsOfAsync("boom"));
                Assert.Equal(500, failure.StatusCode);
                Assert.Throws<NotSupportedException>(() => client.Watch(_ => { }));
            }

            private sealed class StandIn : IDirectoryLookup
            {
                public DirectoryEntryInfo FindUser(string samAccountName) =>
                    samAccountName == "jdoe" ? new DirectoryEntryInfo { SamAccountName = "jdoe", DisplayName = "J. Doe" } : null;

                public List<string> GroupsOf(string samAccountName) =>
                    samAccountName == "boom" ? throw new InvalidOperationException("directory unavailable") : new List<string> { "CN=Admins" };

                public void Watch(Action<string> onChange) => onChange("local");
            }
        }

        """;
}
