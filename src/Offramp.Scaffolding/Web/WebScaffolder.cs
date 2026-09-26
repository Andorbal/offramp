using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding.Remote;
using Offramp.Workspace.Targets;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;

namespace Offramp.Scaffolding.Web;

/// <summary>What <c>web scaffold</c> is asked to do.</summary>
public sealed record WebScaffoldRequest
{
    public required string RepositoryRoot { get; init; }

    public required ProjectInfo Project { get; init; }

    /// <summary><c>--new</c>: the new project's folder, repository-relative.</summary>
    public required string NewDirectory { get; init; }

    /// <summary>The .NET major version from offramp.yml.</summary>
    public required int TargetMajor { get; init; }

    /// <summary><c>yarp</c> or <c>none</c>.</summary>
    public required string Proxy { get; init; }

    public bool Adapters { get; init; }

    /// <summary><c>--legacy-url</c>; null uses the project's IIS URL.</summary>
    public string? LegacyUrl { get; init; }

    /// <summary>Resolves the target's reference assemblies for the in-memory compile; null skips it.</summary>
    public TargetReferenceResolver? References { get; init; }

    public required DiagnosticBag Diagnostics { get; init; }
}

/// <summary>The dry run and the change set that writes it.</summary>
public sealed record WebScaffoldPlan(WebScaffoldResult Result, ChangeSet ChangeSet);

/// <summary>
/// <c>web scaffold</c> (docs/spec/commands/scaffold.md#web-scaffold): a new ASP.NET Core
/// project in front of the legacy application. It serves the controller actions that port
/// (<see cref="WebPorter"/>), keeps the convention routes, stubs HttpModules as middleware
/// and HttpHandlers as endpoints, and sends everything else to the legacy application:
/// YARP with a catch-all route, or an ingress path list with <c>--proxy none</c>.
/// <c>--adapters</c> shares session and authentication through the System.Web adapters.
/// The project is compiled in memory; actions the compiler rejects stay with the legacy
/// application.
/// </summary>
public static class WebScaffolder
{
    private const int MaxRounds = 8;

