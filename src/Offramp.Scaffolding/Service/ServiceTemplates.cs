using System.Globalization;
using System.Text;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Service;

/// <summary>What the templates need to know about the worker project.</summary>
internal sealed record WorkerLayout
{
    /// <summary>The worker project's directory, repository-relative.</summary>
    public required string Directory { get; init; }

    public required string Name { get; init; }

    /// <summary>The namespace of the generated helpers (the service project's root namespace).</summary>
    public required string Namespace { get; init; }

    public required string TargetFramework { get; init; }

    public required string Host { get; init; }

    public required bool Health { get; init; }

    public required string Logging { get; init; }

    /// <summary>The name the process registers as a Windows service or systemd unit.</summary>
    public required string ServiceName { get; init; }

    public string? Description { get; init; }

    public string? Account { get; init; }

    public string? StartType { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public required IReadOnlyList<string> Workers { get; init; }

    public required IReadOnlyList<(string Id, string Version)> Packages { get; init; }

    /// <summary>Repository-relative source files compiled as links.</summary>
    public required IReadOnlyList<string> Links { get; init; }

    /// <summary>The service project's directory, repository-relative (links go under Service\ relative to it).</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>App.config to copy next to the worker as its .dll.config (ConfigurationManager reads it).</summary>
    public string? AppConfig { get; init; }

    public bool CentralPackages { get; init; }

    public string Project => $"{Directory}/{Name}.csproj";

    public string Sdk => Health ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk.Worker";

    public string Image => CSharpText.Kebab(Name);

    public string Heartbeat => $"{Namespace}.WorkerHeartbeat";

    /// <summary>Kubernetes's default termination grace period; the host's shutdown timeout stays below it.</summary>
    public const int ShutdownSeconds = 25;
}

/// <summary>The worker project's files other than the workers themselves.</summary>
internal static class ServiceTemplates
{
    public static IEnumerable<(string Path, string Text)> Files(WorkerLayout layout, bool dockerfile, bool kubernetes)
    {
        yield return (layout.Project, Project(layout));
        yield return ($"{layout.Directory}/Program.cs", Program(layout));
        yield return ($"{layout.Directory}/appsettings.json", Settings(layout));
        if (layout.Health)
        {
            yield return ($"{layout.Directory}/WorkerHeartbeat.cs", Heartbeat(layout));
        }

        if (dockerfile)
        {
            yield return ($"{layout.Directory}/Dockerfile", Dockerfile(layout));
            yield return ($"{layout.Directory}/Dockerfile.dockerignore", "**/bin/\n**/obj/\n**/.vs/\n.git/\n.offramp/\n");
        }

        if (kubernetes)
        {
            yield return ($"{layout.Directory}/kubernetes.yaml", Kubernetes(layout));
        }

        if (layout.Host is "windows" or "both")
        {
            yield return ($"{layout.Directory}/install.ps1", Install(layout));
            yield return ($"{layout.Directory}/uninstall.ps1", Uninstall(layout));
        }

        if (layout.Host == "both")
        {
            yield return ($"{layout.Directory}/{layout.Image}.service", Systemd(layout));
        }
    }

