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

(`--in-place` is deferred until `csproj modernize`; see ADR 0024.)

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

### Details (M10)

Decisions in `docs/decisions/0024-service-workers.md`.

- Output: a new project at `--out` (default `NAME.Worker` next to the service project),
  targeting `netN.0` from `offramp.yml`, `Microsoft.NET.Sdk.Worker` (or `.Web` with
  `--health`). The service project is not edited; `--in-place` is deferred until
  `csproj modernize`, and the removal list is reported (`removals`).
- Workers: one per service, named `NameWorker`. A `ServiceBase` class is lifted: its
  members keep their text, the lifecycle overrides become private methods called from
  `ExecuteAsync`/`StopAsync`, `EventLog.WriteEntry` becomes logging, and other uses of
  `ServiceBase` stay inside `#if OFFRAMP_SERVICEBASE` (OFR4102). A Topshelf service is
  wrapped: `ConstructUsing`, `WhenStarted`, and `WhenStopped` become its creation,
  start, and stop.
- Timers: a `System.Timers.Timer` used only through setup (`Interval`, `Elapsed +=`,
  `Start`, `Stop`, `Dispose`, `Enabled`, `AutoReset`) whose handler ignores its sender
  and arguments becomes a `PeriodicTimer` loop; a failing tick is logged and the loop
  goes on. Other timers stay. `services[].timers` says which converted.
- Code the service uses from its project is compiled as links (`linkedSources`). The
  worker and the links are compiled in memory for the target; errors are OFR4106 (the
  project is still written). `ConfigurationManager` keeps working through
  `<AppConfig>`; `services[].configuration` lists the keys for `config convert`.
- Health: `WorkerHeartbeat` and `/health` on `Health:Port` (8080), 200 while every
  worker beat within `Health:StaleAfterSeconds` (60), 503 otherwise.
- Hosts: `windows` adds `AddWindowsService`, `install.ps1`, `uninstall.ps1`; `both` also
  `AddSystemd` and `IMAGE.service`. `--dockerfile` writes `Dockerfile` and
  `Dockerfile.dockerignore` (build from the repository root); `--k8s` writes
  `kubernetes.yaml` (ConfigMap and Deployment; probes with `--health`).
- Also OFR4106 worker does not compile, OFR4107 no service in the project, OFR4108
  output directory exists (nothing generated). A dry run until `--apply`. Schema:
  `schemas/v1/service.json`.

## `web inventory`

Read-only inventory of an ASP.NET (System.Web) application.

```
offramp web inventory --project P [--format table|json|markdown]
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

### Details (M12)

Decisions in `docs/decisions/0026-web-csproj-config-extract.md`.

- Read from the recorded compilation with the semantic model, plus `Web.config` and the
  project's files: controllers are classes deriving from `System.Web.Mvc.Controller` or
  `System.Web.Http.ApiController`; actions are their public instance methods without
  `[NonAction]`. Verbs come from attributes, and for Web API also from the name's prefix.
  Attribute routes are combined with the controller's `[RoutePrefix]`; a controller's area
  comes from its `Areas/NAME/` folder. `areas` lists the `AreaRegistration` classes and the
  areas the routes name.
- Registrations (routes, filters, bundles) are read from the application's sources and
  those of the C# libraries it references, directly or through other libraries (not
  referenced web applications or test projects; ADR 0059). Convention routes
  are the calls of `MapRoute`, `MapHttpRoute`, an area registration's `context.MapRoute`,
  `IgnoreRoute`, `RouteCollection.Add` with a `Route`, and `MapODataServiceRoute` (kind
  `odata`), also through the codebase's own helpers: when a route's name, template,
  defaults, or area come from the parameters of the method (or local function) that
  registers it, each call of that method is a route, with the call's arguments in place of
  the parameters, up to five calls away; `helper` names the method called. Values are
  literals, constants, concatenations, locals assigned once, and static properties and
  fields that return a literal; a template computed at run time is `(computed)`, with the C#
  that computes it in `computed` (a computed name is `(computed)`). A route's area is its
  `AreaRegistration`'s, a `DataTokens["area"]` set on it, or its `area` default. Defaults
  are listed as `name = value`, `UrlParameter.Optional` and `RouteParameter.Optional` as
  `?`. Routes are in file and line order.
- `globalFilters` are the filters `GlobalFilters.Filters.Add` (and Web API's
  `Filters.Add`) create; `containerFilters` are Autofac's `As*FilterFor<TController>`
  registrations (MVC and Web API): the filter class, its kind (`action`, `result`,
  `exception`, `authorization`, `authentication`, with ` override`), and the controller
  (often a base class) and action it applies to. `bundles` are the virtual paths
  `BundleCollection.Add` adds, also of a bundle built in a local.
- Modules and handlers are the classes implementing `IHttpModule` and `IHttpHandler`
  (not pages, not the `HttpApplication`), joined with their `system.web` and
  `system.webServer` registrations; a registration whose class is not in the project is
  listed with `file: null`.
- A hosted project (`02-workspace-model.md#hosted-projects`) runs inside its host's
  application: `hostedBy` names the host, `url` is the host's IIS URL, and `OFR0204`
  (info) says so.
