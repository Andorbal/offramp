using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Analysis.Audits;
using Offramp.Analysis.Compilations;
using Offramp.Core.Diagnostics;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Scaffolding.Remote;
using Offramp.Workspace.Targets;

namespace Offramp.Scaffolding.Service;

/// <summary>
/// <c>offramp service</c> (docs/spec/commands/scaffold.md#service): finds the project's
/// Windows services and writes a worker project that runs them with the generic host, in a
/// Linux container, as a Windows service, or both. The service project is not edited; what it
/// no longer needs is reported.
/// </summary>
public static partial class ServiceGenerator
{
    public static async Task<ServicePlan?> PlanAsync(ServiceRequest request, CSharpCompilation compilation, CancellationToken cancellationToken)
    {
        var project = request.Project;
        var location = new DiagnosticLocation(project.Id);
        var serviceBases = ServiceDetector.ServiceBaseClasses(compilation);
        var topshelf = ServiceDetector.Topshelf(compilation);
        if (serviceBases.Count == 0 && topshelf.Count == 0)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4107, $"{project.Id} has no ServiceBase subclass and no Topshelf HostFactory configuration.", location);
            return null;
        }

        var root = request.RepositoryRoot;
        var projectDirectory = RepoPaths.Normalize(Path.GetDirectoryName(project.Id) ?? "");
        var parent = projectDirectory.Contains('/', StringComparison.Ordinal) ? projectDirectory[..projectDirectory.LastIndexOf('/')] + "/" : "";
        var directory = request.OutputDirectory is { } output ? RepoPaths.Normalize(output).TrimEnd('/') : parent + project.Name + ".Worker";
        var ns = project.RootNamespace ?? project.Name;
        var heartbeat = request.Health ? ns + ".WorkerHeartbeat" : null;
        var (installers, account, installerFiles) = ServiceDetector.Installers(compilation);

        var services = new List<DetectedService>();
        var workers = new List<WorkerSource>();
        foreach (var type in serviceBases)
        {
            var settings = ServiceDetector.BaseSettings(compilation, type);
            var serviceName = settings.GetValueOrDefault("ServiceName") as string ?? type.Name;
            var installer = installers.GetValueOrDefault(serviceName) ?? new InstallerSettings();
            var worker = WorkerWriter.FromServiceBase(compilation, type, serviceName, heartbeat);
            workers.Add(worker);
            services.Add(Describe(compilation, "servicebase", type, serviceName, installer, account, worker));
            if (settings.GetValueOrDefault("CanHandleSessionChangeEvent") is true || settings.GetValueOrDefault("CanHandlePowerEvent") is true)
            {
                if (!worker.Notes.Any(n => n.Descriptor == DiagnosticCatalog.OFR4105))
                {
                    request.Diagnostics.Report(DiagnosticCatalog.OFR4105, $"{AuditEngine.Name(type)} asks for session change or power events, which the host does not deliver.", Location(root, project.Id, type.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken)));
                }
            }
        }

        foreach (var configuration in topshelf)
        {
            var serviceName = configuration.Settings.ServiceName ?? configuration.Type.Name;
            var worker = WorkerWriter.FromTopshelf(compilation, configuration, serviceName, heartbeat);
            workers.Add(worker);
            services.Add(Describe(compilation, "topshelf", configuration.Type, serviceName, configuration.Settings, configuration.Account, worker));
        }

        foreach (var worker in workers)
        {
            foreach (var note in worker.Notes)
            {
                request.Diagnostics.Report(note.Descriptor, note.Message, note.At is null ? location : Location(root, project.Id, note.At));
            }
        }

        foreach (var service in services.Where(s => s.DependsOn.Count > 0))
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4104,
                $"{service.ServiceName} depends on {string.Join(", ", service.DependsOn)}; a container or systemd unit does not start those first, so the worker must wait for or retry them.", location,
                [KeyValuePair.Create<string, JsonNode?>("dependsOn", new JsonArray([.. service.DependsOn.Select(d => (JsonNode?)d)]))]);
        }

        if (services.Count > 1)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4103,
                $"{project.Id} runs {services.Count} services ({string.Join(", ", services.Select(s => s.ServiceName))}); the worker project hosts one worker for each in one process.", location);
        }

        var entryTrees = EntryPoints(compilation);
        var removals = Removals(root, project, compilation, installerFiles, topshelf.Count > 0);
        var result = new ServiceResult { Project = project.Id, Host = request.Host, Services = services, Removals = removals };
        var target = RepoPaths.ToAbsolute(root, directory);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4108, $"{directory} already exists; nothing was generated.", new DiagnosticLocation(project.Id, directory));
            return new ServicePlan(result, null);
        }

        // The files the workers need from the service project, as links.
        var excluded = serviceBases.SelectMany(t => t.DeclaringSyntaxReferences.Select(r => r.SyntaxTree))
            .Concat(entryTrees)
            .Concat(installerFiles.Select(f => compilation.SyntaxTrees.First(t => t.FilePath == f)))
            .ToHashSet();
        var roots = workers.SelectMany(w => w.Uses).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
        var links = roots.Length == 0 ? [] : HostFramework.Closure(compilation, roots).Where(t => !excluded.Contains(t)).ToList();
        var usesConfiguration = ServiceDetector.UsesConfigurationManager(compilation, workers.SelectMany(w => w.Kept).Concat(links.Select(t => t.GetRoot())));
        var appConfig = usesConfiguration ? AppConfig(root, projectDirectory) : null;

        var packages = Packages(request, project, usesConfiguration, links);
        var tfm = $"net{request.TargetMajor.ToString(CultureInfo.InvariantCulture)}.0";
        var first = services[0];
        var layout = new WorkerLayout
        {
            Directory = directory,
            Name = project.Name + ".Worker",
            Namespace = ns,
            TargetFramework = tfm,
            Host = request.Host,
            Health = request.Health,
            Logging = request.Logging,
            ServiceName = services.Count == 1 ? first.ServiceName : project.Name,
            Description = services.Count == 1 ? first.Description : string.Join("; ", services.Select(s => s.Description ?? s.ServiceName)),
            Account = services.Select(s => s.Account).FirstOrDefault(a => a is not null),
            StartType = services.Select(s => s.StartType).FirstOrDefault(s => s is not null),
            DependsOn = [.. services.SelectMany(s => s.DependsOn).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            Workers = [.. workers.Select(w => w.FullName)],
            Packages = packages,
            Links = [.. links.Select(t => File(root, t))],
            ProjectDirectory = projectDirectory,
            AppConfig = appConfig,
            CentralPackages = CentralPackages(root, directory),
        };

        var files = new List<(string Path, string Text)>();
        files.AddRange(ServiceTemplates.Files(layout, request.Dockerfile, request.Kubernetes));
        files.AddRange(workers.Select(w => ($"{directory}/{w.Name}.cs", w.Text)));
        await TrialAsync(request, compilation, layout, files, links, cancellationToken).ConfigureAwait(false);

        var changeSet = new ChangeSet();
        foreach (var (path, text) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            changeSet.Create(path, text);
        }

        result = result with
        {
            Worker = new WorkerProject(layout.Project, layout.Sdk, tfm),
            LinkedSources = layout.Links,
            Files = [.. files.Select(f => f.Path).Order(StringComparer.Ordinal)],
            NextSteps = NextSteps(request, layout, services, appConfig is not null),
            Preview = changeSet.Preview(),
        };
        return new ServicePlan(result, changeSet);
    }

    private static DetectedService Describe(Compilation compilation, string kind, INamedTypeSymbol type, string serviceName, InstallerSettings installer, string? account, WorkerSource worker) => new()
    {
        Kind = kind,
        Type = AuditEngine.Name(type),
        ServiceName = serviceName,
        DisplayName = installer.DisplayName,
        Description = installer.Description,
        Account = account,
        StartType = installer.StartType,
        DependsOn = installer.DependsOn,
        Lifecycle = worker.Lifecycle,
        Timers = worker.Timers,
        Logging = ServiceDetector.Logging(compilation, Scope(type, worker)),
        Configuration = ServiceDetector.ConfigurationKeys(compilation, Scope(type, worker)),
        Worker = worker.FullName,
    };

    private static IEnumerable<SyntaxNode> Scope(INamedTypeSymbol type, WorkerSource worker) =>
        type.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).Concat(worker.Kept).Distinct();

    /// <summary>Files with the entry point (<c>Main</c>, or top-level statements): the worker has its own.</summary>
    private static List<SyntaxTree> EntryPoints(Compilation compilation) =>
        [.. AuditEngine.Sources(compilation).Where(t => t.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "Main" && m.Modifiers.Any(SyntaxKind.StaticKeyword))
            || t.GetCompilationUnitRoot().Members.OfType<GlobalStatementSyntax>().Any())];

    private static List<ServiceRemoval> Removals(string root, Core.Model.ProjectInfo project, Compilation compilation, List<string> installerFiles, bool topshelf)
    {
        var removals = new List<ServiceRemoval>();
        foreach (var file in installerFiles.Select(f => Path.IsPathRooted(f) ? RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(f)) : RepoPaths.Normalize(f)).Distinct(StringComparer.Ordinal))
        {
            removals.Add(new ServiceRemoval(file, "service installer: sc.exe (install.ps1) or the container platform installs the worker"));
            var designer = file[..^".cs".Length] + ".Designer.cs";
            if (System.IO.File.Exists(RepoPaths.ToAbsolute(root, designer)))
            {
                removals.Add(new ServiceRemoval(designer, "designer part of the service installer"));
            }
        }

        foreach (var reference in project.AssemblyReferences.Where(r => r.Name is "System.ServiceProcess" or "System.Configuration.Install").OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            removals.Add(new ServiceRemoval(project.Id, $"Reference {reference.Name}: the worker uses Microsoft.Extensions.Hosting"));
        }

        if (topshelf)
        {
            foreach (var package in project.PackageReferences.Where(p => p.Id.StartsWith("Topshelf", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                removals.Add(new ServiceRemoval(project.Id, $"PackageReference {package.Id}: the worker uses Microsoft.Extensions.Hosting"));
            }
        }

        var projectDirectory = RepoPaths.Normalize(Path.GetDirectoryName(project.Id) ?? "");
        foreach (var manifest in Directory.EnumerateFiles(RepoPaths.ToAbsolute(root, projectDirectory.Length == 0 ? "." : projectDirectory), "*.manifest", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            if (RequestedExecutionLevel().IsMatch(System.IO.File.ReadAllText(manifest)))
            {
                removals.Add(new ServiceRemoval(RepoPaths.ToRepositoryRelative(root, manifest), "requestedExecutionLevel: a worker does not elevate"));
            }
        }

        return [.. removals.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Reason, StringComparer.Ordinal)];
    }

    private static List<(string Id, string Version)> Packages(ServiceRequest request, Core.Model.ProjectInfo project, bool configuration, List<SyntaxTree> links)
    {
        var packages = new List<(string Id, string Version)>();
        if (!request.Health)
        {
            packages.Add(("Microsoft.Extensions.Hosting", ScaffoldPackages.Version("Microsoft.Extensions.Hosting")));
        }

        if (request.Host is "windows" or "both")
        {
            packages.Add(("Microsoft.Extensions.Hosting.WindowsServices", ScaffoldPackages.Version("Microsoft.Extensions.Hosting.WindowsServices")));
        }

        if (request.Host == "both")
        {
            packages.Add(("Microsoft.Extensions.Hosting.Systemd", ScaffoldPackages.Version("Microsoft.Extensions.Hosting.Systemd")));
        }

        if (configuration)
        {
            packages.Add(("System.Configuration.ConfigurationManager", ScaffoldPackages.Version("System.Configuration.ConfigurationManager")));
        }

        // The linked code's own packages, except the service frameworks the worker replaces.
        if (links.Count > 0)
        {
            var target = CompilationLoader.PreferredTarget(project);
            var direct = target is not null && project.Resolved.TryGetValue(target, out var resolved)
                ? resolved.Packages.Where(p => p.Direct).Select(p => (p.Id, p.Version))
                : project.PackageReferences.Where(p => p.Version is not null).Select(p => (p.Id, p.Version!));
            packages.AddRange(direct.Where(p => !p.Id.StartsWith("Topshelf", StringComparison.OrdinalIgnoreCase) && packages.All(k => !k.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase))));
        }

        return packages;
    }

    /// <summary>Compiles the workers and the linked files for the target; errors are OFR4106 (the project is still written).</summary>
    private static async Task TrialAsync(ServiceRequest request, Compilation recorded, WorkerLayout layout, List<(string Path, string Text)> files, List<SyntaxTree> links, CancellationToken cancellationToken)
    {
        if (request.References is null)
        {
            return;
        }

        var references = await request.References.ResolveAsync(new TargetReferenceRequest
        {
            TargetFramework = layout.TargetFramework,
            Frameworks = request.Health ? ["Microsoft.AspNetCore.App"] : [],
            Packages = layout.Packages,
        }, cancellationToken).ConfigureAwait(false);
        if (references.Error is { } error)
        {
            request.Diagnostics.Report(DiagnosticCatalog.OFR4106, $"The worker could not be compiled in memory: its {layout.TargetFramework} references did not resolve ({error}).", new DiagnosticLocation(request.Project.Id));
            return;
        }

        var options = new CSharpParseOptions(LanguageVersion.Default, preprocessorSymbols: TargetCompilation.TargetSymbols(request.TargetMajor, windows: false));
        var trees = files.Where(f => f.Path.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Text, options, f.Path, cancellationToken: cancellationToken))
            .Concat(links.Select(t => CSharpSyntaxTree.ParseText(t.GetText(cancellationToken), options, t.FilePath, cancellationToken)))
            .Append(CSharpSyntaxTree.ParseText(GlobalUsingsForSdk(layout), options, "sdk-global-usings.cs", cancellationToken: cancellationToken))
            .ToList();
        var compilation = CSharpCompilation.Create(layout.Name, trees, references.Paths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Disable));
        var errors = compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start).ToList();
        if (errors.Count > 0)
        {
            var first = string.Join("; ", errors.Take(3).Select(d => $"{d.Id} {Path.GetFileName(d.Location.SourceTree?.FilePath)}: {d.GetMessage(CultureInfo.InvariantCulture)}"));
            request.Diagnostics.Report(DiagnosticCatalog.OFR4106, $"{layout.Project} has {errors.Count} compile error{(errors.Count == 1 ? "" : "s")} for {layout.TargetFramework}: {first}",
                new DiagnosticLocation(request.Project.Id, layout.Project), [KeyValuePair.Create<string, JsonNode?>("errors", errors.Count)]);
        }
    }

    /// <summary>ImplicitUsings is off in the generated project, so nothing is added.</summary>
    private static string GlobalUsingsForSdk(WorkerLayout layout) => "// " + layout.Sdk + "\n";

    private static List<string> NextSteps(ServiceRequest request, WorkerLayout layout, List<DetectedService> services, bool appConfig)
    {
        var steps = new List<string> { $"dotnet sln add {layout.Project}", $"dotnet run --project {layout.Project}" };
        if (request.Dockerfile)
        {
            steps.Add($"docker build -f {layout.Directory}/Dockerfile -t {layout.Image} .");
        }

        if (request.Kubernetes)
        {
            steps.Add($"kubectl apply -f {layout.Directory}/kubernetes.yaml");
        }

        if (request.Host is "windows" or "both")
        {
            steps.Add($"dotnet publish {layout.Project} -c Release -r win-x64, then run install.ps1 from the output as an administrator");
        }

        if (request.Host == "both")
        {
            steps.Add($"On Linux: install {layout.Image}.service with systemd (see the comment at its top)");
        }

        if (appConfig || services.Any(s => s.Configuration.Count > 0))
        {
            steps.Add("offramp config convert, to move app.config settings to appsettings.json");
        }

        steps.Add("Review the regions marked OFR4102 in the workers; then remove what the removals list names from the service project");
        return steps;
    }

    private static DiagnosticLocation Location(string root, string project, SyntaxNode node)
    {
        var (file, line, _) = AuditEngine.Position(root, node.GetLocation());
        return new DiagnosticLocation(project, file, line);
    }

    private static string? AppConfig(string root, string projectDirectory)
    {
        var path = (projectDirectory.Length == 0 ? "" : projectDirectory + "/") + "App.config";
        return System.IO.File.Exists(RepoPaths.ToAbsolute(root, path)) ? path : null;
    }

    private static string File(string root, SyntaxTree tree) =>
        Path.IsPathRooted(tree.FilePath) ? RepoPaths.ToRepositoryRelative(root, Path.GetFullPath(tree.FilePath)) : RepoPaths.Normalize(tree.FilePath);

    private static bool CentralPackages(string root, string directory)
    {
        for (var current = RepoPaths.ToAbsolute(root, directory); current is not null && current.StartsWith(root, StringComparison.Ordinal); current = Path.GetDirectoryName(current))
        {
            var props = Path.Combine(current, "Directory.Packages.props");
            if (System.IO.File.Exists(props))
            {
                return CentralOn().IsMatch(System.IO.File.ReadAllText(props));
            }
        }

        return false;
    }

    [GeneratedRegex(@"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase)]
    private static partial Regex CentralOn();

    [GeneratedRegex(@"requestedExecutionLevel", RegexOptions.IgnoreCase)]
    private static partial Regex RequestedExecutionLevel();
}