    private static string Project(WorkerLayout layout)
    {
        var packages = new StringBuilder();
        foreach (var (id, version) in layout.Packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            packages.Append(CultureInfo.InvariantCulture, $"    <PackageReference Include=\"{id}\" Version=\"{version}\" />\n");
        }

        var links = new StringBuilder();
        foreach (var file in layout.Links)
        {
            var inProject = layout.ProjectDirectory.Length > 0 && file.StartsWith(layout.ProjectDirectory + "/", StringComparison.Ordinal)
                ? file[(layout.ProjectDirectory.Length + 1)..]
                : Path.GetFileName(file);
            links.Append(CultureInfo.InvariantCulture, $"    <Compile Include=\"{Relative(layout.Directory, file)}\" Link=\"Service\\{inProject.Replace('/', '\\')}\" />\n");
        }

        var central = layout.CentralPackages
            ? "    <!-- Pins its own package versions; `offramp deps consolidate --cpm` can centralize them. -->\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n"
            : "";
        var appConfig = layout.AppConfig is { } config
            ? $"    <!-- ConfigurationManager reads {layout.Name}.dll.config; `offramp config convert` moves the settings to appsettings.json. -->\n    <AppConfig>{Relative(layout.Directory, config)}</AppConfig>\n"
            : "";
        var linkGroup = links.Length == 0 ? "" : $"""

              <!-- The code the service uses, compiled from {layout.ProjectDirectory} until it moves here. -->
              <ItemGroup>
            {links}  </ItemGroup>

            """;
        return $$"""
            <Project Sdk="{{layout.Sdk}}">
              <!-- Generated by offramp service: the service as a hosted worker ({{layout.Host}}). -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{{layout.TargetFramework}}</TargetFramework>
                <Nullable>disable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <RootNamespace>{{layout.Namespace}}</RootNamespace>
            {{central}}{{appConfig}}  </PropertyGroup>

              <ItemGroup>
            {{packages}}  </ItemGroup>
            {{linkGroup}}</Project>

            """;
    }

    private static string Program(WorkerLayout layout)
    {
        var text = new StringBuilder();
        text.Append("// Generated by offramp service: the host that runs the workers.\n");
        text.Append("using System;\nusing Microsoft.Extensions.Configuration;\nusing Microsoft.Extensions.DependencyInjection;\nusing Microsoft.Extensions.Hosting;\nusing Microsoft.Extensions.Logging;\n");
        if (layout.Health)
        {
            text.Append("using Microsoft.AspNetCore.Builder;\nusing Microsoft.AspNetCore.Hosting;\nusing Microsoft.AspNetCore.Http;\n");
        }

        text.Append('\n').Append(layout.Health ? "var builder = WebApplication.CreateBuilder(args);\n" : "var builder = Host.CreateApplicationBuilder(args);\n");
        text.Append("builder.Logging.ClearProviders();\n");
        text.Append(layout.Logging == "simple" ? "builder.Logging.AddSimpleConsole();\n" : "builder.Logging.AddJsonConsole();\n");
        text.Append(CultureInfo.InvariantCulture, $"""

            // Containers and systemd stop the process with SIGTERM and kill it after a grace period
            // (Kubernetes: terminationGracePeriodSeconds, 30 by default); keep the shutdown timeout below it.
            builder.Services.Configure<HostOptions>(options =>
                options.ShutdownTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("HostOptions:ShutdownTimeoutSeconds", {WorkerLayout.ShutdownSeconds})));

            """);
        if (layout.Host is "windows" or "both")
        {
            text.Append(CultureInfo.InvariantCulture, $"builder.Services.AddWindowsService(options => options.ServiceName = {Literal(layout.ServiceName)});\n");
        }

        if (layout.Host == "both")
        {
            text.Append("builder.Services.AddSystemd();\n");
        }

        if (layout.Health)
        {
            var names = string.Join(", ", layout.Workers.Select(w => Literal(w[(w.LastIndexOf('.') + 1)..])));
            text.Append(CultureInfo.InvariantCulture, $"builder.Services.AddSingleton(new {layout.Heartbeat}(TimeSpan.FromSeconds(builder.Configuration.GetValue(\"Health:StaleAfterSeconds\", 60)), {names}));\n");
        }

        foreach (var worker in layout.Workers)
        {
            text.Append(CultureInfo.InvariantCulture, $"builder.Services.AddHostedService<{worker}>();\n");
        }

        if (layout.Health)
        {
            text.Append(CultureInfo.InvariantCulture, $$"""

                // The health endpoint: 200 while every worker has checked in recently, 503 otherwise.
                builder.WebHost.UseUrls("http://+:" + builder.Configuration.GetValue("Health:Port", 8080).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var app = builder.Build();
                app.MapGet("/health", ({{layout.Heartbeat}} heartbeat) =>
                {
                    var report = heartbeat.Report(DateTimeOffset.UtcNow);
                    return report.Healthy ? Results.Ok(report) : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
                });
                app.Run();

                """);
        }
        else
        {
            text.Append("\nbuilder.Build().Run();\n");
        }

        return text.ToString();
    }

