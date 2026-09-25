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