- `--format json` writes the result alone; `markdown` a document for a migration plan;
  `table` (the default) the terminal view. The envelope (`--json`) carries the same
  result. Schema: `schemas/v1/web-inventory.json`.

## `web scaffold`

Strangler-fig setup: a new ASP.NET Core app that hosts migrated routes and
proxies the rest to the legacy app.

```
offramp web scaffold --project P --new src/Foo.Web.Core [--proxy yarp|none] [--adapters] [--legacy-url URL] [--apply]
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

### Details (M12)

Decisions in `docs/decisions/0026-web-csproj-config-extract.md`.

- The new project (`Microsoft.NET.Sdk.Web`, `netN.0` from `offramp.yml`, named after
  the `--new` folder) is written only into a folder that does not exist or is empty
  (`OFR4204` otherwise, exit 2). Nothing in the legacy project is edited.
- **Porting.** An action is copied as text, keeping its formatting, when it does not
  render a view (`View`, `PartialView`) and uses no System.Web API beyond the ones with
  an ASP.NET Core counterpart of the same shape (`Ok`, `NotFound`, `Json`, `Content`,
  `Redirect*`, `Created`, `StatusCode`, `ModelState`, `ViewBag`, `ViewData`, `TempData`,
  `Url`, `EmptyResult`, `RedirectResult`, ...; ADR 0059). Mapped names are replaced:
  `IHttpActionResult` → `IActionResult`, `HttpNotFound()` → `NotFound()`,
  `HttpUnauthorizedResult` → `UnauthorizedResult`, `FormCollection` → `IFormCollection`,
  `InternalServerError()` → `StatusCode(500)`, `new HttpStatusCodeResult(x)` →
  `StatusCode((int)(x))`, `Json(x, JsonRequestBehavior.*)` → `Json(x)` (MVC) and
  `Json(x)` → `new JsonResult(x)` (Web API); `[RoutePrefix]` → `[Route]`,
  `[OutputCache]` → `[ResponseCache]`, `[FromUri]` → `[FromQuery]`; attributes with no
  counterpart are left out and listed in `notes`. Web API controllers become
  `ControllerBase` with `[ApiController]`; their verbs, which ASP.NET Core does not infer
  from names, are made explicit (`POST` when the name has no verb prefix), and a
  conventionally routed one gets `[Route("api/[controller]")]` and `{id}` templates.
- **The compiler decides.** The project (with the legacy files the ported code needs,
  compiled as links) is compiled in memory against `Microsoft.AspNetCore.App` and the
  generated packages. An action whose code has an error stays with the legacy
  application, with the error as its reason, and the project is compiled again (up to
  eight rounds). An error outside the actions is `OFR4203` (the project is still
  written).
- **Routes.** MVC convention routes become `MapControllerRoute` (defaults inline as
  `{name=value}` and `{name?}`), an area's only when one of its controllers is ported;
  attribute routes come with the controllers (`MapControllers`). A route whose template is
  computed at run time cannot be written as a pattern: it is left out, and the proxy keeps
  sending its requests to the legacy application (`OFR4205`, with the code that computes
  it).
- **Proxy.** `yarp` (default): `AddReverseProxy` from `appsettings.json`, with a
  catch-all route of the lowest priority (`Order` = `int.MaxValue`) to `--legacy-url`
  (default: the project's IIS URL), so anything the new application does not map goes to
  the legacy one. `none`: `ingress-paths.txt` and `ingressPaths` list the route templates
  the new application serves.
- **Adapters** (`--adapters`): `Microsoft.AspNetCore.SystemWebAdapters.CoreServices` with
  the remote app client, session client, and remote authentication as the default
  scheme (`RemoteApp:Url`, `RemoteApp:ApiKey`). The legacy side's setup is a next step.
  Without adapters, ported `[Authorize]` actions need an authentication scheme; a comment
  in `Program.cs` and a next step say so.
- **Modules and handlers.** Each module is a middleware stub registered with
  `UseMiddleware` and the original class under `#if OFFRAMP_HTTPMODULE`; each handler an
  endpoint stub answering 501 with the original under `#if OFFRAMP_HTTPHANDLER`, and a
  commented `MapMethods` line: until it is ported, the proxy keeps sending its path to the
  legacy application (`OFR4202`). Web Forms files are `OFR4201`, one per page or control.
