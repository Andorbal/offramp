# windows-service

Two .NET Framework services, one per style, for `service` and kind detection.

- `Heartbeat` (net48, `System.ServiceProcess`): two `ServiceBase` services in one
  executable (OFR4103).
  - `HeartbeatService` (partial, with a designer part that sets `ServiceName`):
    `OnStart` reads `ConfigurationManager.AppSettings` and starts a
    `System.Timers.Timer` (5000 ms, `Elapsed += OnElapsed`), which becomes a
    `PeriodicTimer` loop; `EventLog.WriteEntry` calls become logging;
    `OnStop` calls `RequestAdditionalTime` (OFR4102); `OnPause`/`OnContinue`
    (OFR4101). Uses `Pulse` (plain code, linked into the worker).
  - `JanitorService`: a `System.Threading.Timer` (kept as is) and
    `OnSessionChange` with `CanHandleSessionChangeEvent` (OFR4105).
  - `ProjectInstaller`: `LocalSystem`; Heartbeat starts automatically and
    depends on `EventLog` (OFR4104); Janitor starts manually.
- `Crier` (net48, Topshelf 4.3.0): the Topshelf quick start, `TownCrier` with
  `Start`/`Stop`, `RunAsLocalSystem`, `StartAutomatically`, `SetServiceName`.

Expected: `service --project Heartbeat` generates `Heartbeat.Worker` (net10.0)
with `HeartbeatWorker` and `JanitorWorker`; `--project Crier` generates
`Crier.Worker` with `TownCrierWorker`. Both build on every OS; the Heartbeat
image built from `--dockerfile --health` answers 200 on `/health`.
