# Audit commands: `audit api`, `audit behavior`, `audit serialization`, `audit native`, `audit dead-code`, `audit api-compat`, `ifdef`

Audits are read-only. Each finding has a rule ID (`OFR3###`), a severity,
a location, the symbol involved, a category, and a recommendation. Rules are
data (`rules/*.yml`, embedded, grouped in packs: `core`, `web`, `desktop`,
`data`, `serialization`, `native`) plus a matcher kind; the engine is shared.
Users override severity per rule in `offramp.yml` and the output marks
overridden findings.

Common options:

```
offramp audit <kind> [--target N] [--project P ...] [--pack NAME ...] [--format table|json|sarif|markdown] [--group-by project|rule|namespace|file]
```

SARIF output lets GitHub code scanning and IDEs show findings inline.

## `audit api` (OFR3000–3099)

What will not compile or will throw on the target.

Method:
1. **Trial compilation against the target reference pack.** For each
   `framework`-class project, create a Compilation with the same trees and the
   target's reference assemblies (from `Microsoft.NETCore.App.Ref` for the
   target version, plus `Microsoft.AspNetCore.App.Ref` for web projects, plus
   the package references that support the target). Each `CS0234`/`CS0246`/
   `CS0103`/`CS1061`/`CS0117` error on a symbol that resolves in the Framework
   compilation is a **missing API** finding (`OFR3001`) with the symbol's
   fully-qualified name and its `deps gac` mapping when one exists
   (`System.Web.HttpContext` → "no equivalent; see web scaffold").
2. **Windows-only APIs.** Symbols that resolve but carry
   `[SupportedOSPlatform("windows")]` in the target reference assemblies
   (`OFR3002`, warning unless the target is `-windows`).
3. **Throws on modern .NET** (`OFR3003`): a curated list of APIs that compile
   but throw `PlatformNotSupportedException` (e.g. `Thread.Abort`,
   `AppDomain.CreateDomain`, `Remoting`, `CodeDom` compilation,
   `System.Drawing` on non-Windows, `BinaryFormatter` without the switch).
4. **Removed technologies** (`OFR3004`–`3009`, error): WebForms (`System.Web.UI`),
   ASMX, WCF server without CoreWCF, Remoting, Workflow Foundation,
   `System.EnterpriseServices`, Code Access Security attributes, AppDomain
   sandboxing. Each carries the recommended direction.

Result: findings plus a **porting ledger**: per project, counts by category and
by namespace, a `portability` score (fraction of files with no error-level
finding), and the top 20 offending API namespaces across the solution, which
is what `seams` consumes.

## `audit behavior` (OFR3100–3199)

Compiles fine, behaves differently. Pattern matchers over the semantic model
(symbol identity, not names). Initial rule set; each rule has a docs link:

| Rule | Detects | Why it matters |
|---|---|---|
| OFR3101 | culture-sensitive string ops without an explicit comparison: `string.Compare`, `IndexOf(string)`, `StartsWith(string)`, `ToUpper()`, `OrderBy` on strings | ICU on Linux/modern .NET sorts and matches differently from NLS |
| OFR3102 | `Encoding.GetEncoding(int|name)` for non-Unicode code pages | needs `CodePagesEncodingProvider.Instance` registration |
| OFR3103 | hard-coded `\\` in paths, `Path.Combine` with drive letters, `Environment.SpecialFolder` Windows-only members | case-sensitive file systems and separators |
| OFR3104 | `TimeZoneInfo.FindSystemTimeZoneById` with Windows IDs | IANA IDs on Linux (`TimeZoneConverter` or .NET 6+ conversion) |
| OFR3105 | `Registry`, `Microsoft.Win32` | Windows only |
| OFR3106 | `HttpContext.Current`, `HttpRuntime`, `HostingEnvironment` | no ambient context in ASP.NET Core |
| OFR3107 | `Process.Start(string url)` | `UseShellExecute` default changed to false |
| OFR3108 | `System.Data.SqlClient` types | move to `Microsoft.Data.SqlClient`; `Encrypt` defaults to true in 4.0+ |
| OFR3109 | `double/float.ToString()` without format, `decimal` round-tripping assumptions | shortest round-trippable formatting since Core 3.0 |
| OFR3110 | `WebRequest`, `WebClient`, `ServicePointManager` | obsolete; `HttpClient`/`SocketsHttpHandler` |
| OFR3111 | `Thread.CurrentPrincipal` for auth, `WindowsIdentity` | not flowed the same way; ASP.NET Core uses `HttpContext.User` |
| OFR3112 | `ConfigurationManager.AppSettings/ConnectionStrings` | works via package but reads `app.config` of the host, not `web.config`; consider `IConfiguration` |
| OFR3113 | `Regex` without timeout in request paths | behavior same, but `RegexOptions.NonBacktracking` available; info |
| OFR3114 | `SslProtocols` pinned to TLS 1.0/1.1 | rejected by modern defaults |
| OFR3115 | `Assembly.LoadFrom`/`LoadFile` and `AppDomain.AssemblyResolve` | `AssemblyLoadContext` semantics |
| OFR3116 | `GC` settings, `Server GC` via app.config, `gcConcurrent` | now `runtimeconfig.json` |
| OFR3117 | `Uri` escaping assumptions, `HttpUtility.UrlEncode` vs `WebUtility` | behavior differences in escaping |
| OFR3118 | `System.Web.Security.MachineKey`, Forms Auth cookies | Data Protection replaces MachineKey; cookie sharing needs adapters |
| OFR3119 | `Timer` types (`System.Timers.Timer` in services), `ThreadPool.SetMinThreads` | usually fine; info for services heading to containers |
| OFR3120 | `Environment.OSVersion`, `RuntimeInformation` checks assuming Windows | branch review |

