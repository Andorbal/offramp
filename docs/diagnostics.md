# Diagnostics

Every Offramp diagnostic has a stable code, a meaning, a typical cause, and a fix.
This file is generated from `src/Offramp.Core/Diagnostics/DiagnosticCatalog*.cs`
by `eng/gen-diagnostics.sh`; do not edit it by hand. A test fails when the file
and the catalog disagree, and another fails when a code has no test that produces it.

Severity may be overridden per code in `offramp.yml` (`rules:`); overridden
findings carry `"overridden": true`. The severity listed here is the default;
where a command reports a code at another severity, the entry says so.

## Ranges

| Range | Area |
|---|---|
| OFR0001–0099 | workspace/model, configuration, and environment |
| OFR0100–0199 | project loading |
| OFR0200–0299 | graph/report |
| OFR1000–1999 | dependencies |
| OFR2000–2999 | moves |
| OFR3000–3999 | audits |
| OFR4000–4999 | scaffolding, seams, codemods |
| OFR5000–5999 | verification |
| OFR9000–9999 | MCP and LLM |

## Codes

| Code | Severity | Area | Title |
|---|---|---|---|
| [OFR0001](#ofr0001) | error | workspace | workspace model missing |
| [OFR0002](#ofr0002) | warning | workspace | workspace model stale |
| [OFR0003](#ofr0003) | error | scan | no binary log to reuse |
| [OFR0004](#ofr0004) | error | scan | log file not found or unreadable |
| [OFR0010](#ofr0010) | error | environment | .NET SDK not found |
| [OFR0011](#ofr0011) | error | environment | SDK requested by global.json is not installed |
| [OFR0012](#ofr0012) | error | environment | selected SDK cannot target the requested framework |
| [OFR0013](#ofr0013) | error | environment | .NET Framework reference assemblies not resolvable |
| [OFR0014](#ofr0014) | warning | environment | git not found |
| [OFR0015](#ofr0015) | warning | environment | not a git repository |
| [OFR0016](#ofr0016) | info | configuration | no offramp.yml; built-in defaults in effect |
| [OFR0020](#ofr0020) | error | workspace | more than one solution found |
| [OFR0021](#ofr0021) | error | workspace | project not in the workspace model |
| [OFR0022](#ofr0022) | error | workspace | no solution found |
| [OFR0030](#ofr0030) | error | configuration | offramp.yml already exists |
| [OFR0050](#ofr0050) | warning | configuration | unknown key in offramp.yml |
| [OFR0051](#ofr0051) | warning | configuration | pin without a reason |
| [OFR0052](#ofr0052) | info | configuration | rule override without a reason |
| [OFR0053](#ofr0053) | error | configuration | invalid value in offramp.yml |
| [OFR0054](#ofr0054) | error | configuration | offramp.yml is not valid YAML |
| [OFR0055](#ofr0055) | error | configuration | configuration file not found |
| [OFR0056](#ofr0056) | error | configuration | invalid configuration value from the environment |
| [OFR0099](#ofr0099) | error | cli | internal error |
| [OFR0101](#ofr0101) | warning | project loading | project could not be loaded |
| [OFR0102](#ofr0102) | info | project loading | project kind unknown |
| [OFR0103](#ofr0103) | info | scan | model built from a compiler log alone |
| [OFR0104](#ofr0104) | warning | project loading | package graph unavailable |
| [OFR0110](#ofr0110) | warning | project loading | build step needs Windows: sgen |
| [OFR0111](#ofr0111) | warning | project loading | build step needs Windows: COM reference |
| [OFR0112](#ofr0112) | warning | project loading | build step needs Windows: EDMX EntityDeploy |
| [OFR0113](#ofr0113) | warning | project loading | build step needs Windows: T4 or Fakes |
| [OFR0114](#ofr0114) | warning | project loading | build step needs Windows: SSDT |
| [OFR0115](#ofr0115) | warning | project loading | build step needs Windows: build event calling a Windows executable |
| [OFR0120](#ofr0120) | warning | project loading | project reference cycle |
| [OFR0130](#ofr0130) | error | scan | analysis build failed; model partial |
| [OFR0131](#ofr0131) | error | scan | analysis build timed out |
| [OFR0132](#ofr0132) | warning | scan | compiler calls unavailable for some projects |
| [OFR0201](#ofr0201) | info | graph/report | graph too large for Mermaid |
| [OFR0202](#ofr0202) | warning | graph/report | ledger file is not a snapshot |
| [OFR1001](#ofr1001) | error | deps | no package version supports the target |
| [OFR1002](#ofr1002) | warning | deps | in-use version does not support the target |
| [OFR1003](#ofr1003) | warning | deps | package deprecated |
| [OFR1004](#ofr1004) | warning | deps | package assets are Windows-only |
| [OFR1005](#ofr1005) | warning | deps | package not found on any feed |
| [OFR1006](#ofr1006) | warning | deps | feed unreachable; result partial |
| [OFR1200](#ofr1200) | error | deps | package not referenced |
| [OFR1203](#ofr1203) | warning | deps | pin kept a package below the otherwise-selected version |
| [OFR1210](#ofr1210) | error | deps | pin conflicts with a transitive lower bound |
| [OFR1211](#ofr1211) | error | deps | restore verification failed |
| [OFR1212](#ofr1212) | error | deps | no version satisfies every constraint |
| [OFR1220](#ofr1220) | warning | deps | family member lacks the family version |
| [OFR1301](#ofr1301) | warning | deps | project outside the solution would inherit CPM |
| [OFR1302](#ofr1302) | warning | deps | nested Directory.Packages.props shadows the root |
| [OFR1303](#ofr1303) | warning | deps | packages.config project cannot use CPM |
| [OFR1401](#ofr1401) | info | deps | loose DLL is another project's output |
| [OFR1402](#ofr1402) | info | deps | loose DLL matched to a package |
| [OFR1403](#ofr1403) | warning | deps | loose DLL unmatched |
| [OFR1404](#ofr1404) | error | deps | loose Framework-only DLL with no replacement |
| [OFR1501](#ofr1501) | info | deps | binding redirect added |
| [OFR1502](#ofr1502) | info | deps | binding redirect changed |
| [OFR1503](#ofr1503) | info | deps | binding redirect pruned |
| [OFR1504](#ofr1504) | warning | deps | stale binding redirect |
| [OFR2001](#ofr2001) | warning | move | move would create a project reference cycle |
| [OFR2002](#ofr2002) | error | move | destination equals source |
| [OFR2003](#ofr2003) | error | move | project is frozen |
| [OFR2004](#ofr2004) | error | move | file is not part of the source project |
| [OFR2005](#ofr2005) | error | move | move plan file missing or invalid |
| [OFR2006](#ofr2006) | error | move | nothing to extract |
| [OFR2007](#ofr2007) | error | move | new project already exists |
| [OFR2008](#ofr2008) | error | move | new project's target references did not resolve |
| [OFR2050](#ofr2050) | error | move | verification failed; changes rolled back |
| [OFR2101](#ofr2101) | warning | move | file needs a co-move |
| [OFR2102](#ofr2102) | warning | move | required package unavailable for destination |
| [OFR2103](#ofr2103) | warning | move | file does not compile in the destination |
| [OFR2104](#ofr2104) | error | move | source still depends on moved code |
| [OFR2105](#ofr2105) | warning | move | moved file uses Windows-only APIs |
| [OFR2110](#ofr2110) | info | move | partial type co-moved |
| [OFR2111](#ofr2111) | warning | move | destination excludes the file path |
| [OFR2120](#ofr2120) | warning | move | namespace differs from destination root namespace |
| [OFR2150](#ofr2150) | warning | move | file changed since plan |
| [OFR2151](#ofr2151) | error | move | file changed since the move; rollback stopped |
| [OFR2152](#ofr2152) | error | move | interrupted move cannot be resumed |
| [OFR2201](#ofr2201) | warning | move | test code used by production code |
| [OFR2202](#ofr2202) | error | move | multiple candidate test projects |
| [OFR2203](#ofr2203) | error | move | no test project found |
| [OFR2204](#ofr2204) | warning | move | destination path collision |
| [OFR2205](#ofr2205) | error | move | project language not supported |
| [OFR2206](#ofr2206) | warning | move | file outside the project folder |
| [OFR2210](#ofr2210) | info | move | test-framework packages removable from source |
| [OFR2301](#ofr2301) | warning | move | string reference to a moved type |
| [OFR2302](#ofr2302) | error | move | revision not found |
| [OFR3001](#ofr3001) | error | audit | API missing on target |
| [OFR3002](#ofr3002) | warning | audit | API available only on Windows |
| [OFR3003](#ofr3003) | error | audit | API throws on modern .NET |
| [OFR3004](#ofr3004) | error | audit | ASP.NET Web Forms |
| [OFR3005](#ofr3005) | error | audit | ASMX web services |
| [OFR3006](#ofr3006) | error | audit | WCF service host |
| [OFR3007](#ofr3007) | error | audit | .NET Remoting |
| [OFR3008](#ofr3008) | error | audit | WF (Windows Workflow Foundation) |
| [OFR3009](#ofr3009) | error | audit | COM+, Code Access Security, or AppDomain sandboxing |
| [OFR3010](#ofr3010) | warning | audit | project not compiled against the target |
| [OFR3011](#ofr3011) | info | audit | packages without target support left out |
| [OFR3101](#ofr3101) | warning | audit | culture-sensitive string operation |
| [OFR3102](#ofr3102) | warning | audit | non-Unicode code page |
| [OFR3103](#ofr3103) | warning | audit | path assumes Windows separators or folders |
| [OFR3104](#ofr3104) | warning | audit | time zone looked up by Windows ID |
| [OFR3105](#ofr3105) | warning | audit | registry access |
| [OFR3106](#ofr3106) | warning | audit | ambient ASP.NET context |
| [OFR3107](#ofr3107) | warning | audit | shell execution through Process.Start |
| [OFR3108](#ofr3108) | warning | audit | legacy SQL Server client (System.Data.SqlClient) |
| [OFR3109](#ofr3109) | info | audit | floating-point ToString without a format |
| [OFR3110](#ofr3110) | warning | audit | obsolete networking API |
| [OFR3111](#ofr3111) | warning | audit | ambient principal |
| [OFR3112](#ofr3112) | info | audit | settings read through ConfigurationManager |
| [OFR3113](#ofr3113) | info | audit | regular expression without a timeout |
| [OFR3114](#ofr3114) | warning | audit | TLS 1.0/1.1 or SSL pinned |
| [OFR3115](#ofr3115) | warning | audit | assembly loading |
| [OFR3116](#ofr3116) | info | audit | runtime settings in app.config |
| [OFR3117](#ofr3117) | info | audit | URL encoding differences |
| [OFR3118](#ofr3118) | warning | audit | machine key or Forms authentication |
| [OFR3119](#ofr3119) | info | audit | timers and thread pool tuning |
| [OFR3120](#ofr3120) | info | audit | operating system check |
| [OFR3201](#ofr3201) | error | audit | insecure serializer |
| [OFR3202](#ofr3202) | info | audit | transient binary serialization (deep clone) |
| [OFR3203](#ofr3203) | error | audit | persisted or transported binary serialization |
| [OFR3204](#ofr3204) | info | audit | type serialized with a binary formatter |
| [OFR3205](#ofr3205) | info | audit | [Serializable] type never serialized |
| [OFR3210](#ofr3210) | warning | audit | legacy JSON serializer |
| [OFR3211](#ofr3211) | info | audit | XML serializer |
| [OFR3301](#ofr3301) | info | audit | P/Invoke declaration |
| [OFR3302](#ofr3302) | warning | audit | ANSI string marshalling by default |
| [OFR3303](#ofr3303) | info | audit | candidate for [LibraryImport] |
| [OFR3310](#ofr3310) | warning | audit | COM interop |
| [OFR3320](#ofr3320) | info | audit | structured exception interop |
| [OFR3401](#ofr3401) | info | audit | dead-code candidates |
| [OFR3402](#ofr3402) | info | audit | production code used only by tests |
| [OFR3501](#ofr3501) | warning | audit | public API differs between targets |
| [OFR3502](#ofr3502) | warning | audit | public API differs from the baseline |
| [OFR3503](#ofr3503) | error | audit | nothing to compare |
| [OFR3504](#ofr3504) | error | audit | API comparison could not run |
| [OFR3601](#ofr3601) | warning | audit | member cannot be wrapped in `#if` |
| [OFR3602](#ofr3602) | warning | audit | finding does not match the source |
| [OFR3603](#ofr3603) | warning | audit | conditional region depends on other symbols |
| [OFR3604](#ofr3604) | error | audit | findings file missing or invalid |
| [OFR4001](#ofr4001) | warning | seams | no seam found |
| [OFR4002](#ofr4002) | warning | seams | seam member not wire-friendly |
| [OFR4003](#ofr4003) | warning | seams | static member on the boundary |
| [OFR4010](#ofr4010) | warning | seams | caller instantiates concrete type directly |
| [OFR4011](#ofr4011) | error | seams | extract type not found |
| [OFR4012](#ofr4012) | error | seams | extraction would not compile |
| [OFR4013](#ofr4013) | error | seams | seam not found |
| [OFR4020](#ofr4020) | warning | seams | sync member over remote boundary |
| [OFR4021](#ofr4021) | error | seams | generated project directory exists |
| [OFR4022](#ofr4022) | info | seams | host falls back to net48 |
| [OFR4023](#ofr4023) | error | seams | remote interface not found |
| [OFR4101](#ofr4101) | warning | service | pause, continue, or custom commands differ |
| [OFR4102](#ofr4102) | warning | service | code left in compatibility region |
| [OFR4103](#ofr4103) | info | service | multiple services in one executable |
| [OFR4104](#ofr4104) | warning | service | service depends on other Windows services |
| [OFR4105](#ofr4105) | warning | service | session change or power events |
| [OFR4106](#ofr4106) | warning | service | generated worker does not compile |
| [OFR4107](#ofr4107) | error | service | no service found |
| [OFR4108](#ofr4108) | error | service | worker directory exists |
| [OFR4201](#ofr4201) | warning | web | Web Forms page not ported |
| [OFR4202](#ofr4202) | info | web | HttpHandler became an endpoint stub |
| [OFR4203](#ofr4203) | error | web | scaffolded project does not compile |
| [OFR4204](#ofr4204) | error | web | output folder not empty |
| [OFR4301](#ofr4301) | warning | csproj | compile items kept explicit |
| [OFR4302](#ofr4302) | info | csproj | build step converted for review |
| [OFR4303](#ofr4303) | error | csproj | converted project compiles different inputs |
| [OFR4304](#ofr4304) | warning | csproj | project not converted |
| [OFR4401](#ofr4401) | warning | config convert | setting not representable |
| [OFR4402](#ofr4402) | warning | config convert | WCF configuration |
| [OFR4403](#ofr4403) | info | config convert | system.web settings belong to the web migration |
| [OFR4404](#ofr4404) | warning | config convert | config transform not expressible as overrides |
| [OFR4405](#ofr4405) | error | config convert | no configuration file |
| [OFR4406](#ofr4406) | error | config convert | output file exists |
| [OFR4501](#ofr4501) | info | codemod | codemod site skipped |
| [OFR4502](#ofr4502) | error | codemod | unknown codemod |
| [OFR4503](#ofr4503) | error | codemod | codemod is experimental |
| [OFR4504](#ofr4504) | error | codemod | source changed since the last scan |
| [OFR4505](#ofr4505) | warning | codemod | package not added |
| [OFR4506](#ofr4506) | warning | codemod | project does not reference Offramp.Analyzers |
| [OFR4507](#ofr4507) | error | codemod | verification failed; codemod rolled back |
| [OFR4508](#ofr4508) | error | codemod | dotnet format failed |
| [OFR4510](#ofr4510) | info | codemod | connections encrypted by default (Microsoft.Data.SqlClient) |
| [OFR5001](#ofr5001) | error | verify | verification failed |
| [OFR5002](#ofr5002) | error | verify | verification timed out |
| [OFR5010](#ofr5010) | warning | verify | new error code relative to baseline |
| [OFR5020](#ofr5020) | warning | verify | finding from the verification command |
| [OFR5090](#ofr5090) | info | verify | verification skipped by configuration |

### OFR0001

**workspace model missing** · error · workspace

The command needs the workspace model (`.offramp/workspace.json`) and it does not exist.

- **Typical cause:** `offramp scan` has not been run in this repository, or `--workspace` points somewhere else.
- **Fix:** Run `offramp scan`. `doctor` reports the same condition as a warning.

### OFR0002

**workspace model stale** · warning · workspace

Files the workspace model was built from (project files, `Directory.*.props/targets`, solutions, `packages.config`, or the log it was read from) changed since the last `scan`.

- **Typical cause:** Edits, a branch switch, or a pull since the model was built.
- **Fix:** Run `offramp scan` (or `offramp scan --if-stale`). `--fail-on-stale` turns this into an error.

### OFR0003

**no binary log to reuse** · error · scan

`scan --no-build` reuses the binary log of the previous scan, and there is none.

- **Typical cause:** No earlier `offramp scan`, or the state directory was cleaned.
- **Fix:** Run `offramp scan` without `--no-build`, or pass `--binlog PATH`.

### OFR0004

**log file not found or unreadable** · error · scan

The binary log or compiler log passed to `scan` does not exist or is not a valid log.

- **Typical cause:** A wrong path, a truncated download, or a file that is not an MSBuild binary log or compiler log.
- **Fix:** Pass the path of an existing `.binlog` (from `dotnet build -bl`) or `.complog` (from `complog create`).

### OFR0010

**.NET SDK not found** · error · environment

`dotnet` could not be started, so nothing can be built, restored, or verified.

- **Typical cause:** No .NET SDK is installed, or `dotnet` is not on `PATH`.
- **Fix:** Install the .NET SDK for your `--target` from https://dot.net and make sure `dotnet --list-sdks` works.

### OFR0011

**SDK requested by global.json is not installed** · error · environment

`global.json` pins an SDK version that no installed SDK satisfies, so `dotnet` refuses to run in the repository.

- **Typical cause:** The pinned SDK was never installed on this machine, or `rollForward` is too strict.
- **Fix:** Install the SDK named in the message, or relax `sdk.rollForward` in `global.json`.

### OFR0012

**selected SDK cannot target the requested framework** · error · environment

The SDK that `dotnet` selects in this repository is older than the `--target` framework, so it cannot build `netN.0` projects.

- **Typical cause:** An older SDK is pinned in `global.json`, or only older SDKs are installed.
- **Fix:** Install the .NET SDK for the target and, if `global.json` pins an older one, update it.

### OFR0013

**.NET Framework reference assemblies not resolvable** · error · environment

`Microsoft.NETFramework.ReferenceAssemblies` is neither in the NuGet global packages folder nor available from any configured feed, so `net4x` targets cannot compile outside Windows.

- **Typical cause:** An offline machine with an empty package cache, or a `nuget.config` that removes nuget.org without a mirror of the package.
- **Fix:** Add a feed that carries `Microsoft.NETFramework.ReferenceAssemblies`, or restore once on a connected machine.

### OFR0014

**git not found** · warning · environment

`git` could not be started. Moves fall back to plain file moves, which git later sees as delete plus add unless it detects the rename.

- **Typical cause:** git is not installed or not on `PATH`.
- **Fix:** Install git so moves are staged with `git mv`.

### OFR0015

**not a git repository** · warning · environment

The repository root is not inside a git work tree, so moves are plain file moves and nothing is staged.

- **Typical cause:** Offramp was run outside a clone, or in an exported source tree.
- **Fix:** Run Offramp inside a git work tree to get staged, reviewable renames.

### OFR0016

**no offramp.yml; built-in defaults in effect** · info · configuration

No configuration file was found at the repository root, so every setting has its built-in default.

- **Typical cause:** `offramp init` has not been run.
- **Fix:** Run `offramp init` to write `offramp.yml` with detected values.

### OFR0020

**more than one solution found** · error · workspace

The repository contains several solution files and none was chosen, so Offramp cannot tell which one to work on.

- **Typical cause:** A repository with several `.sln`, `.slnx`, or `.slnf` files and no `solution:` in `offramp.yml`.
- **Fix:** Pass `--solution PATH` or set `solution:` in `offramp.yml`. `init` reports this as a warning and leaves `solution:` empty.

### OFR0021

**project not in the workspace model** · error · workspace

A project named on the command line is not part of the scanned solution.

- **Typical cause:** A typo, a path relative to another directory, or a project outside the solution or slice.
- **Fix:** Use a repository-relative project path as listed by `offramp scan --json` (`result.projects`), or rescan the right solution.

### OFR0022

**no solution found** · error · workspace

`scan` needs a solution to build and none was given or found in the repository.

- **Typical cause:** A repository without `.sln`/`.slnx` files, or one where the solution lives outside the repository root.
- **Fix:** Pass `--solution PATH`, set `solution:` in `offramp.yml`, or pass `--binlog`/`--complog` from a build made elsewhere.

### OFR0030

**offramp.yml already exists** · error · configuration

`init` did not write the configuration because the file already exists.

- **Typical cause:** `init` was run twice.
- **Fix:** Edit the existing file, or re-run with `--force` to replace it.

### OFR0050

**unknown key in offramp.yml** · warning · configuration

A key in `offramp.yml` is not part of the configuration schema and is ignored.

- **Typical cause:** A typo (`verfiy:`), a key at the wrong nesting level, or a key from a newer Offramp version.
- **Fix:** Fix or remove the key. `schemas/v1/config.json` lists every valid key.

### OFR0051

**pin without a reason** · warning · configuration

A `deps.pins` entry has no `reason`, so nobody can tell later why the version is held back.

- **Typical cause:** A pin added without documentation.
- **Fix:** Add `reason:` to the pin.

### OFR0052

**rule override without a reason** · info · configuration

A `rules:` severity override has no `reason`, so the suppression is not attributable.

- **Typical cause:** An override added without documentation.
- **Fix:** Add `reason:` to the override.

### OFR0053

**invalid value in offramp.yml** · error · configuration

A value in `offramp.yml` has the wrong type or is not one of the allowed values. The command stops because the configuration is ambiguous.

- **Typical cause:** For example `target: ten`, or `verify: { mode: compile }`.
- **Fix:** Correct the value; the message names the allowed values. `offramp doctor` lists every problem at once.

### OFR0054

**offramp.yml is not valid YAML** · error · configuration

The configuration file could not be parsed.

- **Typical cause:** A YAML syntax error such as bad indentation or an unclosed quote.
- **Fix:** Fix the syntax at the reported line and column.

### OFR0055

**configuration file not found** · error · configuration

The configuration file named by `--config` or `OFFRAMP_CONFIG` does not exist.

- **Typical cause:** A wrong path, or a path relative to a different working directory.
- **Fix:** Pass an existing file, or drop the option to use `offramp.yml` at the repository root.

### OFR0056

**invalid configuration value from the environment** · error · configuration

An `OFFRAMP_*` environment variable has a value that does not fit the setting it maps to.

- **Typical cause:** For example `OFFRAMP_TARGET=ten` or `OFFRAMP_VERIFY__MODE=compile`.
- **Fix:** Correct or unset the environment variable named in the message.

### OFR0099

**internal error** · error · cli

Offramp hit an unexpected exception. This is a bug in Offramp, not in your repository.

- **Typical cause:** A defect in Offramp.
- **Fix:** Re-run with `--verbose` for the stack trace and report it at https://github.com/Andorbal/offramp/issues.

### OFR0101

**project could not be loaded** · warning · project loading

A project listed in the solution has no usable evaluation in the build log, so it is missing from the model. The message carries the reason.

- **Typical cause:** An unsupported project type (for example `.vcxproj` or `.wixproj`), an evaluation error such as a missing SDK or import, or a project filtered out of the build.
- **Fix:** Fix the evaluation error the message names, or exclude the project from the solution filter you scan.

### OFR0102

**project kind unknown** · info · project loading

None of the kind rules matched (for example `OutputType=WinExe` without Windows Forms or WPF), so the project's kind is `unknown`.

- **Typical cause:** An unusual output type or a project Offramp does not recognize.
- **Fix:** Set the kind in `offramp.yml`: `projects: [{ path: ..., kind: console }]`.

### OFR0103

**model built from a compiler log alone** · info · scan

A compiler log records compiler invocations only, so the model lacks what MSBuild evaluation provides: package references and versions, the SDK, test-project detection, Windows-only build steps, and central package management settings.

- **Typical cause:** `scan --complog` without the binary log the compiler log was made from.
- **Fix:** Copy the binary log from the machine that produced the compiler log and run `offramp scan --binlog build.binlog --complog build.complog`.

### OFR0104

**package graph unavailable** · warning · project loading

The project's `project.assets.json` does not exist in this checkout, so its resolved packages (`resolved`) are missing from the model.

- **Typical cause:** Scanning a log built on another machine or in another checkout without restoring here, or a restore that failed.
- **Fix:** Run `dotnet restore` on the solution, then scan again.

### OFR0110

**build step needs Windows: sgen** · warning · project loading

`GenerateSerializationAssemblies` runs sgen, which loads the built assembly under the .NET Framework runtime; the build fails outside Windows (MSB3474).

- **Typical cause:** `<GenerateSerializationAssemblies>On</GenerateSerializationAssemblies>` in the project or an imported props file.
- **Fix:** Add the compile-only block to `Directory.Build.props` (`offramp doctor --fix --apply`), which turns sgen off outside Windows; on modern .NET use `Microsoft.XmlSerializer.Generator` or drop it.

### OFR0111

**build step needs Windows: COM reference** · warning · project loading

`COMReference` items are imported with the type library importer, which only exists on Windows (MSB4803 elsewhere).

- **Typical cause:** A COM type library referenced from the project.
- **Fix:** Reference the generated interop assembly as a file, or build the project only on Windows and analyze it from a compiler log captured there.

### OFR0112

**build step needs Windows: EDMX EntityDeploy** · warning · project loading

`EntityDeploy` items embed an Entity Framework 6 designer model with a build task that ships with Visual Studio.

- **Typical cause:** An `.edmx` model in the project.
- **Fix:** Move to code-first mappings, or use the compiler-log route for this project.

### OFR0113

**build step needs Windows: T4 or Fakes** · warning · project loading

T4 templates transformed at build time (`TransformOnBuild`, TextTemplating targets) or Microsoft Fakes assemblies need Visual Studio build targets.

- **Typical cause:** `TransformOnBuild=true`, an import of `Microsoft.TextTemplating.targets`, or `Fakes` items.
- **Fix:** Check the generated output in and turn build-time transformation off, or run those builds on Windows.

### OFR0114

**build step needs Windows: SSDT** · warning · project loading

SQL Server Data Tools projects (`.sqlproj`) build with Windows-only targets.

- **Typical cause:** A classic SSDT database project in the solution.
- **Fix:** Move to `MSBuild.Sdk.SqlProj`, which builds cross-platform, or exclude the project from scans outside Windows.

### OFR0115

**build step needs Windows: build event calling a Windows executable** · warning · project loading

A pre- or post-build event runs a Windows command (`.exe`, `.bat`, `xcopy`, `%VAR%`, ...), which fails elsewhere.

- **Typical cause:** A `PreBuildEvent`/`PostBuildEvent` written for cmd.exe.
- **Fix:** Guard the event with `Condition="'$(OS)' == 'Windows_NT'"` or `'$(OfframpCompileOnly)' != 'true'`, or replace it with MSBuild tasks.

### OFR0120

**project reference cycle** · warning · project loading

Projects depend on each other in a loop, through `ProjectReference` items or `HintPath` references to each other's build output. The message shows the loop.

- **Typical cause:** A `HintPath` to another project's `bin` folder added to work around a build order problem.
- **Fix:** Break the loop: extract the shared code into a new project, or replace the `HintPath` with a `ProjectReference` in one direction only.

### OFR0130

**analysis build failed; model partial** · error · scan

The build `scan` ran (or the log it read) has errors, so some projects have no compiler call. The model is written anyway; affected projects are marked `partial: true`.

- **Typical cause:** A compile error, a missing SDK or package, or a Windows-only build step on macOS or Linux.
- **Fix:** Fix the first errors listed, add the compile-only block for Windows-only steps (`offramp doctor --fix`), or scan a log captured on Windows.

### OFR0131

**analysis build timed out** · error · scan

The build `scan` ran did not finish within `verify.timeoutSeconds`.

- **Typical cause:** A very large solution, or a build step waiting for input.
- **Fix:** Raise `verify.timeoutSeconds`, scan a solution filter (`offramp slice`), or pass a binary log built elsewhere with `--binlog`.

### OFR0132

**compiler calls unavailable for some projects** · warning · scan

Some compiler invocations are missing from the compiler log: their inputs were missing when the binary log was converted, or the binary log was captured in another checkout or on another machine and cannot be converted here. Semantic commands skip the projects without one.

- **Typical cause:** Converting a binary log that was built on another machine, or a build that did not compile every project.
- **Fix:** Convert the binary log to a compiler log on the machine that built it (`complog create`), then scan with `--binlog` and `--complog`.

### OFR0201

**graph too large for Mermaid** · info · graph/report

The Mermaid graph has more than 300 projects; Mermaid renderers become slow and unreadable at that size.

- **Typical cause:** `graph --format mermaid` on a large solution without a focus or kind filter.
- **Fix:** Narrow the view with `--focus PROJECT --depth N` or `--exclude-kind test`, or use `--format html`.

### OFR0202

**ledger file is not a snapshot** · warning · graph/report

A JSON file in the ledger directory could not be read as a ledger snapshot, so the report leaves it out of the burn-down.

- **Typical cause:** A hand-edited or truncated snapshot, a merge conflict left in a committed snapshot, or an unrelated JSON file in `report.ledger`.
- **Fix:** Fix or delete the file (snapshots are regenerated by `offramp scan`), or keep other files outside the ledger directory.

### OFR1001

**no package version supports the target** · error · deps

No published version of the package has assets compatible with the target framework, so the projects using it cannot move to the target with it.

- **Typical cause:** A package that only ever shipped .NET Framework assets (for example Microsoft.AspNet.WebApi.Core).
- **Fix:** Replace the package with its successor (the message names one when rules/package-map.yml or deps.packageMap knows it), or isolate the code that uses it behind a seam.

### OFR1002

**in-use version does not support the target** · warning · deps

A version of the package in use has no assets for the target framework, but a newer version does.

- **Typical cause:** An old version that predates the package's .NET Standard or modern .NET support.
- **Fix:** Upgrade to the version the message names or later (`deps consolidate` picks one version for the solution).

### OFR1003

**package deprecated** · warning · deps

The feed marks the package, or the version in use, as deprecated; the message carries the reasons and the alternate the feed suggests.

- **Typical cause:** A package its authors no longer maintain (reason Legacy), or one with critical bugs.
- **Fix:** Move to the alternate the feed suggests, or record the decision to keep it.

### OFR1004

**package assets are Windows-only** · warning · deps

The assets NuGet would pick for the target are marked [SupportedOSPlatform("windows")] or reference Windows-only assemblies (Windows Forms, WPF, System.Web, System.Drawing, the registry, directory services).

- **Typical cause:** A package that wraps Windows APIs, such as System.Drawing.Common on .NET 6 and later.
- **Fix:** Fine if the application stays on Windows; otherwise choose a cross-platform alternative before containerizing.

### OFR1005

**package not found on any feed** · warning · deps

None of the configured feeds has the package, so its support for the target is unknown.

- **Typical cause:** A private package on a feed missing from nuget.config, or a package removed from its feed.
- **Fix:** Add the feed to nuget.config (or `deps.feeds`), or ignore the package with `deps.ignore`.

### OFR1006

**feed unreachable; result partial** · warning · deps

A NuGet feed could not be queried, so any answer that depends on it is incomplete.

- **Typical cause:** No network, a feed that is down, or missing credentials for a private feed.
- **Fix:** Check `nuget.config`, network access, and credential providers, then re-run.

### OFR1200

**package not referenced** · error · deps

`deps consolidate --package` names a package no project in the workspace model references directly.

- **Typical cause:** A typo, a package that only arrives transitively, or a model scanned before the reference was added.
- **Fix:** Check the id (`offramp deps audit` lists them), or run `offramp scan` again.

### OFR1203

**pin kept a package below the otherwise-selected version** · warning · deps

A pin in offramp.yml keeps a project on an older version than the one the rest of the solution consolidates to; under central package management the project gets `VersionOverride`.

- **Typical cause:** A deliberate pin (its reason is quoted).
- **Fix:** Nothing, while the pin's reason holds; remove the pin to consolidate the project too.

### OFR1210

**pin conflicts with a transitive lower bound** · error · deps

A pinned version is lower than a range another package in the same graph asks for, so restore would report a downgrade (NU1605). The chain from the direct reference to the range is attached.

- **Typical cause:** A pin older than what a dependency now requires.
- **Fix:** Isolate the pinned project from that dependency, raise the pin, or add `NoWarn NU1605` to that project with a recorded reason.

### OFR1211

**restore verification failed** · error · deps

`dotnet restore` of the proposed project files, in a scratch copy of the repository, reported NU1605 (downgrade), NU1107 (version conflict), NU1608 (outside a dependency's range), NU1010 (missing PackageVersion), or a restore error that the current files do not. Nothing was applied; the warnings are quoted verbatim.

- **Typical cause:** A constraint the workspace model does not show (a conditional reference, a package's own dependencies at the new version, an SDK-implicit reference).
- **Fix:** Read the quoted warnings; pin or consolidate the package they name, then run again.

### OFR1212

**no version satisfies every constraint** · error · deps

No published version of the package is at least every lower bound, within every upper bound, and supports every target framework of the projects that reference it. The package keeps its versions.

- **Typical cause:** An upper bound from one dependency below the lower bound from another, or a package whose newer versions dropped a framework still in use.
- **Fix:** Read the attached constraints; upgrade or replace the package that imposes the bound, or split the projects.

### OFR1220

**family member lacks the family version** · warning · deps

A package in a `deps.families` family has no published version equal to the family's (the highest member version), so it keeps its own consolidated version.

- **Typical cause:** Families whose members version independently, or a member discontinued before the family's version.
- **Fix:** Narrow the family prefix, or replace the member.

### OFR1301

**project outside the solution would inherit CPM** · warning · deps

A project file that is not part of the scanned solution sits below a `Directory.Packages.props`, so central package management applies to it too, and its `PackageReference` versions stop working.

- **Typical cause:** A monorepo with unrelated projects under the same root as the migrating solution.
- **Fix:** Use a non-default file name for the central versions (`deps.cpm.file`) and opt the solution's projects in with `DirectoryPackagesPropsPath`, as `deps consolidate` does when this fires.

### OFR1302

**nested Directory.Packages.props shadows the root** · warning · deps

A `Directory.Packages.props` below the root one is found first by the projects under it and does not import the root file, so they see different versions.

- **Typical cause:** A copied props file in a subfolder.
- **Fix:** Import the parent file (`<Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))" />`) or delete the nested file.

### OFR1303

**packages.config project cannot use CPM** · warning · deps

The project still uses `packages.config`, which central package management does not apply to.

- **Typical cause:** A legacy project not yet migrated to `PackageReference`.
- **Fix:** Migrate the project to `PackageReference` (`offramp csproj modernize`, or Visual Studio's migration).

### OFR1401

**loose DLL is another project's output** · info · deps

A `Reference` with a `HintPath` points at a DLL whose assembly name is a project's in the solution; a `ProjectReference` builds it instead of trusting a copied file.

- **Typical cause:** A project's output copied into a lib folder before the projects shared a solution.
- **Fix:** Apply `deps resolve-dlls`, which swaps the reference.

### OFR1402

**loose DLL matched to a package** · info · deps

A `Reference` with a `HintPath` points at a DLL that a package ships (same assembly name and public key, at the referenced version or higher, for every target framework of the project).

- **Typical cause:** A package's DLL copied into a lib folder by hand.
- **Fix:** Apply `deps resolve-dlls`, which swaps the reference for a `PackageReference`.

### OFR1403

**loose DLL unmatched** · warning · deps

No project builds the DLL and no package named like the assembly ships it. Its metadata (version, target framework, public key token) is attached for a person to decide.

- **Typical cause:** A vendor or in-house DLL with no package, or a package whose id differs from the assembly name.
- **Fix:** Find the package or source it came from, or publish it to a private feed.

### OFR1404

**loose Framework-only DLL with no replacement** · error · deps

A DLL referenced by `HintPath` is built for .NET Framework, and no project or package replaces it, so the project cannot move to the target while it depends on it.

- **Typical cause:** A vendor library that never shipped for .NET Standard or modern .NET.
- **Fix:** Ask the vendor for a modern build, replace the library, or isolate its use behind a seam (`offramp seams`).

### OFR1501

**binding redirect added** · info · deps

Assemblies in the application's package graph reference a version of the assembly other than the one deployed, and the configuration file has no redirect for it; `redirects sync` adds one to the deployed version.

- **Typical cause:** A package upgrade or consolidation.
- **Fix:** Nothing; review the diff and apply.

### OFR1502

**binding redirect changed** · info · deps

An existing redirect's range or target does not match the graph (the deployed version, from 0.0.0.0 up to the highest version referenced); `redirects sync` updates it.

- **Typical cause:** A package upgrade or consolidation after the redirect was written.
- **Fix:** Nothing; review the diff and apply.

### OFR1503

**binding redirect pruned** · info · deps

With `--prune`, a redirect for an assembly no package in the application's graph provides is removed.

- **Typical cause:** A package removed, or a redirect copied from another application.
- **Fix:** Nothing; review the diff and apply.

### OFR1504

**stale binding redirect** · warning · deps

A redirect names an assembly no package in the application's graph provides, so it redirects to a version the build does not deploy. It is kept unless `--prune` is given.

- **Typical cause:** A package removed, a redirect copied from another application, or a redirect for a framework assembly.
- **Fix:** Run `offramp redirects sync --prune`, or keep the redirect when a framework assembly needs it.

### OFR2001

**move would create a project reference cycle** · warning · move

The file needs a project that depends on the destination, so the destination cannot reference it; the file stays. The cycle path is attached.

- **Typical cause:** Moving code into a lower layer while it still uses a higher one.
- **Fix:** Move the needed code down first, or leave the file; the path shows which reference closes the cycle.

### OFR2002

**destination equals source** · error · move

The move's destination project is the source project itself.

- **Typical cause:** `--to` naming the source project, or a naming rule that resolves to it.
- **Fix:** Name a different destination with `--to`.

### OFR2003

**project is frozen** · error · move

`projects[].frozen` in `offramp.yml` marks the source or destination as frozen: nothing moves into or out of it, and its project file is never edited.

- **Typical cause:** A project owned by another team, or a generated one.
- **Fix:** Choose another project, or remove `frozen` if the freeze no longer applies.

### OFR2004

**file is not part of the source project** · error · move

A file named for the move is not compiled by the source project (nor a .resx beside its files).

- **Typical cause:** A path relative to another folder, a file excluded from the project, or the wrong `--from`.
- **Fix:** Pass repository-relative paths of files the source project compiles.

### OFR2005

**move plan file missing or invalid** · error · move

`move apply --plan` could not read the plan: the file does not exist, is not JSON, or is not a `move-plan.json` document.

- **Typical cause:** A wrong path, or a plan edited by hand into invalid JSON.
- **Fix:** Check the path, or write the plan again with `offramp move plan ... --out PATH`.

### OFR2006

**nothing to extract** · error · move

`move extract` found nothing for a `--types` name (no type of that name in the source project, or several) or a `--files` pattern (no compiled file matches).

- **Typical cause:** A misspelled or partial type name, a type from another project, or a pattern relative to the wrong folder.
- **Fix:** Name types fully qualified (`Ns.Type`) and write `--files` patterns relative to the source project's folder.

### OFR2007

**new project already exists** · error · move

`move extract` creates its project, and the project file or its folder already exists (or the workspace model has a project there). Nothing was planned.

- **Typical cause:** A second extract with the same `--new`, or a folder with other files in it.
- **Fix:** Choose another `--new` or `--dir`, or move the files into the existing project with `move plan --to`.

### OFR2008

**new project's target references did not resolve** · error · move

`move extract` compiles the new project in memory for each of its target frameworks; for one of them, the SDK or NuGet could not resolve the reference assemblies, so nothing was planned.

- **Typical cause:** A target framework the installed SDK does not know, or no access to the NuGet feed that has its reference packs.
- **Fix:** Check `--tfm`, install the SDK for it, or restore once with network access.

### OFR2050

**verification failed; changes rolled back** · error · move

The build (or verification command) failed after the move, and `verify.onFailure: rollback` undid it from the journal: renames reversed, project files restored byte for byte, new files deleted.

- **Typical cause:** Moved code that compiles in isolation but breaks the solution build, a test project that does not restore, or an unrelated broken build.
- **Fix:** Read the verification errors in the result; fix them or narrow the move, then run it again. `verify.onFailure: keep` leaves a failed move in place for inspection.

### OFR2101

**file needs a co-move** · warning · move

The file uses code declared in another file of the source project that is not moving (with `--co-move none`), or that cannot move, so the file stays.

- **Typical cause:** Moving part of a cluster of files that use each other.
- **Fix:** Add the needed files to the move, or use `--co-move closure` (the default).

### OFR2102

**required package unavailable for destination** · warning · move

The file uses a package with no compile assets for one of the destination's target frameworks, so the file stays.

- **Typical cause:** A .NET Framework-only package used by code moving to a .NET Standard or modern project.
- **Fix:** Find a package version or replacement that supports the destination (`offramp deps audit`), then plan again.

### OFR2103

**file does not compile in the destination** · warning · move

A trial compilation of the file in the destination project, with the references the move would add, reports errors, so the file stays where it is.

- **Typical cause:** The file uses an assembly the destination does not reference and Offramp cannot add (a .NET Framework reference), different preprocessor symbols or implicit usings, or code that would stay behind.
- **Fix:** Add the missing reference to the destination (a project file change in its own pull request), then plan the move again.

### OFR2104

**source still depends on moved code** · error · move

Without the moved files the source project no longer compiles, and it cannot reference the destination (that would be a cycle), so nothing moves.

- **Typical cause:** Production code using a test or helper in a way the analysis could not see, such as through a generated file.
- **Fix:** Look at the source errors in the details, move the used code out of the test files, and plan again.

### OFR2105

**moved file uses Windows-only APIs** · warning · move

The destination's platform analyzer (CA1416) reports Windows-only APIs in the file for a modern non-Windows target. The file still moves.

- **Typical cause:** Registry, WMI, System.Drawing, or other Windows-only APIs in code moving to a cross-platform project.
- **Fix:** Guard the calls with `OperatingSystem.IsWindows()` or mark the code `[SupportedOSPlatform("windows")]` in a separate change.

### OFR2110

**partial type co-moved** · info · move

The file declares part of a partial type that a moving file also declares, so they move together.

- **Typical cause:** Partial classes split across files (generated code, large types).
- **Fix:** Nothing to do; the files move as one.

### OFR2111

**destination excludes the file path** · warning · move

The destination's project file removes the path the file would move to from its Compile items, so the file stays.

- **Typical cause:** A `<Compile Remove="..." />` glob in the destination covering the moved folder.
- **Fix:** Adjust the destination's Remove pattern in a separate change, then plan again.

### OFR2120

**namespace differs from destination root namespace** · warning · move

The file declares a namespace outside the destination's root namespace. Moves never edit namespaces; `--namespace-mismatch block` keeps such files.

- **Typical cause:** Code moving between projects whose namespaces follow their names.
- **Fix:** Keep the namespace (namespaces are not bound to projects), or rename it in a separate change.

### OFR2150

**file changed since plan** · warning · move

A planned file no longer matches the plan (its contents changed, it is gone, or its destination is taken), so `move apply` leaves it, and every planned file that needs it, where it is.

- **Typical cause:** Edits made between `move plan` and `move apply`.
- **Fix:** Plan again.

### OFR2151

**file changed since the move; rollback stopped** · error · move

A file the move wrote (a moved file, an edited project file, or a new file) changed after the move, so undoing it would lose that change. Nothing was rolled back.

- **Typical cause:** Edits made after `move tests --apply`, or a second move over the same files.
- **Fix:** Undo the later changes first (for example `git stash`), then roll back; or leave the move in place.

### OFR2152

**interrupted move cannot be resumed** · error · move

`move apply --resume` found no interrupted journal for the plan, or a file the journal still has to write is neither as the journal expects nor as it would leave it.

- **Typical cause:** The run finished or was rolled back already; or files were edited, moved, or restored after the interruption.
- **Fix:** Check `.offramp/journal/`. Roll the interrupted journal back with `offramp move rollback --journal PATH` and apply again.

### OFR2201

**test code used by production code** · warning · move

A test or helper file is used by production code (in the project, or in a project other than the destination), so moving it would break that code.

- **Typical cause:** A test class with a method production code calls, a builder shared with production, or a helper another project uses.
- **Fix:** Split the production part out of the file, or leave it; the referrers are listed.

### OFR2202

**multiple candidate test projects** · error · move

More than one project is named after the source project plus `move.tests.targetSuffix`, so the destination is ambiguous.

- **Typical cause:** Test projects with the same name in different folders.
- **Fix:** Name the destination with `--to`.

### OFR2203

**no test project found** · error · move

No project is named after the source project plus `move.tests.targetSuffix`, and `--create` was not given.

- **Typical cause:** A production project whose tests never had a project of their own.
- **Fix:** Name an existing destination with `--to`, or pass `--create` to create `<Name>.Tests` next to the source.

### OFR2204

**destination path collision** · warning · move

The file's destination path already exists, or another moved file maps to it, so the file stays.

- **Typical cause:** A test file with the same relative path in both projects, or two files that differ only by a stripped `Tests` folder.
- **Fix:** Rename one of the files in a separate change, or set `move.tests.stripTestsSegment: false`.

### OFR2205

**project language not supported** · error · move

`move tests` analyzes C# projects; the source project is in another language.

- **Typical cause:** A Visual Basic or F# project.
- **Fix:** Move the tests by hand.

### OFR2206

**file outside the project folder** · warning · move

The file is compiled into the project through a link but lives outside the project's folder, so it has no place under the destination and stays.

- **Typical cause:** `<Compile Include="..\Common\X.cs" />` sharing a file between projects.
- **Fix:** Move the shared file by hand, or stop sharing it.

### OFR2210

**test-framework packages removable from source** · info · move

After the move, nothing left in the source project uses the test framework, so its test-framework package references can go.

- **Typical cause:** The last tests moved out of a production project.
- **Fix:** Run again with `--prune-packages`, or remove the references by hand.

### OFR2301

**string reference to a moved type** · warning · move

A string names a type that moved together with the assembly it moved out of (`"Ns.Type, Source"`), in a C# string literal or a configuration or data file. Type forwarders redirect compiled references, not strings resolved at run time.

- **Typical cause:** `Type.GetType("...")`, configuration sections, XAML, dependency-injection or serializer settings written before the move.
- **Fix:** Change the string to name the destination assembly, or keep it and rely on the forwarder only where the loader follows forwards.

### OFR2302

**revision not found** · error · move

`forwarders --since` names something that is not a commit in the repository, so the source's former public types cannot be read.

- **Typical cause:** A typo, a branch that exists only elsewhere, or a shallow clone without that history.
- **Fix:** Pass a commit, branch, or tag that exists locally (`git fetch` it first), or omit `--since` to use the last scan's compilation.

### OFR3001

**API missing on target** · error · audit

A type or member the project uses on .NET Framework does not exist in the target's reference assemblies (nor in the packages that support the target). The message names the API and its assembly's mapping from rules/framework-assemblies.yml.

- **Typical cause:** APIs from assemblies with no modern equivalent (System.Web), or in assemblies that moved to packages (System.Drawing.Common, System.Configuration.ConfigurationManager).
- **Fix:** See the assembly's mapping (`offramp deps gac`); replace the API or isolate it behind a seam.

### OFR3002

**API available only on Windows** · warning · audit

The API exists on the target but is marked [SupportedOSPlatform("windows")], so it throws or is missing on Linux and macOS.

- **Typical cause:** Registry access, Windows-only Console members, Windows event logs, and similar APIs that survived the port only for Windows.
- **Fix:** Target netN-windows, guard the call with OperatingSystem.IsWindows(), or isolate it behind a seam.

### OFR3003

**API throws on modern .NET** · error · audit

The API compiles on modern .NET but throws PlatformNotSupportedException at run time.

- **Typical cause:** Thread.Abort, AppDomain.CreateDomain, CodeDom compilation, delegate BeginInvoke, and BinaryFormatter without the compatibility switch.
- **Fix:** These compile but throw PlatformNotSupportedException; replace them (cooperative cancellation, AssemblyLoadContext, Roslyn, System.Text.Json).

### OFR3004

**ASP.NET Web Forms** · error · audit

The project uses ASP.NET Web Forms (System.Web.UI), which modern .NET does not have.

- **Typical cause:** Pages, user controls, and master pages deriving from System.Web.UI types.
- **Fix:** Web Forms has no port; rebuild pages in Razor Pages or Blazor (`offramp web inventory`), incrementally behind a YARP proxy.

### OFR3005

**ASMX web services** · error · audit

The project hosts ASMX web services, which modern .NET does not have.

- **Typical cause:** Classes deriving from WebService or marked [WebService]/[WebMethod].
- **Fix:** Move to ASP.NET Core controllers, or CoreWCF for SOAP clients that cannot change.

### OFR3006

**WCF service host** · error · audit

The project hosts WCF services, which modern .NET does not include (clients have packages; servers need CoreWCF).

- **Typical cause:** ServiceHost, ServiceHostFactory, or [ServiceBehavior] in the project.
- **Fix:** WCF clients have packages; servers move to CoreWCF, gRPC, or HTTP APIs.

### OFR3007

**.NET Remoting** · error · audit

.NET Remoting is gone on modern .NET.

- **Typical cause:** Types from System.Runtime.Remoting: MarshalByRefObject channels, RemotingConfiguration, remote activation.
- **Fix:** Remoting has no port; use gRPC, HTTP, or named pipes (`offramp remote`).

### OFR3008

**WF (Windows Workflow Foundation)** · error · audit

Windows Workflow Foundation is not part of modern .NET.

- **Typical cause:** Types from System.Activities or System.Workflow.
- **Fix:** Workflow Foundation has no port (CoreWF is a community option); isolate workflows behind a seam.

### OFR3009

**COM+, Code Access Security, or AppDomain sandboxing** · error · audit

Enterprise Services (COM+), Code Access Security, and sandboxed AppDomains are gone on modern .NET.

- **Typical cause:** System.EnterpriseServices components, CAS permission attributes, PermissionSet, AllowPartiallyTrustedCallers.
- **Fix:** COM+ services, CAS permissions, and sandboxed AppDomains are gone; isolate the code in a separate process.

### OFR3010

**project not compiled against the target** · warning · audit

The project could not be compiled against the target's reference assemblies, so audit api reports no missing (OFR3001) or Windows-only (OFR3002) APIs for it. The message carries NuGet's or MSBuild's error.

- **Typical cause:** The target's reference packs could not be restored (no network, a feed that requires authentication, an SDK too old for the target).
- **Fix:** Fix the restore error the message names (feeds, credentials, SDK version) and run the audit again.

### OFR3011

**packages without target support left out** · info · audit

Some of the project's packages have no assets for the target, so the target compilation leaves them out and the APIs used from them show up as missing (OFR3001).

- **Typical cause:** Packages that only ever shipped .NET Framework assemblies (for example Microsoft.AspNet.Mvc or Microsoft.Web.Infrastructure).
- **Fix:** Run `offramp deps audit` for replacements; the APIs used from these packages are the ones to port.

### OFR3101

**culture-sensitive string operation** · warning · audit

A string comparison, search, or case mapping uses the current culture implicitly; modern .NET uses ICU on every platform, which compares and matches differently from NLS on .NET Framework.

- **Typical cause:** string.Compare, IndexOf(string), StartsWith(string), ToUpper(), or OrderBy over strings without a StringComparison, CultureInfo, or comparer.
- **Fix:** Pass StringComparison.Ordinal (or a CultureInfo) explicitly; ICU on Linux and modern .NET compares and matches differently from NLS.

### OFR3102

**non-Unicode code page** · warning · audit

Encoding.GetEncoding asks for a legacy code page, which modern .NET only provides after CodePagesEncodingProvider is registered.

- **Typical cause:** Windows-1252, Shift-JIS, or other non-Unicode code pages requested by number or name.
- **Fix:** Register CodePagesEncodingProvider.Instance (System.Text.Encoding.CodePages) at startup before asking for legacy code pages.

### OFR3103

**path assumes Windows separators or folders** · warning · audit

A path uses Windows separators, drive letters, or a Windows-only special folder; other file systems use '/' and are case-sensitive.

- **Typical cause:** Hard-coded backslashes or drive letters passed to System.IO, Environment.SpecialFolder members that exist only on Windows.
- **Fix:** Use Path.Combine with relative segments and Path.DirectorySeparatorChar; file systems elsewhere are case-sensitive and use '/'.

### OFR3104

**time zone looked up by Windows ID** · warning · audit

A time zone is looked up by a Windows ID; other platforms use IANA IDs.

- **Typical cause:** TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time") or an ID read from data.
- **Fix:** Use IANA IDs, or TimeZoneInfo.TryConvertWindowsIdToIanaId (.NET 6+) where Windows IDs come from data.

### OFR3105

**registry access** · warning · audit

The code reads or writes the Windows registry.

- **Typical cause:** Microsoft.Win32.Registry and RegistryKey.
- **Fix:** Move settings to configuration; guard any remaining registry access with OperatingSystem.IsWindows().

### OFR3106

**ambient ASP.NET context** · warning · audit

The code relies on ASP.NET's ambient request or hosting context, which ASP.NET Core does not have.

- **Typical cause:** HttpContext.Current, HttpRuntime, HostingEnvironment.
- **Fix:** ASP.NET Core has no ambient context; pass HttpContext (or IHttpContextAccessor) and IWebHostEnvironment explicitly.

### OFR3107

**shell execution through Process.Start** · warning · audit

Process.Start with a file or URL relies on the shell; UseShellExecute defaults to false on modern .NET.

- **Typical cause:** Process.Start("https://...") or Process.Start("report.pdf").
- **Fix:** UseShellExecute defaults to false on modern .NET; open URLs and documents with new ProcessStartInfo(target) { UseShellExecute = true }.

### OFR3108

**legacy SQL Server client (System.Data.SqlClient)** · warning · audit

System.Data.SqlClient is superseded by Microsoft.Data.SqlClient, whose defaults differ (Encrypt is true from 4.0).

- **Typical cause:** SqlConnection, SqlCommand, and friends from System.Data.SqlClient.
- **Fix:** Move to Microsoft.Data.SqlClient; Encrypt defaults to true from 4.0, so connection strings may need TrustServerCertificate.

### OFR3109

**floating-point ToString without a format** · info · audit

double and float ToString() without a format give the shortest round-trippable string since .NET Core 3.0, so output can change.

- **Typical cause:** Formatting floating-point values for display, storage, or comparison without a format string.
- **Fix:** Since .NET Core 3.0, ToString() gives the shortest round-trippable string; pass a format ("G15", "R", "F2") where output is compared or stored.

### OFR3110

**obsolete networking API** · warning · audit

WebRequest, WebClient, and ServicePointManager are obsolete on modern .NET.

- **Typical cause:** HTTP calls through HttpWebRequest or WebClient, global settings through ServicePointManager.
- **Fix:** Use HttpClient (with SocketsHttpHandler settings instead of ServicePointManager).

### OFR3111

**ambient principal** · warning · audit

The ambient principal does not flow the same way on modern .NET.

- **Typical cause:** Thread.CurrentPrincipal used for authorization, WindowsIdentity.
- **Fix:** Thread.CurrentPrincipal does not flow the same way; ASP.NET Core uses HttpContext.User, services an explicit identity.

### OFR3112

**settings read through ConfigurationManager** · info · audit

ConfigurationManager settings come from the host's app.config through a compatibility package, not from web.config or IConfiguration.

- **Typical cause:** ConfigurationManager.AppSettings and ConnectionStrings.
- **Fix:** The System.Configuration.ConfigurationManager package reads the host's app.config, not web.config; consider IConfiguration (`offramp config convert`).

### OFR3113

**regular expression without a timeout** · info · audit

A regular expression runs without a match timeout.

- **Typical cause:** new Regex(pattern) or Regex.IsMatch(input, pattern) without a TimeSpan.
- **Fix:** Pass a match timeout (or RegexOptions.NonBacktracking) for patterns applied to request data.

### OFR3114

**TLS 1.0/1.1 or SSL pinned** · warning · audit

The code pins SSL 3.0, TLS 1.0, or TLS 1.1, which modern defaults reject.

- **Typical cause:** SslProtocols.Tls, SecurityProtocolType.Tls11, and similar values set explicitly.
- **Fix:** Let the operating system choose (SslProtocols.None / SecurityProtocolType.SystemDefault); old protocols are rejected by modern defaults.

### OFR3115

**assembly loading** · warning · audit

Assembly.LoadFrom, LoadFile, and AppDomain.AssemblyResolve follow AssemblyLoadContext rules on modern .NET.

- **Typical cause:** Plug-in loading and custom assembly probing.
- **Fix:** Assembly.LoadFrom/LoadFile and AppDomain.AssemblyResolve follow AssemblyLoadContext rules on modern .NET; review load contexts and probing.

### OFR3116

**runtime settings in app.config** · info · audit

Garbage collector and threading settings in app.config or web.config are ignored on modern .NET; they belong in runtimeconfig.json.

- **Typical cause:** <gcServer>, <gcConcurrent>, <GCCpuGroup>, and similar elements under <runtime>.
- **Fix:** GC and runtime settings move to runtimeconfig.json (or MSBuild properties such as ServerGarbageCollection).

### OFR3117

**URL encoding differences** · info · audit

HttpUtility and Uri.EscapeUriString escape differently from WebUtility and Uri.EscapeDataString.

- **Typical cause:** URL encoding whose output is persisted, compared, or signed.
- **Fix:** HttpUtility and Uri.EscapeUriString escape differently from WebUtility and Uri.EscapeDataString; compare outputs where they are persisted or signed.

### OFR3118

**machine key or Forms authentication** · warning · audit

MachineKey and Forms authentication have no direct equivalent; ASP.NET Core uses Data Protection.

- **Typical cause:** MachineKey.Protect/Unprotect, FormsAuthentication tickets and cookies.
- **Fix:** ASP.NET Core Data Protection replaces MachineKey; sharing Forms authentication cookies needs a compatibility adapter.

### OFR3119

**timers and thread pool tuning** · info · audit

Timers and thread pool tuning usually work, but services moving to containers should revisit them.

- **Typical cause:** System.Timers.Timer in services, ThreadPool.SetMinThreads.
- **Fix:** Usually fine; services heading to containers should prefer PeriodicTimer or hosted services, and revisit thread pool minimums.

### OFR3120

**operating system check** · info · audit

An operating system check assumes Windows.

- **Typical cause:** Environment.OSVersion or RuntimeInformation.IsOSPlatform branches.
- **Fix:** Branches that assume Windows need review; prefer OperatingSystem.IsWindows() and friends.

### OFR3201

**insecure serializer** · error · audit

BinaryFormatter and its relatives are insecure, and .NET 9 removed them (they throw).

- **Typical cause:** BinaryFormatter, SoapFormatter, NetDataContractSerializer, ObjectStateFormatter, LosFormatter.
- **Fix:** BinaryFormatter and its relatives are removed (throw) from .NET 9; migrate the data, using the System.Runtime.Serialization.Formatters compatibility package only while a dual-read migration runs.

### OFR3202

**transient binary serialization (deep clone)** · info · audit

A binary formatter round-trips an object through a MemoryStream in one member (a deep clone); the data never leaves the process.

- **Typical cause:** The Clone idiom: Serialize into a new MemoryStream, rewind, Deserialize.
- **Fix:** The data never leaves the process; replace the round trip with a copy constructor, a record `with`, or a System.Text.Json round trip (`offramp codemod`).

### OFR3203

**persisted or transported binary serialization** · error · audit

Binary-formatter data leaves the process: a file, a stream parameter or field, or a memory stream whose bytes are returned or stored.

- **Typical cause:** Persisting objects to disk, sending them over a network, caching them, or storing them in session state.
- **Fix:** Data written with BinaryFormatter outlives the process; plan a dual-read migration to a safe format before the target drops the formatter.

### OFR3204

**type serialized with a binary formatter** · info · audit

A type is serialized with a binary formatter; the finding lists whether it implements ISerializable, has deserialization callbacks, or holds delegates.

- **Typical cause:** Types passed to Serialize (followed through object parameters to call sites) or cast from Deserialize.
- **Fix:** Each listed type needs a new serialized shape; ISerializable, OnDeserialized hooks, and delegate members need attention.

### OFR3205

**[Serializable] type never serialized** · info · audit

A [Serializable] type is never passed to a binary formatter anywhere in the audited solution.

- **Typical cause:** Attributes added by habit or left over from removed serialization.
- **Fix:** No serializer in the solution receives it; the attribute can stay.

### OFR3210

**legacy JSON serializer** · warning · audit

JavaScriptSerializer and DataContractJsonSerializer are legacy JSON serializers.

- **Typical cause:** System.Web.Script.Serialization.JavaScriptSerializer, DataContractJsonSerializer.
- **Fix:** Move to System.Text.Json (or Newtonsoft.Json where its behaviors are relied on).

### OFR3211

**XML serializer** · info · audit

XmlSerializer works on modern .NET; pre-generated serializers (sgen) need Microsoft.XmlSerializer.Generator.

- **Typical cause:** XmlSerializer usages, especially with GenerateSerializationAssemblies.
- **Fix:** Works on the target; pre-generated serializers (sgen) need Microsoft.XmlSerializer.Generator or can be dropped.

### OFR3301

**P/Invoke declaration** · info · audit

An inventory entry for a P/Invoke declaration: library, entry point, calling convention, character set, SetLastError, marshalled types, and whether the library exists only on Windows.

- **Typical cause:** Any [DllImport] method.
- **Fix:** Check each library exists on every target platform; Windows system libraries do not.

### OFR3302

**ANSI string marshalling by default** · warning · audit

A P/Invoke marshals strings with the ANSI default (or CharSet.Auto, which is ANSI off Windows).

- **Typical cause:** [DllImport] with string, char, or StringBuilder parameters and no CharSet.Unicode or [MarshalAs].
- **Fix:** CharSet defaults to ANSI (Auto means Unicode only on Windows); declare CharSet.Unicode or marshal strings explicitly.

### OFR3303

**candidate for [LibraryImport]** · info · audit

A P/Invoke has a blittable signature, so [LibraryImport] can generate its marshalling at compile time.

- **Typical cause:** [DllImport] methods over integers, pointers, and handles only.
- **Fix:** A blittable signature can use [LibraryImport] for source-generated marshalling on .NET 7+.

### OFR3310

**COM interop** · warning · audit

The project uses COM, which exists only on Windows.

- **Typical cause:** COMReference items, [ComImport] interfaces, Marshal.GetActiveObject.
- **Fix:** COM works only on Windows; isolate it behind a seam or target netN-windows.

### OFR3320

**structured exception interop** · info · audit

Structured exception and HRESULT interop differ across platforms.

- **Typical cause:** Marshal.GetHRForException, catching SEHException.
- **Fix:** SEHException and HRESULT mapping differ across platforms; review the handling.

### OFR3401

**dead-code candidates** · info · audit

Types or members that nothing in the solution references, summarized per project; the result lists each with its confidence and the evidence for it.

- **Typical cause:** Code left behind by removed features, public helpers nobody calls, types only reflection or configuration reach.
- **Fix:** Delete the high-confidence candidates (the summary gives the lines that go away); check the evidence on medium and low ones first.

### OFR3402

**production code used only by tests** · info · audit

With `--include-tests`: a production type or member that only test projects reference.

- **Typical cause:** Test helpers and fakes kept in production assemblies, or features whose production callers were removed.
- **Fix:** Move it to the test project (`offramp move tests`) or delete it with its tests.

### OFR3501

**public API differs between targets** · warning · audit

ApiCompat found a type or member in one target framework's build of the project that the other target does not have, usually from an `#if`.

- **Typical cause:** Members wrapped in `#if NETFRAMEWORK` (or the modern equivalent) while callers expect them on every target.
- **Fix:** Give the member an implementation on both targets, or confirm no caller outside the one target needs it.

### OFR3502

**public API differs from the baseline** · warning · audit

ApiCompat found a type or member that the build at the baseline revision has and the working tree does not (or the reverse).

- **Typical cause:** A move or refactoring that changed a namespace, removed a member, or changed a signature.
- **Fix:** Restore the surface, add a type forwarder (`offramp forwarders`), or accept the break deliberately.

### OFR3503

**nothing to compare** · error · audit

`audit api-compat` needs two target frameworks of one project, or a baseline revision.

- **Typical cause:** A single-target project without --baseline, or --left and --right naming the same target.
- **Fix:** Pass --baseline REVISION, or --left and --right with two of the project's targets.

### OFR3504

**API comparison could not run** · error · audit

A side of the comparison did not build, the baseline could not be checked out, or the ApiCompat tool could not be installed or run. The message carries the tool's own words.

- **Typical cause:** A build error, a revision that does not exist, or no access to the NuGet feed that hosts Microsoft.DotNet.ApiCompat.Tool.
- **Fix:** Fix the build or the revision the message names, or make the tool's feed reachable, and run again.

### OFR3601

**member cannot be wrapped in `#if`** · warning · audit

`ifdef wrap` left a finding unwrapped: code that also compiles on the target needs the member (it is referenced outside the wrapped code, overrides or implements a member, or shares its lines with other code), so it needs a real port rather than a conditional.

- **Typical cause:** A helper with a missing API that callers on both targets use; an interface implementation that uses one.
- **Fix:** Port the member (or put the missing API behind a seam), or wrap its callers first and run `ifdef wrap` again.

### OFR3602

**finding does not match the source** · warning · audit

The location an audit finding names no longer holds the symbol it reports, so `ifdef wrap` does not touch it.

- **Typical cause:** The file changed after the audit ran.
- **Fix:** Run the audit again (`offramp scan`, then `offramp audit api --format json --out audit.json`) and wrap from the new findings.

### OFR3603

**conditional region depends on other symbols** · warning · audit

`ifdef strip` left an `#if` chain alone: with the stripped symbol decided, which branch compiles still depends on other symbols.

- **Typical cause:** Conditions such as `NETFRAMEWORK && DEBUG` or `#elif` branches on other symbols before the one that names the stripped symbol.
- **Fix:** Simplify the condition by hand, or strip the other symbol first.

### OFR3604

**findings file missing or invalid** · error · audit

`ifdef wrap --findings` could not read an audit result from the file.

- **Typical cause:** A wrong path, or a file that is not the output of `offramp audit api --format json` (or its `--json` envelope).
- **Fix:** Write the findings with `offramp audit api --format json --out audit.json` and pass that file.

### OFR4001

**no seam found** · warning · seams

`seams` found no boundary to put an interface on: nothing uses the unportable symbols, the taint reaches the project's entry points directly, or the smallest boundary crosses more references than --max-cut allows.

- **Typical cause:** Unportable types in the project's public API, or an unportable base type every class derives from.
- **Fix:** Check the unportable symbols (--symbols, seams.unportableSymbols); split the project first, or raise --max-cut.

### OFR4002

**seam member not wire-friendly** · warning · seams

A member the clean side calls on the boundary type takes or returns something that cannot cross a network boundary as data (a delegate, event, stream, pointer, `ref`/`out` parameter, `object`, interface, or a type without public settable properties).

- **Typical cause:** Callbacks, streams, and domain objects with behavior in the boundary's signatures.
- **Fix:** Change the member to exchange data (DTOs), or keep it local with `remote --skip-member`.

### OFR4003

**static member on the boundary** · warning · seams

The clean side calls a static member of the boundary type; an interface cannot declare it, so it needs an instance wrapper.

- **Typical cause:** Static helpers and availability checks on the unportable class.
- **Fix:** Add an instance member that calls the static one and use it through the interface.

### OFR4010

**caller instantiates concrete type directly** · warning · seams

`extract interface` left a caller depending on the concrete type because it creates the instance itself with `new`; the interface cannot be swapped for that caller until the instance is injected.

- **Typical cause:** Service locator style code and classes that build their own dependencies.
- **Fix:** Take the interface as a constructor parameter (or resolve it from the container) and remove the `new`.

### OFR4011

**extract type not found** · error · seams

`extract interface --type` names no class or struct declared in the project's recorded compilation.

- **Typical cause:** A typo, a nested type written with `.` instead of `+`, or a type from another project.
- **Fix:** Pass the fully qualified name of a class declared in --project (as `seams` prints it).

### OFR4012

**extraction would not compile** · error · seams

The edited project was compiled in memory before anything was written, and the extraction introduced errors (or a source file changed since the last scan), so nothing was written.

- **Typical cause:** Members with signatures an interface cannot express, callers that pass the retyped dependency on as the concrete type, stale scans.
- **Fix:** Narrow the members with --members, run `offramp scan` again, or extract by hand; the message lists the first errors.

### OFR4013

**seam not found** · error · seams

`extract interface --from-seams FILE#ID` could not read the seams document, or it has no seam with that id.

- **Typical cause:** A path to something other than `seams --out seams.json` output, or an id from an older run.
- **Fix:** Run `offramp seams --project P --out seams.json` and pass one of its ids (seam-1, seam-2, ...).

### OFR4020

**sync member over remote boundary** · warning · seams

A synchronous interface member now makes an HTTP call: the generated client blocks on it, which ties up a thread for the network round trip and can deadlock under a synchronization context.

- **Typical cause:** Interfaces designed for in-process calls.
- **Fix:** Generate the asynchronous variant with `remote --async-variant` and move callers to it.

### OFR4021

**generated project directory exists** · error · seams

`remote` writes new projects only: a directory it would generate (contracts, client, or host) already exists and is not empty, so nothing was generated.

- **Typical cause:** Running `remote` twice, or a name that collides with an existing project.
- **Fix:** Choose other places with --contracts-dir, --client-dir, and --host-dir, or delete the earlier output.

### OFR4022

**host falls back to net48** · info · seams

The implementation and the files it uses did not compile for net10.0-windows with Microsoft.Windows.Compatibility (or the check could not run), so the host is the legacy net48 fallback: OWIN self-host with ASP.NET Web API 2.

- **Typical cause:** Implementations that use APIs missing from modern .NET even on Windows (WCF server, Remoting, System.Web), or types from other projects.
- **Fix:** Port what the message lists, then generate again with --host-framework net10-windows.

### OFR4023

**remote interface not found** · error · seams

`remote --interface` names no interface declared in the project, the implementation is missing or ambiguous, or `--skip-member` names no member of the interface.

- **Typical cause:** A type from another project, several classes implementing the interface, a typo.
- **Fix:** Pass --project, and --implementation when more than one class implements the interface.

### OFR4101

**pause, continue, or custom commands differ** · warning · service

The service handles pause, continue, or custom commands; the generic host has none of them, so the worker keeps them as methods the application can call and nothing calls them by default.

- **Typical cause:** Services that pause work while an operator investigates, or accept custom commands from `sc control`.
- **Fix:** Decide whether the behavior is still needed; call the worker's Pause and Continue from an endpoint or configuration if it is.

### OFR4102

**code left in compatibility region** · warning · service

The worker keeps service code that uses ServiceBase members the host does not have (RequestAdditionalTime, ExitCode, Stop, the EventLog object, a Topshelf host control) inside `#if OFFRAMP_SERVICEBASE`, which is never defined; the code does not run until someone ports it.

- **Typical cause:** Shutdown timing requests, exit codes, self-stopping services, event log sources.
- **Fix:** Port each region: HostOptions.ShutdownTimeout for more stop time, IHostApplicationLifetime.StopApplication to stop, Environment.ExitCode for exit codes.

### OFR4103

**multiple services in one executable** · info · service

The executable runs several services; the worker project hosts one worker per service in one process, which registers as one Windows service or one container.

- **Typical cause:** `ServiceBase.Run(new ServiceBase[] { ... })` with more than one service.
- **Fix:** Keep them together, or split the worker project if they must start, stop, or scale independently.

### OFR4104

**service depends on other Windows services** · warning · service

The installer or Topshelf configuration makes the service depend on other Windows services; a container or systemd unit has no such dependency, so the worker must wait for or reach the dependency itself.

- **Typical cause:** Dependencies on the event log, SQL Server, MSMQ, or a vendor service.
- **Fix:** Replace the dependency with readiness checks or retries; the install script keeps it for the Windows host.

### OFR4105

**session change or power events** · warning · service

The service reacts to logon sessions or power events, which the generic host does not deliver; the handler is kept in an excluded region.

- **Typical cause:** Services that act on user logon, lock, or system suspend.
- **Fix:** Keep this part as a Windows service (the Windows host), or drop the behavior for containers.

### OFR4106

**generated worker does not compile** · warning · service

The worker project (the generated code and the linked files it uses) was compiled in memory for the target and has errors; it is still written, with the errors to fix.

- **Typical cause:** Linked code that uses .NET Framework-only APIs, the ServiceProcess types in kept code, or project types from other projects.
- **Fix:** Fix the listed errors in the worker, or port the linked code first (`audit api` lists what is missing).

### OFR4107

**no service found** · error · service

`service --project` names a project with no ServiceBase subclass and no Topshelf HostFactory configuration.

- **Typical cause:** A library, a console application, or a service hosted by another framework.
- **Fix:** Pass the project that contains the service's entry point.

### OFR4108

**worker directory exists** · error · service

`service` writes a new project only: its output directory already exists and is not empty, so nothing was generated.

- **Typical cause:** Running `service` twice, or an --out that points at an existing project.
- **Fix:** Pass another --out, or delete the earlier output.

### OFR4201

**Web Forms page not ported** · warning · web

Web Forms pages, user controls, and master pages have no ASP.NET Core counterpart that code can be converted to; `web scaffold` inventories them and leaves them to the legacy application behind the proxy.

- **Typical cause:** Any .aspx, .ascx, or .master file.
- **Fix:** Keep the page behind the proxy, rewrite it as a Razor Page or Blazor component, or use a third-party Web Forms converter.

### OFR4202

**HttpHandler became an endpoint stub** · info · web

The handler's ProcessRequest is kept in a marked region of a minimal API endpoint stub, which is not mapped: its path keeps going to the legacy application through the proxy until someone ports the code and maps the endpoint.

- **Typical cause:** Image, file, and feed handlers (.ashx, *.axd registrations).
- **Fix:** Port ProcessRequest into the stub's Handle method, then uncomment its MapMethods line in Program.cs.

### OFR4203

**scaffolded project does not compile** · error · web

The generated ASP.NET Core project was compiled in memory and still has errors after the actions the compiler rejected were left to the legacy application; it is written anyway.

- **Typical cause:** Code shared with the legacy application that uses System.Web, or types the linked files need from other projects.
- **Fix:** Read the errors in the message; move the shared code into a project both applications reference, or port it.

### OFR4204

**output folder not empty** · error · web

The folder `--new` names already has files, so nothing was generated.

- **Typical cause:** A second run.
- **Fix:** Pass another --new, or delete the folder.

### OFR4301

**compile items kept explicit** · warning · csproj

The project's Compile items are not the files the SDK's `**/*.cs` glob would give (a file on disk the project leaves out, or one outside the glob), so the converted project keeps the list and sets EnableDefaultCompileItems to false.

- **Typical cause:** Excluded or abandoned source files left in the folder, files included from elsewhere without a Link.
- **Fix:** Delete or move the files the glob would add, then run the conversion again to get a globbed project; or keep the explicit list.

### OFR4302

**build step converted for review** · info · csproj

A PreBuildEvent, PostBuildEvent, BeforeBuild, or AfterBuild became a target hooked to the same point in the build. The SDK's output layout (bin/<configuration>/<framework>/) can change what relative paths in the command mean.

- **Typical cause:** Copy steps, signing, and code generation in legacy projects.
- **Fix:** Read the target and check the paths it uses; better, replace it with MSBuild items or tasks.

### OFR4303

**converted project compiles different inputs** · error · csproj

The converted project was built in a scratch copy, and its compiler inputs (source files, references, embedded resources) differ from the original build's, or it did not build. `--apply` is refused unless `--accept-diff`.

- **Typical cause:** A glob that picks up a file the project left out, a package whose assemblies differ from the HintPath ones, a resource with a different manifest name.
- **Fix:** Read the differences in the result's `verification`; fix the project or the files, or accept them with --accept-diff.

### OFR4304

**project not converted** · warning · csproj

The project is not converted to SDK style: an ASP.NET web application project (the SDK has no System.Web project support), or a project that is not C#.

- **Typical cause:** ASP.NET MVC and Web Forms applications.
- **Fix:** Keep the project as it is and move its routes to ASP.NET Core with `offramp web scaffold`.

### OFR4401

**setting not representable** · warning · config convert

A configuration section, or one of its properties, has no faithful appsettings.json form: its section type is not in the solution, it is an element collection, it uses a custom TypeConverter, or its value does not parse as the property's type. It is left out of the JSON and the options class.

- **Typical cause:** Custom section handlers, `ConfigurationElementCollection`s, converters for domain types.
- **Fix:** Add the setting to appsettings.json by hand in the shape the new code reads, and bind it to an options class you write.

### OFR4402

**WCF configuration** · warning · config convert

`system.serviceModel` configures WCF clients and services; modern .NET has no configuration-file WCF. Clients are configured in code (System.ServiceModel.Http packages) and services move to CoreWCF.

- **Typical cause:** WCF clients generated by Add Service Reference, WCF-hosted services.
- **Fix:** Configure the client binding and endpoint in code; for services, see CoreWCF's configuration support.

### OFR4403

**system.web settings belong to the web migration** · info · config convert

`system.web` and `system.webServer` configure ASP.NET and IIS: authentication, session, handlers, modules. Their ASP.NET Core counterparts are middleware and hosting settings, generated by `web scaffold`, not configuration values.

- **Typical cause:** ASP.NET applications.
- **Fix:** Run `offramp web inventory` and `offramp web scaffold` for the web application.

### OFR4404

**config transform not expressible as overrides** · warning · config convert

A transform file (Web.Release.config) does something an appsettings.{Environment}.json override cannot: it inserts or removes elements outside appSettings and connectionStrings, uses XPath locators, or transforms sections with no JSON form. The parts that are overrides are converted; the rest is listed.

- **Typical cause:** Transforms that remove debug settings or rewrite system.web.
- **Fix:** Express the rest as environment-specific configuration or deployment settings by hand.

### OFR4405

**no configuration file** · error · config convert

`config convert` looks for App.config or Web.config in the project's folder and found neither.

- **Typical cause:** A project that reads no configuration, or a configuration file with another name.
- **Fix:** Pass the project that owns the configuration file.

### OFR4406

**output file exists** · error · config convert

A file `config convert` would write (appsettings.json, an environment file, the options or shim class) already exists, so nothing was written.

- **Typical cause:** A second run, or a project that already has appsettings.json.
- **Fix:** Write to another folder with --out, or move the existing file aside and merge by hand.

### OFR4501

**codemod site skipped** · info · codemod

The codemod found a site it does not rewrite safely; the message says why (a synchronous method, a static member, a class created with new, ...). The code is unchanged.

- **Typical cause:** Patterns just outside what the codemod proves safe.
- **Fix:** Change the site by hand, or change the code around it so the codemod can, and run it again.

### OFR4502

**unknown codemod** · error · codemod

`codemod run --mod` names no codemod in the catalog.

- **Typical cause:** A typo, or an ID from a newer version.
- **Fix:** Run `offramp codemod list` for the names and IDs.

### OFR4503

**codemod is experimental** · error · codemod

The codemod is right less than about 95% of the time on the fixtures and corpus, so it runs only with --experimental.

- **Typical cause:** Codemods whose rewrite changes behavior in ways that need review (thread-abort).
- **Fix:** Pass --experimental and review every change before committing it.

### OFR4504

**source changed since the last scan** · error · codemod

A file the codemod would rewrite differs from the text recorded by the last scan, so it was left alone: rewriting it would lose the newer edits.

- **Typical cause:** Editing files after `offramp scan`.
- **Fix:** Run `offramp scan` and the codemod again.

### OFR4505

**package not added** · warning · codemod

The rewritten code needs a package that could not be added to the project file (a packages.config project, or no central PackageVersion file found under central package management).

- **Typical cause:** Old-style projects, central package management with an unusual layout.
- **Fix:** Add the package the message names by hand, or modernize the project first (`offramp csproj modernize`).

### OFR4506

**project does not reference Offramp.Analyzers** · warning · codemod

`--format-mode` runs `dotnet format analyzers`, which only sees analyzers the project references; this project does not reference the Offramp.Analyzers package, so it was skipped.

- **Typical cause:** Using --format-mode before adding the analyzer package.
- **Fix:** Add `<PackageReference Include="Offramp.Analyzers" PrivateAssets="all" />`, or run without --format-mode.

### OFR4507

**verification failed; codemod rolled back** · error · codemod

The build (or verification command) failed after the codemod, and `verify.onFailure: rollback` restored every file from the journal.

- **Typical cause:** A rewrite that needs a package the project cannot restore, or a build that was already broken.
- **Fix:** Read the verification errors; fix them or skip the sites, then run the codemod again. `verify.onFailure: keep` leaves the change in place.

### OFR4508

**dotnet format failed** · error · codemod

`--format-mode` ran `dotnet format analyzers` for the project and it failed: an exit code other than 0 (or 2, which a dry run returns when there are changes), or no report.

- **Typical cause:** A project that does not restore or load, or an SDK without `dotnet format`.
- **Fix:** Run the command in the message yourself to see its output; `dotnet restore` the project first, or run without --format-mode.

### OFR4510

**connections encrypted by default (Microsoft.Data.SqlClient)** · info · codemod

Microsoft.Data.SqlClient defaults Encrypt to true (System.Data.SqlClient defaulted to false), so connections to servers without a trusted certificate now fail.

- **Typical cause:** Development and on-premises SQL Servers with self-signed certificates.
- **Fix:** Install a trusted certificate on the server, or set TrustServerCertificate=True (or Encrypt=False) in the connection strings that need it.

### OFR5001

**verification failed** · error · verify

The verification build (or `verify.command`) failed. With a baseline, only errors the baseline does not list count.

- **Typical cause:** A compile error in the selected projects, a broken change, or a verification command that exited non-zero.
- **Fix:** Read the grouped errors in the result (the first occurrence of each code is shown) and the binary log under `.offramp/verify/`; fix them or record the current state with `verify --baseline`.

### OFR5002

**verification timed out** · error · verify

The verification build or command ran longer than `verify.timeoutSeconds` and was stopped.

- **Typical cause:** A large build, a hung process, or a timeout set too low for this repository.
- **Fix:** Raise `verify.timeoutSeconds`, narrow the build with `--projects` or `verify.projects`, or verify a slice.

### OFR5010

**new error code relative to baseline** · warning · verify

The build reports an error code that the recorded baseline does not contain.

- **Typical cause:** A change introduced a new kind of failure in a repository that was already failing to build in known ways.
- **Fix:** Fix the new errors, or record a new baseline with `verify --baseline` if they are expected.

### OFR5020

**finding from the verification command** · warning · verify

`verify.command` printed a JSON envelope with a finding whose code is not an Offramp code; it is reported under this code at its own severity, with the original code in `data.code`.

- **Typical cause:** A verification script that runs linters, tests, or other tools and reports their findings as an envelope.
- **Fix:** See the tool that reported `data.code`. Offramp codes in the envelope are merged unchanged.

### OFR5090

**verification skipped by configuration** · info · verify

`verify.mode` (or `--mode`) is `none`, so nothing was built or run.

- **Typical cause:** Verification turned off in `offramp.yml`, for example while iterating on a plan.
- **Fix:** Set `verify.mode` to `build` or `command` to verify changes.

## Reserved codes

Codes the specification assigns to commands that have not shipped yet. Implementations
use these numbers; each moves to the table above in the pull request that first emits it.

| Code | Severity | Meaning |
|---|---|---|
| OFR2010 | error | move crosses a solution slice boundary |
| OFR4030 | error | gRPC unavailable for net48 host |
| OFR9101 | error | MCP request outside allowed root |
