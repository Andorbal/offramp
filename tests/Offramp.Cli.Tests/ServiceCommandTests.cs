using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>service</c> on the <c>windows-service</c> fixture: workers that build on every OS.</summary>
public sealed class ServiceCommandTests
{
    [Fact]
    [ProducesDiagnostic("OFR4101")]
    [ProducesDiagnostic("OFR4102")]
    [ProducesDiagnostic("OFR4103")]
    [ProducesDiagnostic("OFR4104")]
    [ProducesDiagnostic("OFR4105")]
    public async Task Servicebase_services_become_workers_that_build_with_health_and_both_hosts()
    {
        var fixture = await ScannedFixtures.ScanAsync("windows-service");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        Assert.All(fixture.Outcome.Model!.Projects, p => Assert.Equal(Core.Model.ProjectKind.Service, p.Kind));
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("service", "--project", "Heartbeat", "--health", "--dockerfile", "--k8s", "--host", "both", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "service");
        var envelope = JsonNode.Parse(dryRun.Out)!;
        var codes = envelope["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ToList();
        Assert.Equal(["OFR4101", "OFR4102", "OFR4103", "OFR4104", "OFR4105"], codes.Where(c => c.StartsWith("OFR41", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.False(repository.Directory.Exists("src/Heartbeat.Worker/Heartbeat.Worker.csproj"));
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");

        var applied = await cli.RunAsync("service", "--project", "Heartbeat", "--health", "--dockerfile", "--k8s", "--host", "both", "--apply", "--json");

        Assert.Equal(0, applied.ExitCode);
        var worker = repository.Directory.Read("src/Heartbeat.Worker/HeartbeatWorker.cs");
        Assert.Contains("await EveryAsync(System.TimeSpan.FromMilliseconds(5000), () => OnElapsed(null, null), stoppingToken);", worker, StringComparison.Ordinal);
        Assert.Contains("#if OFFRAMP_SERVICEBASE // OFR4102: ServiceBase.RequestAdditionalTime", worker, StringComparison.Ordinal);
        Assert.Contains("Log(Microsoft.Extensions.Logging.LogLevel.Warning, \"Heartbeat stopped after \"", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("_timer", worker, StringComparison.Ordinal);
        Assert.True(repository.Directory.Exists("src/Heartbeat.Worker/heartbeat-worker.service"));
        Assert.Contains("depend= EventLog", repository.Directory.Read("src/Heartbeat.Worker/install.ps1"), StringComparison.Ordinal);
        Assert.Contains("terminationGracePeriodSeconds: 30", repository.Directory.Read("src/Heartbeat.Worker/kubernetes.yaml"), StringComparison.Ordinal);
        await BuildAsync(repository, "src/Heartbeat.Worker");
    }

    [Fact]
    public async Task A_topshelf_service_becomes_a_windows_worker()
    {
        var fixture = await ScannedFixtures.GetAsync("windows-service");
        using var cli = new CliHarness(fixture.Repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("service", "--project", "Crier", "--host", "windows", "--out", "out/Crier.Worker", "--json");

        Assert.Equal(0, run.ExitCode);
        var result = JsonNode.Parse(run.Out)!["result"]!;
        var service = result["services"]![0]!;
        Assert.Equal(("topshelf", "TownCrier", "LocalSystem", "Automatic"), (service["kind"]!.GetValue<string>(), service["serviceName"]!.GetValue<string>(), service["account"]!.GetValue<string>(), service["startType"]!.GetValue<string>()));
        Assert.Equal(["out/Crier.Worker/Crier.Worker.csproj", "out/Crier.Worker/Program.cs", "out/Crier.Worker/TownCrierWorker.cs", "out/Crier.Worker/appsettings.json", "out/Crier.Worker/install.ps1", "out/Crier.Worker/uninstall.ps1"],
            result["files"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(["src/Crier/TownCrier.cs"], result["linkedSources"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Contains(result["removals"]!.AsArray(), r => r!["reason"]!.GetValue<string>().StartsWith("PackageReference Topshelf", StringComparison.Ordinal));
        Assert.Contains("_service.Start();", result["preview"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddWindowsService(options => options.ServiceName = \\\"TownCrier\\\");", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4106")]
    public async Task Linked_code_that_does_not_compile_for_the_target_is_reported()
    {
        var fixture = await ScannedFixtures.ScanAsync("windows-service", (root, request) =>
        {
            // HttpRuntime is in no modern .NET: the linked Pulse.cs cannot compile for net10.0.
            var project = Path.Combine(root, "src/Heartbeat/Heartbeat.csproj");
            File.WriteAllText(project, File.ReadAllText(project).Replace("<Reference Include=\"System.ServiceProcess\" />", "<Reference Include=\"System.ServiceProcess\" />\n    <Reference Include=\"System.Web\" />", StringComparison.Ordinal));
            var pulse = Path.Combine(root, "src/Heartbeat/Pulse.cs");
            File.WriteAllText(pulse, File.ReadAllText(pulse).Replace("Console.WriteLine(Source + \": \" + Count + \" beats\");", "Console.WriteLine(System.Web.HttpRuntime.AppDomainAppId + Source + \": \" + Count + \" beats\");", StringComparison.Ordinal));
            return request;
        });
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("service", "--project", "Heartbeat", "--json");

        Assert.Equal(0, run.ExitCode);
        var failure = Assert.Single(JsonNode.Parse(run.Out)!["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "OFR4106");
        Assert.Contains("HttpRuntime", failure!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4107")]
    [ProducesDiagnostic("OFR4108")]
    public async Task A_project_without_a_service_or_an_existing_output_generates_nothing()
    {
        var seams = await ScannedFixtures.GetAsync("seams");
        using var seamsCli = new CliHarness(seams.Repository.Directory);
        var none = await seamsCli.RunAsync("service", "--project", "Accounts", "--json");

        var fixture = await ScannedFixtures.GetAsync("windows-service");
        fixture.Repository.Directory.Write("taken/Crier.Worker/notes.txt", "taken\n");
        using var cli = new CliHarness(fixture.Repository.Directory);
        var taken = await cli.RunAsync("service", "--project", "Crier", "--out", "taken/Crier.Worker", "--apply", "--json");

        Assert.Equal(2, none.ExitCode);
        Assert.Contains("\"code\": \"OFR4107\"", none.Out, StringComparison.Ordinal);
        Assert.Equal(1, taken.ExitCode);
        Assert.Contains("\"code\": \"OFR4108\"", taken.Out, StringComparison.Ordinal);
        Assert.False(fixture.Repository.Directory.Exists("taken/Crier.Worker/Program.cs"));
    }

    /// <summary>
    /// Roadmap M10: the Linux image builds and answers on the health endpoint. Needs a Linux
    /// Docker engine, so it runs in CI's container job (<c>--filter Category=Docker</c>,
    /// <c>OFFRAMP_DOCKER_TESTS=1</c>); elsewhere it is excluded or reports itself skipped.
    /// </summary>
    [Fact]
    [Trait("Category", "Docker")]
    public async Task The_linux_image_builds_and_answers_on_the_health_endpoint()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OFFRAMP_DOCKER_TESTS") == "1", "Needs a Linux Docker engine: set OFFRAMP_DOCKER_TESTS=1 (CI's container job does).");
        var fixture = await ScannedFixtures.ScanAsync("windows-service");
        using var repository = fixture.Repository;
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();
        var apply = await cli.RunAsync("service", "--project", "Heartbeat", "--health", "--dockerfile", "--apply");
        Assert.True(apply.ExitCode == 0, apply.ToString());

        var tag = "offramp-service-smoke-" + Guid.NewGuid().ToString("N")[..8];
        var port = FreePort();
        try
        {
            var build = await DockerAsync(repository, TimeSpan.FromMinutes(15), "build", "-f", "src/Heartbeat.Worker/Dockerfile", "-t", tag, ".");
            Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);
            var run = await DockerAsync(repository, TimeSpan.FromMinutes(1), "run", "-d", "--name", tag, "-p", $"{port}:8080", tag);
            Assert.True(run.ExitCode == 0, run.StandardError);

            using var http = new HttpClient();
            HttpResponseMessage? response = null;
            for (var attempt = 0; attempt < 60 && response?.StatusCode != HttpStatusCode.OK; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                try
                {
                    response = await http.GetAsync(new Uri($"http://localhost:{port}/health"), TestContext.Current.CancellationToken);
                }
                catch (HttpRequestException)
                {
                }
            }

            var logs = await DockerAsync(repository, TimeSpan.FromMinutes(1), "logs", tag);
            Assert.True(response?.StatusCode == HttpStatusCode.OK, "The health endpoint did not answer 200.\n" + logs.StandardOutput + logs.StandardError);
            Assert.Contains("\"healthy\":true", await response!.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            await DockerAsync(repository, TimeSpan.FromMinutes(1), "rm", "-f", tag);
            await DockerAsync(repository, TimeSpan.FromMinutes(1), "rmi", "-f", tag);
        }
    }

    private static async Task BuildAsync(FixtureRepository repository, string project)
    {
        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", project, "-nologo", "-v:q"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(10),
        });
        Assert.True(build.ExitCode == 0, project + ":\n" + build.StandardOutput + build.StandardError);
    }

    private static Task<ProcessResult> DockerAsync(FixtureRepository repository, TimeSpan timeout, params string[] arguments) =>
        ProcessRunner.Instance.RunAsync(new ProcessSpec("docker", arguments) { WorkingDirectory = repository.Path, Timeout = timeout });

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
