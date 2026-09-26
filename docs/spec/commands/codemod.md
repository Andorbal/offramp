# `codemod`

Bulk, idempotent Roslyn rewrites for recurring migration patterns. Implemented
as analyzers + code fixes in `Offramp.Analyzers` (netstandard2.0) so they can
also ship as a NuGet analyzer package and be applied by `dotnet format
analyzers`. The `codemod` command is a driver that runs a chosen fixer across
a project set with reporting, dry-run diffs, and verification.

```
offramp codemod list
offramp codemod run --mod NAME [--project P ...] [--apply] [--verify ...]
offramp codemod run --mod NAME --format-mode   # delegates to `dotnet format analyzers --diagnostics OFRM###`
```

## Rules for a codemod

- Idempotent: running twice produces no second change.
- Formatting-preserving: only the rewritten nodes change; trivia is kept.
- Semantically checked: the fixer uses the semantic model, never names.
- Reports: files changed, sites rewritten, sites skipped with reasons
  (`OFR4501` per skipped site).
- Tested with `Microsoft.CodeAnalysis.CSharp.CodeFix.Testing` (before/after
  pairs) and with a fixture-level run.
- Each codemod has an ID `OFRM###` (analyzer diagnostic) and a short name.

## Initial catalog

| Name | ID | Rewrite |
|---|---|---|
| `sqlclient` | OFRM001 | `System.Data.SqlClient` → `Microsoft.Data.SqlClient` usings and types; adds the package; emits `OFR4510` about `Encrypt` default |
| `config-manager` | OFRM002 | `ConfigurationManager.AppSettings["k"]` → injected `IConfiguration["k"]` or the shim from `config convert` (`--shim` mode when DI is not available) |
| `http-context` | OFRM003 | `HttpContext.Current` → `IHttpContextAccessor` parameter in classes already using constructor injection; otherwise skipped with reason |
| `webclient` | OFRM004 | `WebClient.DownloadString/UploadString/DownloadData` → `HttpClient` equivalents in async-capable methods; sync sites skipped |
| `javascript-serializer` | OFRM005 | `JavaScriptSerializer.Serialize/Deserialize<T>` → `System.Text.Json.JsonSerializer` with a compatibility options preset (camelCase off, case-insensitive on) |
| `binaryformatter-clone` | OFRM006 | the deep-clone idiom (serialize+deserialize in one method) → `System.Text.Json` clone helper or a generated `Clone()`; only when `audit serialization` classified the site as transient |
| `thread-abort` | OFRM007 | `Thread.Abort()` patterns → `CancellationTokenSource` plumbing where the thread body is in the same type; otherwise skipped |
| `process-start-url` | OFRM008 | `Process.Start(url)` → `new ProcessStartInfo(url) { UseShellExecute = true }` |
| `string-comparison` | OFRM009 | culture-sensitive string calls → explicit `StringComparison.Ordinal` (or `OrdinalIgnoreCase` when the original used `ToLower/ToUpper` comparisons); opt-in per rule group because it changes behavior deliberately |
| `codepages` | OFRM010 | inserts `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at the entry point when `audit behavior` found OFR3102 |
| `timezone-ids` | OFRM011 | `FindSystemTimeZoneById("Pacific Standard Time")` → `TimeZoneInfo.FindSystemTimeZoneById` with `TZConvert` fallback |
| `service-controller` | OFRM012 | `System.ServiceProcess` references in non-service code → `System.ServiceProcess.ServiceController` package |
| `assemblyinfo` | OFRM013 | duplicate `AssemblyInfo` attributes vs. SDK-generated ones → removed |

Codemods are added when an audit rule has a mechanical, safe rewrite for a
meaningful share of its findings. A codemod that is right less than ~95% of
the time on the fixtures and corpus is kept behind `--experimental`.

## Analyzer package

`Offramp.Analyzers` is also packable as `Offramp.Analyzers` (NuGet) so teams
can get the diagnostics in the IDE without the CLI. The package ships with all
rules set to `suggestion` severity by default and an `.editorconfig` sample.

## Details (M11)

Decisions in `docs/decisions/0025-codemods.md`.

### The driver (`codemod run`)

- `--mod` takes names or IDs, comma-separated or repeated. `all` is every codemod that is
  neither opt-in (`string-comparison`) nor experimental (`thread-abort`, unless
  `--experimental`). An unknown name is OFR4502; an experimental one without
  `--experimental` is OFR4503. Both stop the command before anything runs.
- Projects: `--project` (repeatable), else every C# project with a recorded compilation.
  Each project is analyzed in its first .NET Framework target (else its first target),
  as the other analyses do. Code inactive in that target is not seen.
- For each project, the recorded compilation becomes a Roslyn workspace project. The
  chosen codemods run one after another in catalog order. Each one analyzes the project
  as the previous codemods left it, then fixes each document's sites in one
  `FixDocumentAsync` pass followed by the code action cleanup (simplify, then format the
  annotated nodes). This is the same code path as the IDE's fix-all and
  `dotnet format`.
- Only the project's own compile items are rewritten: not generated code, and not files
  outside the repository.
- A site whose file differs from the text the scan recorded is skipped: OFR4504, once
  per file. So is a site in a file that is not UTF-8.
- Rewritten text keeps the file's byte order mark and its dominant line ending.
- Site lines and columns are in the file as scanned, mapped back through earlier fixes.
- The analyzers read `build_property.UsingMicrosoftNETSdk` (from the model) and
  `build_property.GenerateAssemblyInfo` (true when the recorded compilation contains the
  SDK's generated AssemblyInfo file), on top of the recorded global options.
- Packages: a codemod with at least one rewritten (or, for `service-controller`,
  referenced) site adds its packages to the project unless the project file or the model
  already references them.
  - `framework` packages go in an item group conditioned on
    `'$(TargetFrameworkIdentifier)' == '.NETFramework'`. They are skipped when the
    project has no .NET Framework target.
  - `modern` packages go in an item group conditioned on `!= '.NETFramework'`.
  - Under central package management the version goes to `Directory.Packages.props` (the
    recorded `DirectoryPackagesPropsPath`, else the nearest one above the project).
  - A packages.config project, or a central project without a versions file, gets
    OFR4505 instead.
- `assemblyinfo` moves the removed attributes' values to project properties
  (`AssemblyTitle`, `Company`, `Product`, `AssemblyVersion`, `FileVersion`,
  `InformationalVersion`) unless the project already sets them.
- Every skipped site is OFR4501. `sqlclient` adds OFR4510 once per project.
- A dry run by default, with the diff in `preview`. `--apply` writes through a journal
  (`move rollback --journal` undoes it), then builds the changed projects and their direct
  dependents (`--verify end`, the default; `--verify none` skips it). A failed build
  restores every file (OFR4507) unless `verify.onFailure: keep`.
- Schemas: `schemas/v1/codemod-list.json`, `schemas/v1/codemod-run.json`.

### `--format-mode`

- Runs `dotnet format analyzers PROJECT --diagnostics OFRM### ... --severity info
  --report DIR` for each project that references the `Offramp.Analyzers` package. Other
  projects are skipped with OFR4506.
- A dry run adds `--verify-no-changes`; `--apply` lets dotnet format write the files (no
  journal: the files are dotnet format's to write).
- Sites come from its report. They are all `rewritten`, because dotnet format does not
  report what it skips. `mode` is `format`, and `targetFramework`, `packages`, and
  `properties` are empty.
- A run that fails (exit code other than 0, or other than 2 in a dry run) or writes no
  report is OFR4508.

### The rewrites

| Codemod | Rewrites | Skips (OFR4501) |
|---|---|---|
| `sqlclient` | `using System.Data.SqlClient` and qualified names → `Microsoft.Data.SqlClient` | none |
| `config-manager` | `ConfigurationManager.AppSettings["k"]` → `_configuration["k"]`; `ConnectionStrings["k"].ConnectionString` → `ConfigurationExtensions.GetConnectionString(_configuration, "k")`. `IConfiguration` is injected through the constructor (field, parameter, assignment) | static members and initializers; classes without constructor injection (one constructor with parameters), or whose constructor chains or has no block body; classes created with `new` in the project; lookups that are not one key, use the `ConnectionStringSettings` object, or write |
| `http-context` | `HttpContext.Current` → `((System.Web.HttpContext)_httpContextAccessor.HttpContext)`, injected the same way | as `config-manager`; projects without ASP.NET Core's `IHttpContextAccessor` or the System.Web adapters' `HttpContext`; assignments |
| `webclient` | a `WebClient` local used only for `DownloadString`, `DownloadData`, and two-argument `UploadString` in an async method → `HttpClient` with the awaited `GetStringAsync`, `GetByteArrayAsync`, and `PostAsync` | synchronous methods; clients configured at creation or not in a local of their own; other members; calls inside `lock`; projects without System.Net.Http |
| `javascript-serializer` | `Serialize`/`Deserialize<T>` → `JsonSerializer` with a static options field: `PropertyNameCaseInsensitive = true`, `IncludeFields = true`, names as declared | members with no System.Text.Json equivalent (`DeserializeObject`, `ConvertToType`, ...) |
| `binaryformatter-clone` | a method that serializes its argument to a `MemoryStream` with `BinaryFormatter` and returns it deserialized → a `JsonSerializer` round trip with `IncludeFields = true` | a serialized value that is not of the returned type |
| `thread-abort` (experimental) | `_thread.Abort()` on a field whose thread body is a method of the type with a top-level `while` loop → a `CancellationTokenSource` field; the loop checks it, `Thread.Sleep(n)` waits on its token, and `Abort()` becomes `Cancel()` | threads that are not such a field |
| `process-start-url` | `Process.Start(file[, args])` → `Process.Start(new ProcessStartInfo(file[, args]) { UseShellExecute = true })` | none |
| `string-comparison` (opt-in) | `StartsWith`/`EndsWith`/`IndexOf`/`LastIndexOf(string, ...)`, `string.Compare` → `StringComparison.Ordinal` (or `OrdinalIgnoreCase` for `Compare(a, b, true)`); `a.ToLower() == b.ToLower()` → `string.Equals(a, b, OrdinalIgnoreCase)` | `Compare` whose `ignoreCase` is not a constant |
| `codepages` | `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` as the entry point's first statement, when the project asks for an encoding modern .NET does not have built in and registers no provider | none (a project without an entry point is not reported) |
| `timezone-ids` | `FindSystemTimeZoneById(id)` → `TZConvert.GetTimeZoneInfo(id)`, which takes Windows and IANA IDs | constant IANA IDs, which work as they are |
| `service-controller` | no code change: the `System.ServiceProcess.ServiceController` package for modern targets, when `ServiceController` is used outside a `ServiceBase` class | none |
| `assemblyinfo` | removes `AssemblyTitle`, `AssemblyCompany`, `AssemblyProduct`, `AssemblyConfiguration`, and the three version attributes in an SDK-style project that generates them; values move to project properties | projects with `GenerateAssemblyInfo=false` |

### The package

- `Offramp.Analyzers` (packed by `src/Offramp.Analyzers.CodeFixes`) holds both
  assemblies under `analyzers/dotnet/cs`, a `build/Offramp.Analyzers.props` that makes
  `UsingMicrosoftNETSdk` and `GenerateAssemblyInfo` visible to the analyzers, a README,
  and `sample.editorconfig`.
- Every rule is `suggestion` (info) by default. `string-comparison` is disabled until
  enabled in `.editorconfig`.
- The analyzers target netstandard2.0 on Roslyn 4.8, so Visual Studio 2022 17.8+ and the
  .NET 8 SDK+ load them.
