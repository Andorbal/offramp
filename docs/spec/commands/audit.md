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

## Details: `audit api`, `behavior`, `serialization`, `native`

Decisions behind the four code audits (ADR 0021).

**Rules and packs.**
- Rules live in `rules/audit-api.yml`, `audit-behavior.yml`, `audit-serialization.yml`,
  and `audit-native.yml`. Each rule has a pack, a severity, a title, a category, a
  recommendation, and what it matches: `symbols`, `baseTypes`, `attributes`, or a
  named `matcher`.
- `symbols` are documentation IDs. A `T:` type matches the type and its members, an
  `N:` namespace everything in it, and an `M:` without a parameter list every
  overload. A name that binds to a namespace is never a finding: the types and
  members used from it are.
- One finding per rule and line.
- `--pack` runs only the packs named and overrides `rules.packs.disable`. A rule set to
  `none` in `offramp.yml` does not run. Any other override changes the severity and
  marks the finding `overridden`.

**Result and diagnostics.**
- The result (`schemas/v1/audit.json`) lists every finding, sorted by rule, project,
  file, line, column, and symbol.
- Each rule with findings in a project is also one diagnostic, located at the first
  finding and carrying the count, so `--fail-on` gates on audits.
- The projects audited are the C# projects with a recorded compiler call, each read at
  its first .NET Framework target (else its first target).
- Generated files under `obj/` and `bin/` are not audited.
- `--format sarif` writes SARIF 2.1.0: paths relative to `SRCROOT`, the rules that
  ran, and a partial fingerprint of rule, project, and symbol.
- `--format markdown` and `--format json` print the document alone, or write it to
  `--out` (the format follows the extension: `.sarif`, `.json`, `.md`).
- The terminal view and the Markdown list the first five locations per group
  (`--group-by rule|project|namespace|file`); `--all-locations` lists every one.

