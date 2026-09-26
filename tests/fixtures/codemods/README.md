# codemods fixture

A .NET Framework application and a dual-target library with one site (or more) for each
codemod in `docs/spec/commands/codemod.md`, so `offramp codemod run` can be tested end to end:
the dry run, the applied rewrite verified by a build, and idempotency after a rescan.

| File | Codemod | Expected |
|---|---|---|
| `src/Shop/Program.cs` | `codepages`, `process-start-url` | provider registered in `Main`; `ProcessStartInfo` with `UseShellExecute` |
| `src/Shop/Orders.cs` | `sqlclient` | `Microsoft.Data.SqlClient`; `OFR4510` |
| `src/Shop/Mailer.cs` | `config-manager` | the instance read uses an injected `IConfiguration`; the static read is skipped (`OFR4501`) |
| `src/Shop/Feeds.cs` | `webclient` | the async download uses `HttpClient`; the synchronous one is skipped |
| `src/Shop/Json.cs` | `javascript-serializer` | `System.Text.Json` with the compatibility options |
| `src/Shop/Monitor.cs` | `service-controller` | the package, for modern targets |
| `src/Shop/Poller.cs` | `thread-abort` | experimental: only with `--experimental` |
| `src/Shared/Cloner.cs` | `binaryformatter-clone` | a `System.Text.Json` round trip; the package for net48 only |
| `src/Shared/Clock.cs` | `timezone-ids` | `TZConvert.GetTimeZoneInfo` |
| `src/Shared/Names.cs` | `string-comparison` | opt-in: only when named |

`assemblyinfo` needs a project that does not build (duplicate attributes, CS0579), so its
test adds `Properties/AssemblyInfo.cs` to a copy before scanning.
