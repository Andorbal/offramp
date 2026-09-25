# Scaffolding: `service`, `web inventory`, `web scaffold`, `csproj modernize`, `config convert`

Generators write new files and, where stated, edit existing ones. Every
generator supports `--dry-run` (default) with a full preview and `--apply`.
Templates are embedded resources; users can override any template by placing
a file with the same name under `.offramp/templates/`.

## `service`

Windows Service → hosted console app that runs in a Linux container (default),
as a Windows Service, or both.

```
offramp service --project P [--host linux|windows|both] [--out DIR|--in-place]
                [--dockerfile] [--k8s] [--health] [--logging json-console|simple] [--apply]
```

Detection (from the workspace model and the compilation):
- `ServiceBase` subclasses: `OnStart`, `OnStop`, `OnPause`, `OnContinue`,
  `OnShutdown`, `OnCustomCommand`; `ServiceName`; `ServiceInstaller`/
  `ServiceProcessInstaller` (account, start type, dependencies, description).
- Topshelf: `HostFactory.Run` configuration lambda (service class,
  `WhenStarted`/`WhenStopped`, `RunAsLocalSystem`, `StartAutomatically`,
  `SetServiceName`/`Description`, recovery options).
- Timers driving work: `System.Timers.Timer`, `System.Threading.Timer`,
  `Task.Run` loops with `Thread.Sleep`.
- `EventLog` writes, `Trace`/`Debug`, log4net/NLog/Serilog usage.
- `ConfigurationManager` reads (reported, see `config convert`).

Generated (for `--host linux`):
- `Program.cs`: `Host.CreateApplicationBuilder`, `AddHostedService<XWorker>()`,
  `UseConsoleLifetime` (implicit), `HostOptions.ShutdownTimeout` from config
  (default 25 s, with a comment about `terminationGracePeriodSeconds`),
  JSON console logging, configuration from `appsettings.json` + environment.
- `XWorker.cs : BackgroundService`: `ExecuteAsync` built from `OnStart` body
  (setup) + the timer callback body converted to a `PeriodicTimer` loop that
  honors the stopping token; `OnStop` body into `StopAsync` override; `OnPause`/
  `OnContinue` mapped to a `Paused` flag with `OFR4101` (semantics differ).
  Where the original body cannot be lifted safely (uses `this` of the
  `ServiceBase`, calls `RequestAdditionalTime`), the generator inserts the
  original code inside a clearly marked region with `OFR4102` per site.
- `--host windows|both`: `builder.Services.AddWindowsService(o => o.ServiceName = ...)`
  and `AddSystemd()`; `install.ps1` using `sc.exe`, `uninstall.ps1`, and a
  systemd unit file for `--host both`.
- Health (`--health`): a minimal HTTP endpoint on a configurable port
  returning the worker's last heartbeat.
- Dockerfile: multi-stage, `mcr.microsoft.com/dotnet/runtime:<target>` (or
  `aspnet` when health is enabled), non-root user, `DOTNET_gcServer` left to
  config.
- `--k8s`: Deployment with resource requests, liveness/readiness on the
  health endpoint, `terminationGracePeriodSeconds` matching the shutdown
  timeout, and a ConfigMap stub.
- Removal list: `ProjectInstaller.*`, `System.ServiceProcess` reference,
  Topshelf package, `app.manifest` `requestedExecutionLevel` if present
  (reported; removed with `--in-place --apply`).

Diagnostics: `OFR4101` pause/continue semantics, `OFR4102` code left in
compatibility region, `OFR4103` multiple services in one executable (one
worker each), `OFR4104` service depends on other Windows services
(`ServicesDependedOn`), `OFR4105` uses `SessionChange`/`PowerEvent`.

## `web inventory`

Read-only inventory of an ASP.NET (System.Web) application.

```
offramp web inventory --project P [--format json|markdown]
```

