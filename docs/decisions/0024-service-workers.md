# 0024. Lift ServiceBase classes into BackgroundService workers in a new project, link the code they use, and defer --in-place

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/scaffold.md#service`

## Context

The spec says what `service` generates. It leaves these open:
- how the `OnStart`/`OnStop` bodies get into a worker without the rest of the class
- which timers become `PeriodicTimer` loops, and what happens when a tick throws
- what "a clearly marked region" is
- where the worker project gets the code the service uses
- how several services in one executable are hosted
- what the health endpoint reports
- whether `--in-place` ships now

## Decision

- **Output.** `service` writes a new project, `NAME.Worker` next to the service project
  by default. It targets `netN.0` from `offramp.yml`. It uses `Microsoft.NET.Sdk.Worker`,
  or `Microsoft.NET.Sdk.Web` with `--health`. The service project is not edited.
- **`--in-place` is deferred** until `csproj modernize` (M12) exists to convert the
  project. Until then the removal list (installers, `System.ServiceProcess` and
  `System.Configuration.Install` references, Topshelf packages, `requestedExecutionLevel`
  manifests) is reported.
- **Lifting a `ServiceBase` class.** The worker is a new class, `NameWorker` (a `Service`
  suffix is dropped).
  - It keeps the service's fields, properties, helpers, and constructors as text, in the
    original namespace with the original usings, minus `System.ServiceProcess`.
  - The lifecycle overrides become private methods. `ExecuteAsync` yields, calls
    `OnStart(Array.Empty<string>())`, then runs the timer loops or waits.
  - `StopAsync` stops the loops (`base.StopAsync`), then calls `OnStop`, or `OnShutdown`
    when there is no `OnStop`.
  - `OnPause`/`OnContinue` sit behind public `Pause`/`Continue` methods that set
    `Paused`, which the loops honor. Nothing calls them (OFR4101).
  - Constructors get the logger (and heartbeat) injected first.
  - Designer boilerplate (`components`, its `Dispose(bool)`, and an `InitializeComponent`
    that only sets `ServiceBase` properties) and `ServiceBase` property assignments are
    dropped.
- **Uses of `ServiceBase`.**
  - `EventLog.WriteEntry(message[, type])` becomes structured logging at the matching
    level.
  - Reads of `ServiceName` become the name as a string.
  - `base.OnX()` calls are dropped.
  - Any other statement that uses a `ServiceBase` member is kept inside
    `#if OFFRAMP_SERVICEBASE // OFR4102: ...` / `#endif`. The symbol is never defined, so
    the code stays visible, does not compile, and is findable. A use outside a statement
    puts the whole member in the region.
  - `OnSessionChange` and `OnPowerEvent` go in such a region (OFR4105).
- **Timers.**
  - A `System.Timers.Timer` field converts to a `PeriodicTimer` loop only when every use
    is setup: `Interval`, `Elapsed +=`, `Start`/`Stop`/`Dispose`, `Enabled`, `AutoReset`,
    or construction.
  - The interval must not depend on locals or parameters, and `AutoReset` must not be
    `false`. The Elapsed handler (a method of the class or a lambda) must not read its
    `sender` or arguments.
  - The setup statements and the field go.
  - Each tick runs in a try/catch that logs and continues, because `System.Timers.Timer`
    swallowed exceptions too.
  - Other timers, including every `System.Threading.Timer`, stay as they are. They work
    on modern .NET.
- **Topshelf.** The worker wraps the configured class:
  - `ConstructUsing` becomes its creation, else `new T()`.
  - `WhenStarted`/`WhenStopped` bodies become the start and stop calls, with the lambda
    parameter as the field.
  - A lambda that uses the host settings or `HostControl` is OFR4102.
- **Linked code.** The worker compiles the files it needs from the service project as
  `Compile` links: the closure of project types the kept code uses, minus the service
  classes, the entry point, and the installers. This is the same mechanism `remote` uses
  (ADR 0023).
  - The worker, with the links, is compiled in memory against the target's reference
    assemblies and the worker's packages. Errors are OFR4106, and the project is still
    written.
  - `ConfigurationManager` use adds `System.Configuration.ConfigurationManager` and sets
    `<AppConfig>` to the service's `App.config`, so the settings keep working until
    `config convert`.
- **Several services** in one executable become one worker each, in one process that
  registers as one Windows service or systemd unit (OFR4103). The name is the service's
  own when there is one, else the project name.
- **Health.**
  - `WorkerHeartbeat` records each worker's last beat. Workers beat after `OnStart`, on
    every converted tick, and every 10 seconds when nothing ticks.
  - `/health` answers 200 while every worker has beaten within `Health:StaleAfterSeconds`
    (60), and 503 otherwise, with the per-worker times.
- **Hosts.** `windows`: `AddWindowsService`, `install.ps1` (`sc.exe create` with start
  type, account, dependencies, description, restart-on-failure), and `uninstall.ps1`.
  `both` adds `AddSystemd` and a systemd unit (`Type=notify`, `DynamicUser=yes`).
- **Containers.**
  - The Dockerfile is multi-stage from the repository root, runs as `$APP_UID`, and
    publishes only the worker.
  - `Dockerfile.dockerignore` keeps `bin/`, `obj/`, `.git/`, and `.offramp/` out of the
    context.
  - The shutdown timeout is 25 s from `HostOptions:ShutdownTimeoutSeconds`. The
    Kubernetes grace period is 30 s.
- **The image test** (`Category=Docker`) runs in its own CI job on ubuntu, where Docker
  runs Linux containers. The main test jobs exclude it.

## Alternatives considered

- Reference the service project from the worker and wrap the `ServiceBase` instance. The
  project is net48, so a net10.0 worker cannot reference it, and `ServiceBase` does not
  run on Linux.
- Rewrite with a Roslyn `SyntaxRewriter` and format the result. Every token of the user's
  code would be reprinted. Text edits keep their formatting and comments byte for byte.
- Comment out unsupported code. `#if` regions keep it compilable-in-principle, keep
  syntax highlighting, and are removable with `ifdef strip`.

## Consequences

- Linked files compile in both projects until the service project is retired. Then they
  move into the worker with `move plan`.
- Behavior that depended on the service control manager (pause, custom commands, session
  events, recovery options) needs a decision per service. The diagnostics say where.