Output groups by rule with counts, then by project, with the first N
locations per rule and `--all-locations` to expand.

## `audit serialization` (OFR3200–3299)

- `BinaryFormatter`, `SoapFormatter`, `NetDataContractSerializer`,
  `ObjectStateFormatter`, `LosFormatter` usages (`OFR3201` error on target ≥ 9,
  warning on 8).
- For each usage, classify the flow by data-flow heuristics over the
  semantic model: **transient** (serialize and deserialize in the same method
  or type, typical deep-clone; `OFR3202` info with a codemod suggestion) vs
  **persisted/transported** (stream from `File`, `Stream` parameter, DB
  parameter, `MemoryStream.ToArray()` leaving the method, `Session`, MSMQ,
  cache; `OFR3203` error: needs a migration path).
- The **types serialized**: static type of the argument, and when it is
  `object`, the concrete types passed at call sites found via
  `FindCallersAsync` (`OFR3204` lists them, with whether they implement
  `ISerializable`, have `[OnDeserialized]` hooks, or contain delegates and
  other unserializable members).
- `[Serializable]` types that are never passed to any serializer are reported
  as safe to leave alone (`OFR3205` info).
- `JavaScriptSerializer`, `DataContractJsonSerializer` (`OFR3210`, warning,
  System.Text.Json suggested), `XmlSerializer` with `sgen` (`OFR3211`, info:
  use `Microsoft.XmlSerializer.Generator` or drop).
- Recommendation block: for persisted data, the bridge is the
  `System.Runtime.Serialization.Formatters` compatibility package plus the
  `EnableUnsafeBinaryFormatterSerialization` switch while a dual-read
  migration runs.

## `audit native` (OFR3300–3399)

- `DllImport` inventory: library name, entry point, calling convention,
  `CharSet`, `SetLastError`, marshalled types (`OFR3301` per import; Windows
  libraries flagged `windowsOnly`).
- Marshalling defaults that differ between Framework and modern .NET:
  `CharSet.Auto`/default (ANSI on Framework) (`OFR3302`), `bool` marshalling,
  `LPStruct`, `SafeHandle` opportunities.
- `LibraryImport` migration candidates (blittable signatures) (`OFR3303` info).
- COM: `COMReference` items, `dynamic` over COM objects,
  `Marshal.GetActiveObject`, `[ComImport]` (`OFR3310`, Windows only, no
  cross-platform path).
- `Marshal.GetHRForException`, `SEHException` handling (`OFR3320` info).

## `audit dead-code` (OFR3400–3499)

Unreferenced code across the whole solution, with honesty about what static
analysis cannot see.

```
offramp audit dead-code [--scope public|all] [--min-confidence high|medium|low] [--include-tests]
```

- For every named type and member in scope, `FindReferencesAsync` across all
  compilations (solution-wide). Zero references outside its own declaration →
  candidate.
- Confidence: `high` when the symbol is `internal`/`private` or the assembly
  has no `InternalsVisibleTo` and is not packed; `medium` for `public` symbols
  in assemblies that other repositories might consume (packable, or listed in
  `deadCode.externalConsumers`); `low` when any of: the symbol name appears in
  a string literal or resource anywhere in the solution, the type matches a DI
  convention pattern (`services.Scan`, `RegisterAssemblyTypes`, MediatR
  handlers, controllers, `[Export]`), the type has `[Serializable]`/data
  contract attributes, or it is an entry point/`Main`/`Program`.
- Test-only usage: with `--include-tests`, a production symbol referenced only
  from tests is reported separately (`OFR3402`); that is a strong "move to test
  project or delete" signal.
- Output: per project, list with symbol, kind, confidence, evidence, and
  lines of code that would go away. Summary shows the total LOC removable at
  `high` confidence, which is a stakeholder number.

## `audit api-compat` (OFR3500–3599)

Public API surface comparison using Microsoft's ApiCompat.

```
offramp audit api-compat --project P [--left net48 --right net10.0] | [--baseline GIT_REF]
```

- Dual-target projects: compares the `net48` and modern builds; every
  difference is `OFR3501` (a member available on one target only, often
  from an `#if`).
- `--baseline`: compares the current build with the build at a git ref, for
  "did moving files change any public surface" (`OFR3502`).
- Wraps `Microsoft.DotNet.ApiCompat.Tool`; requires built assemblies (runs
  `verify`-style builds if missing).

## `ifdef` (OFR3600–3699)

Manage conditional compilation used to bridge targets.

```
offramp ifdef report [--symbol NETFRAMEWORK|NET10_0_OR_GREATER|...]
offramp ifdef wrap --findings audit.json [--symbol NETFRAMEWORK] [--apply]
offramp ifdef strip --symbol NETFRAMEWORK [--keep true|false] [--apply]
```

- `report`: counts of `#if` regions per symbol per project and the lines
  inside them, trending via the ledger so the bridge debt is visible.
- `wrap`: for findings from `audit api` at statement or member granularity,
  wraps the smallest enclosing statement or member in `#if NETFRAMEWORK ...
  #endif` (or `#if !NET10_0_OR_GREATER`), preserving formatting exactly
  outside the inserted lines. Members that are required by callers on both
  targets are not wrapped (`OFR3601`, needs a real port).
- `strip`: removes regions for a symbol, keeping the branch selected by
  `--keep`, for the day `net48` is dropped.