    /// <summary>The plan, or null (with OFR4204) when the new project's folder already has files.</summary>
    public static async Task<WebScaffoldPlan?> PlanAsync(WebScaffoldRequest request, Compilation compilation, CancellationToken cancellationToken)
    {
        var root = request.RepositoryRoot;
        var project = request.Project;
        var inventory = WebInventory.Analyze(root, project, compilation);
        var directory = RepoPaths.Normalize(request.NewDirectory).TrimEnd('/');
        var name = Path.GetFileName(directory);
        var tfm = string.Create(CultureInfo.InvariantCulture, $"net{request.TargetMajor}.0");
        var legacyUrl = request.LegacyUrl ?? inventory.Url ?? "http://localhost:5000/";
        var result = new WebScaffoldResult
        {
            Project = project.Id,
            NewProject = $"{directory}/{name}.csproj",
            TargetFramework = tfm,
            Proxy = request.Proxy,
            Adapters = request.Adapters,
            LegacyUrl = legacyUrl,
        };

        var target = RepoPaths.ToAbsolute(root, directory);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4204, $"{directory} already has files; nothing was generated.", new DiagnosticLocation(project.Id, directory));
            return null;
        }

        var systemWebFiles = inventory.SystemWeb.Select(s => s.File).ToHashSet(StringComparer.Ordinal);
        var controllers = inventory.Controllers
            .Select(c => WebPorter.Port(compilation, compilation.GetTypeByMetadataName(c.Type)!, c.Kind, c.File))
            .ToList();
        var ns = project.RootNamespace ?? project.Name;
        var modules = inventory.Modules.Where(m => m.File is not null).ToList();
        var handlers = inventory.Handlers.Where(h => h.File is not null).ToList();

        List<(string Path, string Text)> files = [];
        List<string> links = [];
        for (var round = 0; round < MaxRounds; round++)
        {
            (files, links) = Files(request, compilation, inventory, controllers, directory, name, tfm, ns, legacyUrl, modules, handlers, systemWebFiles);
            if (request.References is null || !await DropRejectedAsync(request, compilation, files, links, controllers, tfm, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        foreach (var page in inventory.WebForms.Where(f => !f.EndsWith(".ashx", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".asmx", StringComparison.OrdinalIgnoreCase)))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4201, $"{page} stays with the legacy application: keep it behind the proxy, rewrite it as a Razor Page or Blazor component, or use a third-party converter.",
                new DiagnosticLocation(project.Id, page));
        }

        foreach (var handler in handlers)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4202, $"{handler.Type} ({handler.Path ?? handler.Name}) is an endpoint stub in Endpoints/{Stub(handler.Type, "Handler", "Endpoint")}.cs; the proxy serves its path until it is ported and mapped.",
                new DiagnosticLocation(project.Id, handler.File));
        }

        var changeSet = new ChangeSet();
        foreach (var (path, text) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            changeSet.Create(path, text);
        }

        result = result with
        {
            Controllers = [.. controllers.Select(c => new WebPortedController
            {
                Type = c.Type.ToDisplayString(),
                File = c.Ported.Any() ? $"{directory}/Controllers/{c.Type.Name}.cs" : null,
                Ported = [.. c.Ported],
                Unported = [.. c.Unported.OrderBy(u => u.Action, StringComparer.Ordinal)],
                Notes = c.Notes,
            })],
            Routes = Routes(inventory, controllers),
            Middleware = [.. modules.Select(m => $"{directory}/Middleware/{Stub(m.Type, "Module", "Middleware")}.cs")],
            Endpoints = [.. handlers.Select(h => $"{directory}/Endpoints/{Stub(h.Type, "Handler", "Endpoint")}.cs")],
            WebForms = inventory.WebForms,
            IngressPaths = request.Proxy == "none" ? IngressPaths(inventory, controllers) : [],
            LinkedSources = links,
            Files = [.. files.Select(f => f.Path).Order(StringComparer.Ordinal)],
            NextSteps = NextSteps(request, result.NewProject, inventory, controllers),
            Preview = changeSet.Preview(),
        };
        return new WebScaffoldPlan(result, changeSet);
    }

    private static (List<(string Path, string Text)> Files, List<string> Links) Files(WebScaffoldRequest request, Compilation compilation, WebInventoryResult inventory,
        List<PortedControllerSource> controllers, string directory, string name, string tfm, string ns, string legacyUrl,
        List<WebComponent> modules, List<WebComponent> handlers, HashSet<string> systemWebFiles)
    {
        var root = request.RepositoryRoot;
        var files = new List<(string Path, string Text)>();
        var ported = controllers.Where(c => c.Ported.Any()).ToList();
        foreach (var controller in ported)
        {
            var header = $"// Ported by offramp web scaffold from {controller.SourceFile}.\n"
                + string.Concat(controller.Unported.OrderBy(u => u.Action, StringComparer.Ordinal).Select(u => $"// Not ported, served by the legacy application: {u.Action}: {u.Reason}\n"))
                + "\n";
            files.Add(($"{directory}/Controllers/{controller.Type.Name}.cs", WebPorter.Render(controller, header, out _)));
        }

        // The legacy project's files the ported code needs, as links: not the controllers, not files that use System.Web.
        var controllerTrees = controllers.Select(c => c.Type.DeclaringSyntaxReferences[0].SyntaxTree).ToHashSet();
        var roots = ported.SelectMany(c => c.Members.Where(m => m.Action is null || !c.Dropped.Contains(m.Action)).Select(m => m.Syntax))
            .SelectMany(s => s.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => compilation.GetSemanticModel(s.SyntaxTree).GetSymbolInfo(n).Symbol))
            .Select(s => s as INamedTypeSymbol ?? s?.ContainingType)
            .OfType<INamedTypeSymbol>()
            .Where(t => t.DeclaringSyntaxReferences.Length > 0)
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        var links = roots.Length == 0 ? [] : HostFramework.Closure(compilation, roots)
            .Where(t => !controllerTrees.Contains(t) && WebInventory.FileOf(root, t) is { } file && !systemWebFiles.Contains(file))
            .Select(t => WebInventory.FileOf(root, t)!)
            .ToList();

        var mvc = ported.Any(c => c.Kind == "mvc");
        files.Add(($"{directory}/{name}.csproj", ProjectFile(request, directory, tfm, ns, links)));
        files.Add(($"{directory}/Program.cs", Program(request, inventory, controllers, ns, mvc, modules, handlers)));
        files.Add(($"{directory}/appsettings.json", Settings(request, legacyUrl)));
        foreach (var module in modules)
        {
            files.Add(($"{directory}/Middleware/{Stub(module.Type, "Module", "Middleware")}.cs", MiddlewareStub(root, ns, module)));
        }

        foreach (var handler in handlers)
        {
            files.Add(($"{directory}/Endpoints/{Stub(handler.Type, "Handler", "Endpoint")}.cs", EndpointStub(root, ns, handler)));
        }

        if (request.Proxy == "none")
        {
            files.Add(($"{directory}/ingress-paths.txt", string.Concat(IngressPaths(inventory, controllers).Select(p => p + "\n"))));
        }

        return (files, links);
    }

    /// <summary>Compiles the project in memory; drops the actions with errors. True when something was dropped (compile again).</summary>
    private static async Task<bool> DropRejectedAsync(WebScaffoldRequest request, Compilation recorded, List<(string Path, string Text)> files, List<string> links,
        List<PortedControllerSource> controllers, string tfm, CancellationToken cancellationToken)
    {
        var packages = new List<(string Id, string Version)>();
        if (request.Proxy == "yarp")
        {
            packages.Add(("Yarp.ReverseProxy", ScaffoldPackages.Version("Yarp.ReverseProxy")));
        }

        if (request.Adapters)
        {
            packages.Add(("Microsoft.AspNetCore.SystemWebAdapters.CoreServices", ScaffoldPackages.Version("Microsoft.AspNetCore.SystemWebAdapters.CoreServices")));
        }

        var references = await request.References!.ResolveAsync(new TargetReferenceRequest { TargetFramework = tfm, Frameworks = ["Microsoft.AspNetCore.App"], Packages = packages }, cancellationToken).ConfigureAwait(false);
        if (references.Error is { } unresolved)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4203, $"The new project could not be compiled in memory: its {tfm} references did not resolve ({unresolved}).", new DiagnosticLocation(request.Project.Id));
            return false;
        }

        var options = new CSharpParseOptions(LanguageVersion.Default, preprocessorSymbols: TargetCompilation.TargetSymbols(request.TargetMajor, windows: false));
        var trees = files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Text, options, f.Path, cancellationToken: cancellationToken))
            .Concat(links.Select(l => CSharpSyntaxTree.ParseText(recorded.SyntaxTrees.First(t => WebInventory.FileOf(request.RepositoryRoot, t) == l).GetText(cancellationToken), options, l, cancellationToken)))
            .ToList();
        var compilation = CSharpCompilation.Create("Scaffold", trees, references.Paths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable));
        var errors = compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count == 0)
        {
            return false;
        }

        var dropped = false;
        foreach (var controller in controllers.Where(c => c.Ported.Any()))
        {
            var path = files.First(f => f.Path.EndsWith($"/Controllers/{controller.Type.Name}.cs", StringComparison.Ordinal)).Path;
            WebPorter.Render(controller, files.First(f => f.Path == path).Text[..Header(files.First(f => f.Path == path).Text)], out var spans);
            foreach (var error in errors.Where(e => e.Location.SourceTree?.FilePath == path).OrderBy(e => e.Location.SourceSpan.Start))
            {
                var action = spans.FirstOrDefault(s => s.Value.Contains(error.Location.SourceSpan.Start)).Key;
                var reason = $"{error.Id}: {error.GetMessage(CultureInfo.InvariantCulture)}";
                var victims = action is null ? controller.Ported.ToList() : [action];
                foreach (var victim in victims.Where(controller.Dropped.Add))
                {
                    controller.Unported.Add(new WebUnported(victim, action is null ? "the controller's shared code does not compile for ASP.NET Core: " + reason : "does not compile for ASP.NET Core: " + reason));
                    dropped = true;
                }
            }
        }

        if (!dropped)
        {
            var first = string.Join("; ", errors.Take(3).Select(d => $"{d.Id} {Path.GetFileName(d.Location.SourceTree?.FilePath)}: {d.GetMessage(CultureInfo.InvariantCulture)}"));
            request.Diagnostics.Report(DiagnosticCatalog.OFR4203, $"The new project has {errors.Count} compile error{(errors.Count == 1 ? "" : "s")} outside the ported actions: {first}", new DiagnosticLocation(request.Project.Id));
        }

        return dropped;
    }

    /// <summary>The length of a controller file's header comment block.</summary>
    private static int Header(string text)
    {
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        return end < 0 ? 0 : end + 2;
    }

    private static string ProjectFile(WebScaffoldRequest request, string directory, string tfm, string ns, List<string> links)
    {
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n\n  <PropertyGroup>\n");
        builder.Append(CultureInfo.InvariantCulture, $"    <TargetFramework>{tfm}</TargetFramework>\n    <RootNamespace>{ns}</RootNamespace>\n");
        builder.Append("    <Nullable>disable</Nullable>\n    <ImplicitUsings>disable</ImplicitUsings>\n");
        if (CentralPackages(request.RepositoryRoot, directory))
        {
            builder.Append("    <!-- Pins its own package versions; `offramp deps consolidate --cpm` can centralize them. -->\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n");
        }

        builder.Append("  </PropertyGroup>\n");
        var packages = new List<string>();
        if (request.Proxy == "yarp")
        {
            packages.Add("Yarp.ReverseProxy");
        }

        if (request.Adapters)
        {
            packages.Add("Microsoft.AspNetCore.SystemWebAdapters.CoreServices");
        }

        if (packages.Count > 0)
        {
            builder.Append("\n  <ItemGroup>\n");
            foreach (var package in packages.Order(StringComparer.Ordinal))
            {
                builder.Append(CultureInfo.InvariantCulture, $"    <PackageReference Include=\"{package}\" Version=\"{ScaffoldPackages.Version(package)}\" />\n");
            }

            builder.Append("  </ItemGroup>\n");
        }

        if (links.Count > 0)
        {
            var legacy = Path.GetDirectoryName(request.Project.Id.Replace('\\', '/'))!.Replace('\\', '/');
            builder.Append("\n  <!-- Compiled from the legacy project until the code moves (offramp move plan). -->\n  <ItemGroup>\n");
            foreach (var link in links.Order(StringComparer.Ordinal))
            {
                var relative = RemoteLayout.Relative(directory, link).Replace('/', '\\');
                var shown = link.StartsWith(legacy + "/", StringComparison.Ordinal) ? link[(legacy.Length + 1)..] : Path.GetFileName(link);
                builder.Append(CultureInfo.InvariantCulture, $"    <Compile Include=\"{relative}\" Link=\"Linked\\{shown.Replace('/', '\\')}\" />\n");
            }

            builder.Append("  </ItemGroup>\n");
        }

        builder.Append("\n</Project>\n");
        return builder.ToString();
    }

    private static string Program(WebScaffoldRequest request, WebInventoryResult inventory, List<PortedControllerSource> controllers, string ns, bool mvc,
        List<WebComponent> modules, List<WebComponent> handlers)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"// Generated by offramp web scaffold: the ASP.NET Core front of {request.Project.Id}. What it does not serve goes to the legacy application.\n");
        builder.Append(request.Adapters ? "using System;\n" : "");
        builder.Append("using Microsoft.AspNetCore.Builder;\n");
        builder.Append(request.Adapters ? "using Microsoft.Extensions.Configuration;\n" : "");
        builder.Append("using Microsoft.Extensions.DependencyInjection;\n\n");
        builder.Append("var builder = WebApplication.CreateBuilder(args);\n\n");
        builder.Append(mvc ? "builder.Services.AddControllersWithViews();\n" : "builder.Services.AddControllers();\n");
        if (request.Adapters)
        {
            builder.Append("""

                // Session and authentication are the legacy application's, shared through the System.Web adapters.
                builder.Services.AddSystemWebAdapters()
                    .AddJsonSessionSerializer()
                    .AddRemoteAppClient(options =>
                    {
                        options.RemoteAppUrl = new Uri(builder.Configuration["RemoteApp:Url"]);
                        options.ApiKey = builder.Configuration["RemoteApp:ApiKey"];
                    })
                    .AddSessionClient()
                    .AddAuthenticationClient(true);

                """);
        }

        if (request.Proxy == "yarp")
        {
            builder.Append("builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection(\"ReverseProxy\"));\n");
        }

        builder.Append("\nvar app = builder.Build();\n\n");
        foreach (var module in modules)
        {
            builder.Append(CultureInfo.InvariantCulture, $"app.UseMiddleware<{ns}.Middleware.{Stub(module.Type, "Module", "Middleware")}>();\n");
        }

        builder.Append("app.UseRouting();\n");
        if (request.Adapters)
        {
            builder.Append("app.UseAuthentication();\n");
        }
        else if (controllers.Any(c => c.Authorizes))
        {
            builder.Append("// [Authorize] on ported actions needs an authentication scheme: --adapters shares the legacy application's,\n// or add one with builder.Services.AddAuthentication(...) and app.UseAuthentication() here.\n");
        }

        builder.Append("app.UseAuthorization();\n");
        if (request.Adapters)
        {
            builder.Append("app.UseSystemWebAdapters();\n");
        }

        builder.Append('\n');
        foreach (var route in Routes(inventory, controllers))
        {
            builder.Append(route).Append('\n');
        }

        builder.Append("app.MapControllers();\n");
        foreach (var handler in handlers)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"// OFR4202: until {handler.Type} is ported, the proxy sends {handler.Path ?? handler.Name} to the legacy application. Then:\n// app.MapMethods(\"/{(handler.Path ?? handler.Name).TrimStart('/', '~')}\", new[] {{ \"{(handler.Verb is null or "*" ? "GET" : handler.Verb)}\" }}, {ns}.Endpoints.{Stub(handler.Type, "Handler", "Endpoint")}.Handle);\n");
        }

        if (request.Proxy == "yarp")
        {
            builder.Append("app.MapReverseProxy();\n");
        }

        builder.Append("\napp.Run();\n");
        return builder.ToString();
    }

    /// <summary>Convention routes as <c>MapControllerRoute</c> calls: the MVC ones, and an area's when a controller of the area is ported.</summary>
    private static List<string> Routes(WebInventoryResult inventory, List<PortedControllerSource> controllers)
    {
        var portedAreas = controllers.Where(c => c.Ported.Any()).Select(c => inventory.Controllers.First(i => i.Type == c.Type.ToDisplayString()).Area).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var route in inventory.Routes.Where(r => r.Kind == "mvc" && (r.Area is null || portedAreas.Contains(r.Area))))
        {
            var pattern = route.Template;
            var extra = new List<string>();
            foreach (var entry in route.Defaults)
            {
                var parts = entry.Split(" = ", 2);
                var (name, value) = (parts[0], parts[1]);
                if (pattern.Contains("{" + name + "}", StringComparison.Ordinal))
                {
                    pattern = pattern.Replace("{" + name + "}", value == "?" ? "{" + name + "?}" : "{" + name + "=" + value + "}", StringComparison.Ordinal);
                }
                else if (value != "?")
                {
                    extra.Add($"{name} = \"{value}\"");
                }
            }

            var defaults = extra.Count == 0 ? "" : $", defaults: new {{ {string.Join(", ", extra)} }}";
            result.Add(route.Area is null
                ? $"app.MapControllerRoute(name: \"{route.Name}\", pattern: \"{pattern}\"{defaults});"
                : $"app.MapAreaControllerRoute(name: \"{route.Name}\", areaName: \"{route.Area}\", pattern: \"{pattern}\"{defaults});");
        }

        return result;
    }

    private static List<string> IngressPaths(WebInventoryResult inventory, List<PortedControllerSource> controllers)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var controller in controllers)
        {
            var info = inventory.Controllers.First(i => i.Type == controller.Type.ToDisplayString());
            foreach (var action in info.Actions.Where(a => controller.Ported.Contains(a.Name)))
            {
                if (action.Routes.Count > 0)
                {
                    foreach (var route in action.Routes)
                    {
                        paths.Add("/" + route);
                    }
                }
                else
                {
                    var controllerName = info.Name.EndsWith("Controller", StringComparison.Ordinal) ? info.Name[..^"Controller".Length] : info.Name;
                    paths.Add(info.Kind == "webapi" ? $"/api/{controllerName}" : $"/{(info.Area is null ? "" : info.Area + "/")}{controllerName}/{action.Name}");

                    // Convention routes that name the controller and action in their defaults (catalog/{category}).
                    foreach (var route in inventory.Routes.Where(r => r.Kind == "mvc" && r.Area == info.Area && !r.Template.Contains("{controller}", StringComparison.Ordinal)
                        && r.Defaults.Contains("controller = " + controllerName) && r.Defaults.Contains("action = " + action.Name)))
                    {
                        paths.Add("/" + route.Template);
                    }
                }
            }
        }

        return [.. paths];
    }

    private static string Settings(WebScaffoldRequest request, string legacyUrl)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Default\": \"Information\",\n      \"Microsoft.AspNetCore\": \"Warning\"\n    }\n  },\n  \"AllowedHosts\": \"*\"");
        if (request.Adapters)
        {
            builder.Append(CultureInfo.InvariantCulture, $",\n  \"RemoteApp\": {{\n    \"Url\": \"{legacyUrl}\",\n    \"ApiKey\": \"\"\n  }}");
        }

        if (request.Proxy == "yarp")
        {
            builder.Append(CultureInfo.InvariantCulture, $$"""
                ,
                  "ReverseProxy": {
                    "Routes": {
                      "legacy": {
                        "ClusterId": "legacy",
                        "Order": 2147483647,
                        "Match": {
                          "Path": "{**catch-all}"
                        }
                      }
                    },
                    "Clusters": {
                      "legacy": {
                        "Destinations": {
                          "app": {
                            "Address": "{{legacyUrl}}"
                          }
                        }
                      }
                    }
                  }
                """);
        }

        builder.Append("\n}\n");
        return builder.ToString();
    }

    private static string MiddlewareStub(string root, string ns, WebComponent module)
    {
        var name = Stub(module.Type, "Module", "Middleware");
        var original = File.ReadAllText(RepoPaths.ToAbsolute(root, module.File!)).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return $$"""
            // Generated by offramp web scaffold from {{module.File}}.
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Http;

            namespace {{ns}}.Middleware
            {
                /// <summary>
                /// The {{module.Type}} HttpModule's place in the pipeline. Port its BeginRequest work
                /// before the call to the next middleware and its EndRequest work after; the original
                /// is below the namespace, not compiled.
                /// </summary>
                public sealed class {{name}}
                {
                    private readonly RequestDelegate _next;

                    public {{name}}(RequestDelegate next)
                    {
                        _next = next;
                    }

                    public async Task InvokeAsync(HttpContext context)
                    {
                        await _next(context);
                    }
                }
            }

            #if OFFRAMP_HTTPMODULE // The original HttpModule, to port into InvokeAsync:
            {{original}}
            #endif

            """;
    }

    private static string EndpointStub(string root, string ns, WebComponent handler)
    {
        var name = Stub(handler.Type, "Handler", "Endpoint");
        var original = File.ReadAllText(RepoPaths.ToAbsolute(root, handler.File!)).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return $$"""
            // Generated by offramp web scaffold from {{handler.File}}.
            using Microsoft.AspNetCore.Http;

            namespace {{ns}}.Endpoints
            {
                /// <summary>
                /// OFR4202: the {{handler.Type}} HttpHandler ({{handler.Path ?? handler.Name}}) as a minimal API
                /// endpoint. Port ProcessRequest into Handle, then map it in Program.cs; until then the
                /// proxy sends its path to the legacy application.
                /// </summary>
                public static class {{name}}
                {
                    public static IResult Handle(HttpContext context) => Results.StatusCode(StatusCodes.Status501NotImplemented);
                }
            }

            #if OFFRAMP_HTTPHANDLER // The original HttpHandler, to port into Handle:
            {{original}}
            #endif

            """;
    }

    private static List<string> NextSteps(WebScaffoldRequest request, string project, WebInventoryResult inventory, List<PortedControllerSource> controllers)
    {
        var steps = new List<string> { $"dotnet sln add {project}", $"dotnet run --project {project}" };
        var authorizing = controllers.Where(c => c.Authorizes).Select(c => c.Type.Name).Order(StringComparer.Ordinal).ToList();
        if (!request.Adapters && authorizing.Count > 0)
        {
            steps.Add($"Give the new application an authentication scheme for the [Authorize] actions of {string.Join(", ", authorizing)} (or scaffold with --adapters to share the legacy application's); until then they answer 500.");
        }

        if (request.Adapters)
        {
            steps.Add("In the legacy application, add Microsoft.AspNetCore.SystemWebAdapters.FrameworkServices and, in Application_Start: SystemWebAdapterConfiguration.AddSystemWebAdapters(this).AddJsonSessionSerializer().AddRemoteAppServer(options => options.ApiKey = <the shared key>).AddSessionServer().AddAuthenticationServer(); set the same key in RemoteApp:ApiKey.");
        }

        if (request.Proxy == "none")
        {
            steps.Add("Route the paths in ingress-paths.txt to the new application and everything else to the legacy one.");
        }

        if (inventory.WebForms.Count > 0)
        {
            steps.Add("Decide per Web Forms page (OFR4201): keep behind the proxy, rewrite, or convert.");
        }

        steps.Add("Port the actions listed as not ported (views need Razor views in the new project), then remove them from the legacy application.");
        return steps;
    }

    private static string Stub(string type, string suffix, string replacement)
    {
        var name = type.Split('.')[^1];
        return (name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name) + replacement;
    }

    private static bool CentralPackages(string root, string directory)
    {
        var rootPath = Path.GetFullPath(root);
        for (var current = RepoPaths.ToAbsolute(root, directory); current is not null && current.StartsWith(rootPath, StringComparison.Ordinal); current = Path.GetDirectoryName(current))
        {
            var props = Path.Combine(current, "Directory.Packages.props");
            if (File.Exists(props))
            {
                return XDocument.Load(props).Descendants("ManagePackageVersionsCentrally").Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            }
        }

        return false;
    }
}