    private static string Settings(WorkerLayout layout)
    {
        var health = layout.Health ? ",\n  \"Health\": {\n    \"Port\": 8080,\n    \"StaleAfterSeconds\": 60\n  }" : "";
        return $$"""
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.Hosting.Lifetime": "Information"
                }
              },
              "HostOptions": {
                "ShutdownTimeoutSeconds": {{WorkerLayout.ShutdownSeconds}}
              }{{health}}
            }

            """;
    }

    private static string Heartbeat(WorkerLayout layout) => $$"""
        // Generated by offramp service: when each worker last checked in, for the health endpoint.
        namespace {{layout.Namespace}}
        {
            using System;
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System.Linq;

            /// <summary>Workers call <see cref="Beat"/> as they work; the health endpoint asks whether all of them did recently.</summary>
            public sealed class WorkerHeartbeat
            {
                private readonly ConcurrentDictionary<string, DateTimeOffset> _beats = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                private readonly TimeSpan _staleAfter;
                private readonly string[] _workers;

                public WorkerHeartbeat(TimeSpan staleAfter, params string[] workers)
                {
                    _staleAfter = staleAfter;
                    _workers = workers;
                }

                public void Beat(string worker)
                {
                    _beats[worker] = DateTimeOffset.UtcNow;
                }

                public HealthReport Report(DateTimeOffset now)
                {
                    var workers = _workers.ToDictionary(w => w, w => _beats.TryGetValue(w, out var last) ? last : (DateTimeOffset?)null, StringComparer.Ordinal);
                    var healthy = workers.Values.All(last => last.HasValue && now - last.Value <= _staleAfter);
                    return new HealthReport { Status = healthy ? "healthy" : "unhealthy", Healthy = healthy, Workers = workers };
                }
            }

            public sealed class HealthReport
            {
                public string Status { get; set; }

                public bool Healthy { get; set; }

                public IDictionary<string, DateTimeOffset?> Workers { get; set; }
            }
        }

        """;

    private static string Dockerfile(WorkerLayout layout)
    {
        var major = layout.TargetFramework["net".Length..].Split('.')[0];
        var runtime = layout.Health ? "aspnet" : "runtime";
        var health = layout.Health ? "# The health endpoint listens on Health__Port; the image's default HTTP port setting is cleared.\nENV ASPNETCORE_HTTP_PORTS=\"\" \\\n    Health__Port=8080\nEXPOSE 8080\n" : "";
        return $$"""
            # Generated by offramp service. Build from the repository root:
            #   docker build -f {{layout.Directory}}/Dockerfile -t {{layout.Image}} .
            FROM mcr.microsoft.com/dotnet/sdk:{{major}}.0 AS build
            WORKDIR /src
            COPY . .
            RUN dotnet publish {{layout.Project}} -c Release -o /app

            FROM mcr.microsoft.com/dotnet/{{runtime}}:{{major}}.0
            WORKDIR /app
            COPY --from=build /app .
            # The .NET images define a non-root user; server GC is left to configuration (DOTNET_gcServer).
            USER $APP_UID
            {{health}}ENTRYPOINT ["dotnet", "{{layout.Name}}.dll"]

            """;
    }

    private static string Kubernetes(WorkerLayout layout)
    {
        var probes = layout.Health
            ? """
                          ports:
                            - containerPort: 8080
                          livenessProbe:
                            httpGet:
                              path: /health
                              port: 8080
                            initialDelaySeconds: 15
                            periodSeconds: 20
                          readinessProbe:
                            httpGet:
                              path: /health
                              port: 8080
                            initialDelaySeconds: 5
                            periodSeconds: 10

              """
            : "";
        return $$"""
            # Generated by offramp service: {{layout.Name}} on Linux nodes.
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: {{layout.Image}}
            data:
              DOTNET_ENVIRONMENT: Production
              HostOptions__ShutdownTimeoutSeconds: "{{WorkerLayout.ShutdownSeconds}}"
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{layout.Image}}
              labels:
                app: {{layout.Image}}
            spec:
              replicas: 1
              selector:
                matchLabels:
                  app: {{layout.Image}}
              template:
                metadata:
                  labels:
                    app: {{layout.Image}}
                spec:
                  nodeSelector:
                    kubernetes.io/os: linux
                  # Longer than HostOptions:ShutdownTimeoutSeconds, so the workers finish stopping.
                  terminationGracePeriodSeconds: {{WorkerLayout.ShutdownSeconds + 5}}
                  containers:
                    - name: worker
                      image: {{layout.Image}}:latest
                      envFrom:
                        - configMapRef:
                            name: {{layout.Image}}
                      resources:
                        requests:
                          cpu: 100m
                          memory: 128Mi
                        limits:
                          memory: 512Mi
            {{probes}}
            """;
    }

