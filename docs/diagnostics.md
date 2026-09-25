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
| [OFR0010](#ofr0010) | error | environment | .NET SDK not found |
| [OFR0011](#ofr0011) | error | environment | SDK requested by global.json is not installed |
| [OFR0012](#ofr0012) | error | environment | selected SDK cannot target the requested framework |
| [OFR0013](#ofr0013) | error | environment | .NET Framework reference assemblies not resolvable |
| [OFR0014](#ofr0014) | warning | environment | git not found |
| [OFR0015](#ofr0015) | warning | environment | not a git repository |
| [OFR0016](#ofr0016) | info | configuration | no offramp.yml; built-in defaults in effect |
| [OFR0020](#ofr0020) | error | workspace | more than one solution found |
| [OFR0030](#ofr0030) | error | configuration | offramp.yml already exists |
| [OFR0050](#ofr0050) | warning | configuration | unknown key in offramp.yml |
| [OFR0051](#ofr0051) | warning | configuration | pin without a reason |
| [OFR0052](#ofr0052) | info | configuration | rule override without a reason |
| [OFR0053](#ofr0053) | error | configuration | invalid value in offramp.yml |
| [OFR0054](#ofr0054) | error | configuration | offramp.yml is not valid YAML |
| [OFR0055](#ofr0055) | error | configuration | configuration file not found |
| [OFR0056](#ofr0056) | error | configuration | invalid configuration value from the environment |
| [OFR0099](#ofr0099) | error | cli | internal error |
| [OFR1006](#ofr1006) | warning | deps | feed unreachable; result partial |

### OFR0001

**workspace model missing** · error · workspace

The command needs the workspace model (`.offramp/workspace.json`) and it does not exist.

- **Typical cause:** `offramp scan` has not been run in this repository, or `--workspace` points somewhere else.
- **Fix:** Run `offramp scan`. `doctor` reports the same condition as a warning.

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

### OFR1006

**feed unreachable; result partial** · warning · deps

A NuGet feed could not be queried, so any answer that depends on it is incomplete.

- **Typical cause:** No network, a feed that is down, or missing credentials for a private feed.
- **Fix:** Check `nuget.config`, network access, and credential providers, then re-run.

## Reserved codes

Codes the specification assigns to commands that have not shipped yet. Implementations
use these numbers; each moves to the table above in the pull request that first emits it.

| Code | Severity | Meaning |
|---|---|---|
| OFR0002 | warning | workspace model stale (inputs changed since scan) |
| OFR0101 | warning | project could not be loaded (reason attached) |
| OFR0102 | info | project kind unknown |
| OFR0110 | warning | Windows-only build step: sgen |
| OFR0111 | warning | Windows-only build step: COM reference |
| OFR0112 | warning | Windows-only build step: EDMX EntityDeploy |
| OFR0113 | warning | Windows-only build step: T4 / Fakes |
| OFR0114 | warning | Windows-only build step: SSDT |
| OFR0115 | warning | Windows-only build step: build event calling Windows executable |
| OFR0120 | warning | project reference cycle |
| OFR0130 | error | analysis build failed; model partial |
| OFR0201 | info | Mermaid output too large to render well |
| OFR1001 | error | no package version supports the target |
| OFR1002 | warning | in-use version does not support the target |
| OFR1003 | warning | package deprecated |
| OFR1004 | warning | package assets are Windows-only |
| OFR1005 | warning | package not found on any feed |
| OFR1203 | warning | pin kept a package below the otherwise-selected version |
| OFR1210 | error | pin conflicts with a transitive lower bound (chain attached) |
| OFR1211 | error | restore verification reported NU1605/NU1107/NU1608/NU1010 |
| OFR1220 | warning | family member lacks the family version |
| OFR1301 | warning | project outside the solution would inherit CPM |
| OFR1302 | warning | nested Directory.Packages.props shadows the root |
| OFR1303 | warning | packages.config project cannot use CPM |
| OFR1401 | info | loose DLL is another project's output |
| OFR1402 | info | loose DLL matched to a package |
| OFR1403 | warning | loose DLL unmatched |
| OFR1404 | error | loose Framework-only DLL with no replacement |
| OFR1501–1504 | info/warning | binding redirect added/changed/pruned/stale |
| OFR2001 | error | move would create a project reference cycle |
| OFR2002 | error | destination equals source |
| OFR2010 | error | move crosses a solution slice boundary |
| OFR2050 | error | verification failed; changes rolled back |
| OFR2101 | warning | file needs co-move |
| OFR2102 | error | required package unavailable for destination |
| OFR2103 | error | file does not compile in destination |
| OFR2104 | error | source still depends on moved code |
| OFR2105 | warning | Windows-only API in moved file |
| OFR2110 | info | partial type co-moved |
| OFR2111 | warning | destination excludes the file path |
| OFR2120 | warning | namespace differs from destination root namespace |
| OFR2150 | warning | file changed since plan |
| OFR2201 | warning | candidate referenced by production code; not moved |
| OFR2202 | error | multiple candidate test projects |
| OFR2203 | error | no test project found; use `--to` or `--create` |
| OFR2204 | warning | destination path collision |
| OFR2210 | info | test-framework packages removable from source |
| OFR2301 | warning | string reference to a moved type |
| OFR3001 | error | API missing on target |
| OFR3002 | warning | Windows-only API |
| OFR3003 | error | API throws on modern .NET |
| OFR3004–3009 | error | removed technology (WebForms, ASMX, WCF server, Remoting, WF, CAS) |
| OFR3101–3120 | varies | behavior rules (see `spec/commands/audit.md`) |
| OFR3201–3211 | varies | serialization rules |
| OFR3301–3320 | varies | native interop rules |
| OFR3401–3402 | info | dead code candidates; test-only usage |
| OFR3501–3502 | warning | public API differs between targets / from baseline |
| OFR3601 | warning | member cannot be wrapped in `#if` |
| OFR4001–4003 | varies | seams |
| OFR4010 | warning | caller instantiates concrete type directly |
| OFR4020 | warning | sync member over remote boundary |
| OFR4030 | error | gRPC unavailable for net48 host |
| OFR4101–4105 | varies | service conversion notes |
| OFR4201–4202 | varies | web scaffold notes |
| OFR4301–4303 | varies | csproj modernize notes |
| OFR4401–4404 | varies | config convert notes |
| OFR4501, OFR4510 | varies | codemod skipped site; SqlClient encrypt default |
| OFR5001 | error | verification build failed |
| OFR5002 | error | verification timed out |
| OFR5010 | warning | new error code relative to baseline |
| OFR5090 | info | verification skipped by configuration |
| OFR9101 | error | MCP request outside allowed root |