**`audit api` target compilation.**
- Only `framework`-class projects are compiled against the target.
- The reference assemblies come from the SDK: a scratch project under
  `.offramp/cache/targets/` with the target framework, `Microsoft.AspNetCore.App` for
  web projects, `Microsoft.WindowsDesktop.App` and `-windows` for WinForms and WPF, and
  the project's direct packages at their resolved versions. `dotnet msbuild -restore
  -getItem:ReferencePathWithRefAssemblies` lists them.
- Implicit asset target fallback is off, so a package without assets for the target
  fails restore with NU1202. It is left out, together with every direct package that
  depends on it (OFR3011), and the APIs used from it become OFR3001 findings.
- Any other restore failure leaves the project uncompiled (OFR3010). Its symbol rules
  still run.
- The target compilation keeps the recorded sources, compilation options, and language
  version. It swaps the .NET Framework preprocessor symbols (`NETFRAMEWORK`, `NET48`,
  ...) for the target's (`NET`, `NET10_0`, `NET10_0_OR_GREATER`, ...).
- Project references become the referenced project's own target compilation
  (`framework` class) or its recorded modern or standard build. `HintPath` DLLs are
  referenced as they are.
- OFR3001 comes from CS0234, CS0246, CS0103, CS1061, CS0117, and CS1069 (a type
  forwarded to an assembly the target does not reference). It is reported when the
  same position binds, in the recorded compilation, to a type or member from metadata.
  - When the error names a namespace (`System.Web.UI.Page` on a target without
    `System.Web.UI`), the finding is the first type or member to its right.
  - An attribute or constructor is reported as its type.
  - The finding carries the assembly and its `rules/framework-assemblies.yml` mapping.
- OFR3002: a symbol from metadata marked `[SupportedOSPlatform("windows")]` on itself,
  a containing type, or its assembly. `OperatingSystem.IsWindows()` guards are not
  recognized; .NET Framework code has none. Desktop projects, compiled for
  `-windows`, get no OFR3002.
- The porting ledger counts each project's audited C# files and the files with no
  error-level finding. `byNamespace` and `topNamespaces` count findings by the
  namespace of the symbol involved.

**`audit behavior` matchers.**
- OFR3101: `string.Compare` and `CompareTo`; `IndexOf`, `LastIndexOf`, `StartsWith`,
  and `EndsWith` with a string; `ToUpper()` and `ToLower()` with no arguments;
  `OrderBy`/`ThenBy` (and descending) over string keys. Each counts only without a
  `StringComparison`, `CultureInfo`, `IFormatProvider`, `CompareOptions`, or comparer.
- OFR3102: `Encoding.GetEncoding` with anything but a Unicode, ASCII, or Latin-1 page,
  including computed ones. A project that calls `Encoding.RegisterProvider` anywhere
  gets none.
- OFR3103: a constant (or the literal parts of an interpolated string) with a backslash
  or a drive letter passed to a `System.IO` API, plus the Windows-only
  `Environment.SpecialFolder` members.
- OFR3104: `FindSystemTimeZoneById` with a constant ID that has no `/` (except `UTC`
  and `GMT`), or with a computed ID.
- OFR3109: `double` or `float` `ToString` without a format string.
- OFR3113: every `Regex` constructed, or applied through a static method, without a
  timeout. Request paths cannot be told apart, so the rule is `info`.
- OFR3116: `<gcServer>`, `<gcConcurrent>`, `<GCCpuGroup>`, and the other GC and
  thread-pool elements under `<runtime>` in the project's `app.config` or
  `web.config`, located in that file.
- The other behavior rules match by symbol.

**`audit serialization`.**
- A formatter call is `Serialize` or `Deserialize` on BinaryFormatter, SoapFormatter,
  NetDataContractSerializer, ObjectStateFormatter, LosFormatter, or any `IFormatter`.
- It is **transient** (OFR3202, once per member) when all of these hold:
  - its stream is a `MemoryStream` created empty in the same member
  - the member both serializes and deserializes through that stream
  - the stream is used for nothing else but repositioning and disposal
- Everything else is **persisted or transported** (OFR3203). The evidence is one of:
  - `file`: a `FileStream`, `File.*`, or `FileInfo.*` stream
  - `parameter`: a stream parameter
  - `field`: a stream field or property
  - `memory-escapes`: a memory stream whose bytes are returned, stored, or passed on,
    or one created over existing data
  - `value`: an overload without a stream
  - `unknown`
- OFR3204 lists the types carried, once per type, with `iSerializable`,
  `onDeserialized`, and `delegates`:
  - the static type of the serialized argument
  - for an `object`, interface, or generic parameter, the arguments at the member's
    call sites in the same compilation, three levels deep
  - the cast applied to `Deserialize`
- OFR3205: a `[Serializable]` class or struct that no formatter call in any audited
  project reaches. A type is reached when it is carried, or is a base type or the type
  of a non-`[NonSerialized]` field of a reached type.

**`audit native`.**
- Every `[DllImport]` declared in the project is inventoried (OFR3301), with library,
  entry point, calling convention, character set, `SetLastError`, marshalled types, and
  `windowsOnly` (kernel32, user32, advapi32, and the other Windows system libraries).
- OFR3302: string, `char`, or `StringBuilder` parameters or return without
  `CharSet.Unicode` and without `[MarshalAs]`.
- OFR3303: a signature of primitive integers, floating-point numbers, pointers, and
  `IntPtr`/`UIntPtr` only, with no `ref` or `out`.
- OFR3310: `COMReference` items (located in the project file), `[ComImport]`, and
  `Marshal.GetActiveObject`.

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

### Details

- `report` counts, per project and per symbol, the `#if` chains whose conditions name
  the symbol, the lines inside them (directives excluded), and the files. It reads each
  project's C# compile items, active or not, from disk. Totals count a shared file
  once. Trending in the ledger is not in v1.
- `wrap` reads the `--format json` result (or the `--json` envelope) of `audit api`.
  - Each finding must still name its symbol at its position, parsed with the owning
    project's .NET Framework preprocessor symbols; one that does not is stale (OFR3602).
    Findings already inside a branch with the same condition are counted and left.
  - The smallest enclosing statement in a block is wrapped when it has lines of its
    own, declares no local used after it, and has no `return`, `throw`, `break`,
    `continue`, `goto`, or `yield` of its own.
  - Otherwise the enclosing member or type is wrapped, with its attributes and
    documentation comment. It is not wrapped (OFR3601) when any of these holds:
    - code outside the wrapped ranges and outside regions with the same condition uses
      it, in any project (matched by documentation ID)
    - it overrides, is abstract, or implements an interface member
    - it shares its lines with other code
  - Members used only from other wrapped code are wrapped with it.
  - Adjacent ranges share one region. `#if` and `#endif` go at the start of the line, as
    `dotnet format` places them. `--symbol` is written verbatim, so
    `--symbol '!NET10_0_OR_GREATER'` works.
- `strip --symbol S --keep true|false` decides each chain that names `S` with `S`
  defined or not and every other symbol unknown.
  - The first branch that is then certainly true stays, without its directive. Every
    other branch and directive line goes.
  - A chain whose outcome still depends on other symbols stays (OFR3603).
- `wrap` and `strip` insert or remove whole lines only; every other byte, including
  the byte order mark and line endings, stays. They are dry runs until `--apply`, which
  writes through a journal that `move rollback --journal` undoes.
- Schemas: `ifdef-report.json`, `ifdef-wrap.json`, `ifdef-strip.json`.