    private static string Install(WorkerLayout layout)
    {
        var start = layout.StartType switch
        {
            "Manual" => "demand",
            "Disabled" => "disabled",
            "AutomaticDelayed" => "delayed-auto",
            _ => "auto",
        };
        var account = layout.Account switch
        {
            "LocalService" => " obj= \"NT AUTHORITY\\LocalService\"",
            "NetworkService" => " obj= \"NT AUTHORITY\\NetworkService\"",
            "User" => "",
            _ => " obj= LocalSystem",
        };
        var depend = layout.DependsOn.Count > 0 ? $" depend= {string.Join("/", layout.DependsOn)}" : "";
        var userNote = layout.Account == "User" ? "# The service ran as a user account: add obj= DOMAIN\\user password= ... to sc.exe create.\n" : "";
        var description = layout.Description is { } d ? $"sc.exe description {Quote(layout.ServiceName)} {Quote(d)} | Out-Null\n" : "";
        return $$"""
            # Generated by offramp service: installs {{layout.Name}} as the Windows service {{Quote(layout.ServiceName)}}.
            # Run from an elevated PowerShell next to the published output (dotnet publish -c Release -r win-x64).
            param(
                [string]$BinaryPath = (Join-Path $PSScriptRoot '{{layout.Name}}.exe')
            )

            $ErrorActionPreference = 'Stop'
            {{userNote}}sc.exe create {{Quote(layout.ServiceName)}} binPath= "`"$BinaryPath`"" start= {{start}}{{account}}{{depend}}
            if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }
            {{description}}sc.exe failure {{Quote(layout.ServiceName)}} reset= 86400 actions= restart/60000/restart/60000// | Out-Null
            sc.exe start {{Quote(layout.ServiceName)}}

            """;
    }

    private static string Uninstall(WorkerLayout layout) => $$"""
        # Generated by offramp service: removes the Windows service {{Quote(layout.ServiceName)}}.
        $ErrorActionPreference = 'Stop'
        sc.exe stop {{Quote(layout.ServiceName)}} | Out-Null
        sc.exe delete {{Quote(layout.ServiceName)}}
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }

        """;

    private static string Systemd(WorkerLayout layout) => $$"""
        # Generated by offramp service. Publish to /opt/{{layout.Image}} (dotnet publish -c Release -r linux-x64 -o /opt/{{layout.Image}}), then:
        #   sudo cp {{layout.Image}}.service /etc/systemd/system/ && sudo systemctl enable --now {{layout.Image}}
        [Unit]
        Description={{layout.Description ?? layout.ServiceName}}
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=notify
        WorkingDirectory=/opt/{{layout.Image}}
        ExecStart=/opt/{{layout.Image}}/{{layout.Name}}
        Restart=on-failure
        DynamicUser=yes
        TimeoutStopSec={{WorkerLayout.ShutdownSeconds + 5}}
        Environment=DOTNET_ENVIRONMENT=Production

        [Install]
        WantedBy=multi-user.target

        """;

    private static string Relative(string fromDirectory, string toFile) =>
        Path.GetRelativePath("/r/" + fromDirectory, "/r/" + toFile).Replace('/', '\\');

    private static string Literal(string text) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, quote: true);

    private static string Quote(string text) => "\"" + text.Replace("\"", "`\"", StringComparison.Ordinal) + "\"";
}
