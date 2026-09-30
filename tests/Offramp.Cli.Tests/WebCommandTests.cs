using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Offramp.Core.Processes;
using Offramp.Fixtures;

namespace Offramp.Cli.Tests;

/// <summary><c>web inventory</c> and <c>web scaffold</c> on the <c>mvc5</c> fixture.</summary>
public sealed class WebCommandTests
{
    [Fact]
    public async Task Web_inventory_lists_what_the_application_is_made_of()
    {
        var fixture = await ScannedFixtures.ScanAsync("mvc5");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("web", "inventory", "--project", "Shop.Web", "--json");
        var markdown = await cli.RunAsync("web", "inventory", "--project", "Shop.Web", "--format", "markdown");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "web-inventory");
        await Verify(Scrub.Envelope(run.Out, repository.Path), extension: "json");
        Assert.Equal(0, markdown.ExitCode);
        Assert.Contains("| Shop.Web.Controllers.HomeController | mvc | Health | any | convention |", markdown.Out, StringComparison.Ordinal);
    }

    /// <summary>The acceptance test: the scaffolded application builds, serves what was ported, and proxies the rest.</summary>
    [Fact]
    [ProducesDiagnostic("OFR4201")]
    [ProducesDiagnostic("OFR4202")]
    public async Task Web_scaffold_builds_serves_the_ported_actions_and_proxies_the_rest()
    {
        var fixture = await ScannedFixtures.ScanAsync("mvc5");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var dryRun = await cli.RunAsync("web", "scaffold", "--project", "Shop.Web", "--new", "src/Shop.Web.Core", "--json");

        Assert.Equal(0, dryRun.ExitCode);
        SchemaAssert.ValidEnvelope(dryRun.Out, "web-scaffold");
        Assert.False(repository.Directory.Exists("src/Shop.Web.Core"));
        await Verify(Scrub.Envelope(dryRun.Out, repository.Path), extension: "json");
        var home = JsonNode.Parse(dryRun.Out)!["result"]!["controllers"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "Shop.Web.Controllers.HomeController")!;
        Assert.Contains(home["unported"]!.AsArray(), u => u!["action"]!.GetValue<string>() == "Config" && u["reason"]!.GetValue<string>().Contains("CS1061", StringComparison.Ordinal));

        var applied = await cli.RunAsync("web", "scaffold", "--project", "Shop.Web", "--new", "src/Shop.Web.Core", "--apply", "--json");
        Assert.True(applied.ExitCode == 0, applied.Out);
        var build = await ProcessRunner.Instance.RunAsync(new ProcessSpec("dotnet", ["build", "src/Shop.Web.Core/Shop.Web.Core.csproj", "-nologo", "-v:q"])
        {
            WorkingDirectory = repository.Path,
            Timeout = TimeSpan.FromMinutes(10),
        });
        Assert.True(build.ExitCode == 0, build.StandardOutput + build.StandardError);

        await using var legacy = new FakeLegacyApplication();
        var port = FreePort();
        using var app = StartApplication(repository.Path, port, legacy.Port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) };
        await WaitUntilServingAsync(client, app);

        Assert.Equal("ok", await GetAsync(client, "Home/Health"));
        Assert.Contains("\"name\":\"Kettle\"", await GetAsync(client, "api/products"), StringComparison.Ordinal);
        Assert.Contains("\"category\":\"tea\"", await GetAsync(client, "catalog/tea"), StringComparison.Ordinal);
        Assert.Equal("legacy /Home/About", await GetAsync(client, "Home/About"));
        Assert.Equal("legacy /Home/Config", await GetAsync(client, "Home/Config"));
        Assert.Equal("legacy /Legacy/Report.aspx", await GetAsync(client, "Legacy/Report.aspx"));
        Assert.Equal("legacy /thumbnail.axd", await GetAsync(client, "thumbnail.axd"));
    }

    /// <summary>
    /// SmartStoreNET's registrations (field test P1 #11, ADR 0059): routes through the codebase's
    /// helpers and a local function, Web API and OData routes and Autofac filters in the library
    /// the site references, areas declared by routes, a bundle in a local.
    /// </summary>
    [Fact]
    public async Task Web_inventory_follows_route_helpers_and_reads_the_referenced_library()
    {
        var fixture = await ScannedFixtures.ScanAsync("web-registrations");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("web", "inventory", "--project", "Store.Web", "--json");
        var markdown = await cli.RunAsync("web", "inventory", "--project", "Store.Web", "--format", "markdown");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "web-inventory");
        await Verify(Scrub.Envelope(run.Out, repository.Path), extension: "json");
        var result = JsonNode.Parse(run.Out)!["result"]!;
        var routes = result["routes"]!.AsArray().Select(r => r!).ToList();
        JsonNode Route(string name) => routes.Single(r => r["name"]!.GetValue<string>() == name);
        string? Text(JsonNode node, string property) => node[property]?.GetValue<string>();

        // Through MapLocalizedRoute's overloads, four calls from the routes.Add, with the call's arguments.
        Assert.Equal(("login/", "LocalizedRouteExtensions.MapLocalizedRoute", "src/Store.Web/Infrastructure/StoreRoutes.cs"),
            (Text(Route("Login"), "template"), Text(Route("Login"), "helper"), Text(Route("Login"), "file")));
        Assert.Equal(["controller = Home", "action = Wishlist", "customerGuid = ?"], Route("Wishlist")["defaults"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal("", Text(Route("HomePage"), "template"));

        // A local function around MapRoute, with a template computed from the settings: shown as computed.
        Assert.Equal(("(computed)", "MediaSettings.PublicPath() + MediaSettings.Tenant() + \"/uploaded/{*path}\"", "RegisterMediaRoute"),
            (Text(Route("MediaUploaded"), "template"), Text(Route("MediaUploaded"), "computed"), Text(Route("MediaUploaded"), "helper")));
        Assert.Contains("action = Uploaded", Route("MediaUploaded")["defaults"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal(["UrlTemplateFor(\"Category\")", "UrlTemplateFor(\"Product\")"],
            routes.Where(r => Text(r, "helper") == "LocalizedRouteExtensions.CreateLocalizedRoute").Select(r => Text(r, "computed")!).Order(StringComparer.Ordinal));
        Assert.Equal("path.Route", Text(Route("(computed)"), "computed"));

        // The library's Web API and OData routes, named by static properties.
        Assert.Equal(("webapi", "api/{version}/{controller}/{id}", "src/Store.Framework/WebApi/WebApiStartup.cs"),
            (Text(Route("WebApi.Default"), "kind"), Text(Route("WebApi.Default"), "template"), Text(Route("WebApi.Default"), "file")));
        Assert.Equal(("odata", "odata/v1"), (Text(Route("WebApi.OData"), "kind"), Text(Route("WebApi.OData"), "template")));

        // Areas declared by routes: an area default, a chained DataTokens["area"], the helper's parameter.
        Assert.Equal(("Admin", "Store.Tax", "Reports"), (Text(Route("Admin_default"), "area"), Text(Route("Store.Tax.Configure"), "area"), Text(Route("Reports_default"), "area")));
        Assert.Equal(["Admin", "Reports", "Store.Tax"], result["areas"]!.AsArray().Select(a => a!.GetValue<string>()));

        // A copy of the route table the library filters is not a registration.
        Assert.DoesNotContain(routes, r => Text(r, "file")!.EndsWith("AreaRouteFilter.cs", StringComparison.Ordinal));

        // Filters registered with Autofac, in the library and the site.
        var filters = result["containerFilters"]!.AsArray().Select(f => $"{Text(f!, "filter")} {Text(f!, "kind")} {Text(f!, "controller")} {Text(f!, "action")}").ToList();
        Assert.Contains("Store.Framework.Filters.HandleExceptionFilter exception Store.Framework.Controllers.StoreController ", filters);
        Assert.Contains("Store.Framework.Filters.HandleExceptionFilter action Store.Framework.Controllers.StoreController ", filters);
        Assert.Contains("Store.Framework.Filters.CookieConsentFilter action Store.Framework.Controllers.PublicControllerBase ", filters);
        Assert.Contains("Store.Web.Infrastructure.PreviewFilter result Store.Web.Controllers.HomeController Index", filters);
        Assert.Equal(["HandleError"], result["globalFilters"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(["~/bundles/app", "~/bundles/vendor"], result["bundles"]!.AsArray().Select(b => b!.GetValue<string>()));

        Assert.Equal(0, markdown.ExitCode);
        Assert.Contains("| MediaImage | (computed): `MediaSettings.PublicPath() + \"image/{*path}\"` | mvc | controller = Media, action = Image | RegisterMediaRoute |", markdown.Out, StringComparison.Ordinal);
        Assert.Contains("| Store.Web.Infrastructure.PreviewFilter | result | Store.Web.Controllers.HomeController | Index |", markdown.Out, StringComparison.Ordinal);
    }

    /// <summary>The routes the helpers declare are mapped; a computed template is left to the proxy (OFR4205).</summary>
    [Fact]
    [ProducesDiagnostic("OFR4205")]
    public async Task Web_scaffold_maps_the_helpers_routes_and_leaves_computed_templates_to_the_legacy_application()
    {
        var fixture = await ScannedFixtures.ScanAsync("web-registrations");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("web", "scaffold", "--project", "Store.Web", "--new", "src/Store.Web.Core", "--json");

        Assert.Equal(0, run.ExitCode);
        SchemaAssert.ValidEnvelope(run.Out, "web-scaffold");
        await Verify(Scrub.Envelope(run.Out, repository.Path), extension: "json");
        var node = JsonNode.Parse(run.Out)!;
        var routes = node["result"]!["routes"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Contains("app.MapControllerRoute(name: \"Login\", pattern: \"login/\", defaults: new { controller = \"Home\", action = \"Login\" });", routes);
        Assert.DoesNotContain(routes, r => r.Contains("Media", StringComparison.Ordinal) || r.Contains("(computed)", StringComparison.Ordinal));
        var computed = node["diagnostics"]!.AsArray().Where(d => d!["code"]!.GetValue<string>() == "OFR4205").Select(d => d!["message"]!.GetValue<string>()).ToList();
        Assert.Contains(computed, m => m.StartsWith("Route MediaImage has a template computed at run time (MediaSettings.PublicPath() + \"image/{*path}\")", StringComparison.Ordinal));
    }

    [Fact]
    [ProducesDiagnostic("OFR4203")]
    public async Task Code_outside_the_actions_that_does_not_compile_is_reported()
    {
        var fixture = await ScannedFixtures.ScanAsync("mvc5", (root, request) =>
        {
            var model = Path.Combine(root, "src/Shop.Web/Models/Product.cs");
            File.WriteAllText(model, File.ReadAllText(model).Replace("public decimal Price { get; set; }",
                "public decimal Price { get; set; }\n\n        public string Source => System.AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;", StringComparison.Ordinal));
            return request;
        });
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("web", "scaffold", "--project", "Shop.Web", "--new", "src/Shop.Web.Core", "--json");

        Assert.Contains("\"code\": \"OFR4203\"", run.Out, StringComparison.Ordinal);
        Assert.Contains("Product.cs", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    [ProducesDiagnostic("OFR4204")]
    public async Task A_folder_with_files_is_left_alone()
    {
        var fixture = await ScannedFixtures.ScanAsync("mvc5");
        using var repository = fixture.Repository;
        repository.Directory.Write("offramp.yml", "version: 1\n");
        repository.Directory.Write("src/Shop.Web.Core/notes.txt", "mine\n");
        using var cli = new CliHarness(repository.Directory).WithRealGitAndBuilds();

        var run = await cli.RunAsync("web", "scaffold", "--project", "Shop.Web", "--new", "src/Shop.Web.Core", "--proxy", "none", "--apply", "--json");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("\"code\": \"OFR4204\"", run.Out, StringComparison.Ordinal);
        Assert.Equal(["notes.txt"], Directory.GetFileSystemEntries(Path.Combine(repository.Path, "src/Shop.Web.Core")).Select(Path.GetFileName));
    }

    private static RunningApplication StartApplication(string repositoryRoot, int port, int legacyPort)
    {
        // The project's folder is the content root, as with dotnet run: appsettings.json is read from there.
        var info = new ProcessStartInfo("dotnet", ["bin/Debug/net10.0/Shop.Web.Core.dll", "--urls", $"http://127.0.0.1:{port}"])
        {
            WorkingDirectory = Path.Combine(repositoryRoot, "src", "Shop.Web.Core"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["ReverseProxy__Clusters__legacy__Destinations__app__Address"] = $"http://127.0.0.1:{legacyPort}/";
        var process = Process.Start(info)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new RunningApplication(process, output);
    }

    private static async Task WaitUntilServingAsync(HttpClient client, RunningApplication app)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            Assert.False(app.Process.HasExited, "The scaffolded application exited:\n" + app.Output);
            try
            {
                using var response = await client.GetAsync("Home/Health", TestContext.Current.CancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The scaffolded application did not start serving:\n" + app.Output);
    }

    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {body}");
        return body;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The scaffolded application, stopped (with its process tree) when the test is done.</summary>
    private sealed class RunningApplication(Process process, StringBuilder output) : IDisposable
    {
        public Process Process { get; } = process;

        public string Output
        {
            get
            {
                lock (output)
                {
                    return output.ToString();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10_000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Process.Dispose();
        }
    }

    /// <summary>The legacy application behind the proxy: answers every request with <c>legacy PATH</c>.</summary>
    private sealed class FakeLegacyApplication : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeLegacyApplication()
        {
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync();
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }

                var body = Encoding.UTF8.GetBytes("legacy " + (requestLine?.Split(' ') is [_, var path, ..] ? path : "/"));
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