- **Hosted projects.** A plugin, area, or module serves nothing by itself: `hostedBy`
  names its host, `--legacy-url` defaults to the host's IIS URL, and `OFR0205`
  (warning) says that only the hosted project's actions are ported and that scaffolding
  the host puts the whole application behind the new one (ADR 0055).
- A dry run until `--apply`, which writes through a journal. Schema:
  `schemas/v1/web-scaffold.json`.

## `csproj modernize`

Legacy csproj → SDK-style, plus modern hygiene for already-SDK projects.

```
offramp csproj modernize --project P|--all [--tfm net48;net10.0] [--nullable enable] [--accept-diff] [--apply]
```

(`--cpm` and `--hoist` are deferred; `deps consolidate --cpm` centralizes versions. See
ADR 0026.)

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

### Details (M12)

Decisions in `docs/decisions/0026-web-csproj-config-extract.md`.

- Offramp converts legacy projects itself (no `try-convert`): the conversion is a pure
  function of the project file, the model's evaluated items, the files on disk, and
  `packages.config`, so a dry run shows exactly what `--apply` writes.
  - Properties the SDK sets (`ProjectGuid`, `OutputPath`, `FileAlignment`,
    `TargetFrameworkVersion`, the imports, ...) are dropped and listed in `removed`; the
    others are kept. `TargetFrameworkVersion` becomes `TargetFramework` (or `--tfm`).
    With a single target framework the result sets `AppendTargetFrameworkToOutputPath` to
    `false`, so the output stays in the folder the legacy project wrote it to (build steps
    and `HintPath`s into it keep working).
  - The converted project compiles against what the legacy one did (ADR 0043). When a
    referenced project has project references of its own (or is not in the model), it sets
    `DisableTransitiveProjectReferences` to `true`: an SDK-style project would also compile
    against its references' references. NuGet 2's restore import (`.nuget\NuGet.targets`)
    goes with `RestorePackages`, since `PackageReference` restore replaces it; `SolutionDir`
    is dropped too, unless something the conversion keeps (a build event, an import) still
    uses `$(SolutionDir)`: then its definition, with its fallback for builds outside the
    solution, stays.
  - `Compile`, `.resx` `EmbeddedResource`, and `None` items become the SDK's globs when
    those give the same files; otherwise the Compile list stays with
    `EnableDefaultCompileItems` false (`OFR4301`). `Link`, `DependentUpon`, `Generator`,
    and resource names are kept as `Update` items. Every item keeps its `Condition` and its
    metadata, whether written as child elements or as attributes; a `ProjectReference` drops
    only `Project` and `Name` (a source generator's `OutputItemType="Analyzer"` stays).
  - `packages.config` becomes `PackageReference` items (development dependencies with
    `PrivateAssets="all"`; versions omitted under central package management); `HintPath`
    references into `packages/` are dropped with it. Framework `Reference` items stay; when a
    target framework is not .NET Framework (`--tfm "net48;net10.0-windows"`), they go in an
    item group conditioned on `'$(TargetFrameworkIdentifier)' == '.NETFramework'`, and a
    `-windows` target gets `UseWindowsForms` (a `System.Windows.Forms` reference) or `UseWPF`
    (`PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`). A package
    that a project in the ProjectReference closure passes on at a higher version (converted in
    the same run, or restoring the `PackageReference` way already) raises the project's own
    version to it, since the lower one would be a package downgrade (NU1605): `OFR4307`.
  - `BeforeBuild`/`AfterBuild` become targets hooked at the same point (`OFR4302`).
    `PreBuildEvent`/`PostBuildEvent` stay where the legacy project defines them, and a
    `SetBuildEvents` target that runs before `BeforeBuild` sets each one that is set again,
    with the same text and its conditions: in the project body the SDK has not defined
    `$(TargetPath)` and the other macros yet, in the target they have their values. The SDK's
    own `PreBuildEvent`/`PostBuildEvent` targets run them as the legacy build did (from the
    output folder, by `RunPostBuildEvent`'s rule), and under `-p:PostBuildEvent=` the property
    stays empty, the target does not set it, and the event does not run (`OFR4302`; ADR 0061).
  - `OFR4308` names what the SDK overrides without a word: a target in the body with the name
    of a common target (`AfterCompile`, `_CopyFilesMarkedCopyLocal`, ...), which the SDK's
    targets, imported after the body, replace; and an imported file in the repository that sets
    `TargetFrameworkVersion`, `OutputPath`, `IntermediateOutputPath`, `MSBuildExtensionsPath`,
    or the like without a condition.
  - ASP.NET web application projects and non-C# projects are not converted (`OFR4304`).
    `--all` takes every project of the model, so each one it does not convert is named.
  - AssemblyInfo attributes the SDK generates are removed with the `assemblyinfo` codemod
    (the SDK generates them from properties instead), from the project's own files only. A file
    outside the project's folder, compiled by another project, ignored by git, added to the
    compilation by a build target, or generated code keeps its attributes, and the converted
    project sets the matching `GenerateAssembly<Name>Attribute` properties to `false` instead
    (`OFR4306`; ADR 0039). This holds for a single `--project` and for `--all` alike.
- SDK-style projects only get `--tfm` and `--nullable` when asked.
- **Verification always runs**, dry run included: the change set is applied in a scratch
  copy, the changed projects are built with a binary log, and each target's compiler
  inputs (source files, references by file name, embedded resources by manifest name)
  are compared with the scan's build of the original. Reference changes the SDK and
  `PackageReference` make by themselves are allowed and reported with their evidence in
  `explanations` (ADR 0061): the SDK's implicit framework references; the compile assemblies
  of a package the converted project's restore resolved (`transitiveReferencesAdded`: a
  package's other assemblies, packages flowing from referenced projects); framework assemblies
  that restored packages declare in their nuspec (`frameworkReferencesAdded`); and the .NET
  Standard facades the legacy build added from the framework's `Facades` folder or
  `Microsoft.NET.Build.Extensions` (`facadesRemoved`). Any other added or removed reference is
  a difference. Sources the build generates are not compared: those under `obj/` and in the
  folder the compiler writes the assembly to (the intermediate output path, wherever the project
  puts it). The scratch copy holds the committed tree plus the model's inputs from the
  working tree, imported files in dot-directories and untracked ones included. A
  difference or a failed build is `OFR4303`; `--apply` then refuses unless `--accept-diff`. A build that fails only on NuGet audit (NU1901–NU1904,
  known vulnerabilities, which `PackageReference` restore reports and
  `TreatWarningsAsErrors` makes errors) is not the conversion's fault: it is reported as
  `OFR4305` and the verification build runs again with `NuGetAudit=false`. A failed build is
  reported in full: `verification.built` is false, `buildErrorCount` counts its distinct
  errors, `buildErrorCodes` gives the count per code (most frequent first, then by code), and
  `buildErrors` holds the first 10, with paths relative to the repository; `OFR4303`'s message
  gives the count by code, and the terminal view says the project does not build. Schema:
  `schemas/v1/csproj-modernize.json`.

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

### Details (M12)

Decisions in `docs/decisions/0026-web-csproj-config-extract.md`.

- The file is the project folder's `App.config` or `Web.config` (`OFR4405` when there is
  none). Output goes next to it, or to `--out`; an existing output file stops the run
  (`OFR4406`).
- `appSettings` become root keys (what `IConfiguration["key"]` reads) and
  `AppSettingsOptions`; `connectionStrings` the `ConnectionStrings` section (read with
  `GetConnectionString`); `providerName` is kept as a note.
- Custom sections: the section class is read with the semantic model
  (`[ConfigurationProperty]` attributes: names, types, defaults, required). Scalars keep
  their JSON type; nested elements become objects; an element collection, a custom
  `TypeConverter`, or a value that does not parse is `OFR4401` and left out. Each section
  gets an options class (`NameOptions`, a class with setters so it binds on every target).
- `system.serviceModel` is `OFR4402`, `system.web`/`system.webServer`/`system.web.extensions`
  `OFR4403`; `runtime`, `startup`, and `system.diagnostics` are dropped with a note. A
  section group is converted section by section under nested keys (`bundleTransformer/core`
  → `BundleTransformer:Core`), and `--sections` takes a group's name (ADR 0060); elements
  are matched by local name, so a section's `xmlns` does not hide them. The
  sections machine.config declares are known without a declaration: the ones whose settings
  .NET makes in code (`system.net`, `system.data`, `system.transactions`,
  `system.runtime.caching`, ...) are `OFR4407`, with the code that replaces them; the ones
  .NET has nothing for (`system.codedom`, `system.xml.serialization`, `uri`, ...) are dropped
  with a note. Any other section not declared in `configSections`, or whose class is not in
  the solution, is `OFR4401`.
- Transforms: `SetAttributes`, `Replace`, and `Insert` of `appSettings` and
  `connectionStrings` entries located by key or name, and `SetAttributes`/`Replace` on a
  converted custom section, become overrides in `appsettings.{Environment}.json`; the
  rest is listed (`OFR4404`).
- `--shim` writes `ConfigurationManagerShim` (`AppSettings`, `ConnectionStrings`, set up
  with `Initialize(IConfiguration)`). The `config-manager-shim` codemod (OFRM014) points
  the `ConfigurationManager` call sites that `config-manager` cannot inject into (static
  members, classes created with `new`) at it.
- For an SDK-style project, `appsettings*.json` is copied to the output and, with
  `--shim`, `Microsoft.Extensions.Configuration.Abstractions` is referenced. Schema:
  `schemas/v1/config-convert.json`.