Collects: controllers (MVC/Web API) with actions and routes (attribute and
convention, from `RouteConfig`/`WebApiConfig` parsing), filters (global and
per-controller), `HttpModule`s and `HttpHandler`s (from `web.config` and
code), `Global.asax` events, `Application_*` handlers, bundling, areas,
WebForms pages and user controls (`.aspx`, `.ascx`, `.master`), session usage,
Forms Authentication, `MachineKey`, output caching, `web.config` sections
that matter (`system.web`, `system.webServer`, `authentication`,
`sessionState`, `httpModules`, `handlers`, `customErrors`), and the
`System.Web.*` API surface used per file (feeding `audit api`).

## `web scaffold`

Strangler-fig setup: a new ASP.NET Core app that hosts migrated routes and
proxies the rest to the legacy app.

```
offramp web scaffold --project P --new src/Foo.Web.Core [--proxy yarp|none] [--adapters] [--apply]
```

- Generates an ASP.NET Core project with: controllers ported for actions
  whose code has no `OFR3001`-level findings (the rest listed with reasons),
  route table preserved, `System.Web.Adapters` (`--adapters`) configured for
  session and auth sharing with the legacy app, and YARP (`--proxy yarp`)
  configured with a catch-all route to the legacy app's URL. With `--proxy
  none` (Kubernetes ingress plays the proxy), it emits the ingress path list.
- WebForms pages are inventoried, never ported (`OFR4201`, with the options:
  keep behind the proxy, rewrite as Razor Pages/Blazor, or a third-party
  converter).
- `HttpModule`s become middleware stubs with the original code in a marked
  region; `HttpHandler`s become minimal API endpoints (`OFR4202` per one).

## `csproj modernize`

Legacy csproj → SDK-style, plus modern hygiene for already-SDK projects.

```
offramp csproj modernize --project P|--all [--tfm net48;net10.0] [--cpm] [--apply]
```

- Legacy projects: uses `try-convert` when available (`dotnet tool`), then
  post-passes: remove `AssemblyInfo` attributes that the SDK generates (keep
  `InternalsVisibleTo`, custom ones), convert `packages.config` to
  `PackageReference` (respecting pins and CPM), replace explicit `Compile`
  lists with globs when the file set equals the glob result (else keep them
  and report `OFR4301`), preserve `Link` items, `DependentUpon`,
  `EmbeddedResource` names, `PreBuildEvent`/`PostBuildEvent` (reported
  `OFR4302` for review), `ProjectTypeGuids` → SDK choice.
- All projects: add the requested `TargetFrameworks`, move shared properties to
  `Directory.Build.props` when identical across ≥ N projects (`--hoist`),
  add the compile-only conditional block for non-Windows when `doctor` found
  Windows-only steps, set `LangVersion`/`Nullable` only when asked
  (`--nullable enable`).
- Before/after verification: the set of compiled files and references must be
  identical (compared from binlogs of both builds); any difference is
  `OFR4303` and blocks `--apply` unless `--accept-diff`.

## `config convert`

`app.config`/`web.config` → `appsettings.json` + options classes + a
compatibility shim.

```
offramp config convert --project P [--out DIR] [--sections appSettings,connectionStrings,custom] [--shim] [--apply]
```

- `appSettings` → flat JSON keys, and an `AppSettingsOptions` record;
  `connectionStrings` → `ConnectionStrings` section; custom
  `ConfigurationSection` classes → nested JSON with a generated options class
  per section and a mapping report `OFR4401` for properties whose types are
  not representable (e.g. `TimeSpan` strings are fine; custom converters are
  flagged).
- `system.serviceModel` → `OFR4402` (CoreWCF/client config notes),
  `runtime/assemblyBinding` → dropped with a note, `system.web` →
  `OFR4403` (belongs to `web scaffold`), `system.diagnostics` listeners →
  logging config notes.
- `--shim`: a `ConfigurationManagerShim` that reads `IConfiguration` and
  exposes `AppSettings`/`ConnectionStrings` with the old API shape, plus a
  Roslyn codemod (`codemod config-manager`) that redirects call sites to it.
- Transforms (`web.Release.config`) → `appsettings.{Environment}.json` when
  the transform is expressible as overrides (`OFR4404` otherwise).
