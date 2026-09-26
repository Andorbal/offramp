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
| [OFR1301](#ofr1301) | warning | deps | project outside the solution would inherit CPM |
| [OFR1302](#ofr1302) | warning | deps | nested Directory.Packages.props shadows the root |
| [OFR1303](#ofr1303) | warning | deps | packages.config project cannot use CPM |
| [OFR2002](#ofr2002) | error | move | destination equals source |
| [OFR2050](#ofr2050) | error | move | verification failed; changes rolled back |
| [OFR2103](#ofr2103) | warning | move | file does not compile in the destination |
| [OFR2104](#ofr2104) | error | move | source still depends on moved code |
| [OFR2151](#ofr2151) | error | move | file changed since the move; rollback stopped |
| [OFR2201](#ofr2201) | warning | move | test code used by production code |
| [OFR2202](#ofr2202) | error | move | multiple candidate test projects |
| [OFR2203](#ofr2203) | error | move | no test project found |
| [OFR2204](#ofr2204) | warning | move | destination path collision |
| [OFR2205](#ofr2205) | error | move | project language not supported |
| [OFR2206](#ofr2206) | warning | move | file outside the project folder |
| [OFR2210](#ofr2210) | info | move | test-framework packages removable from source |
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

### OFR2002

**destination equals source** · error · move

The move's destination project is the source project itself.

- **Typical cause:** `--to` naming the source project, or a naming rule that resolves to it.
- **Fix:** Name a different destination with `--to`.

### OFR2050

**verification failed; changes rolled back** · error · move

The build (or verification command) failed after the move, and `verify.onFailure: rollback` undid it from the journal: renames reversed, project files restored byte for byte, new files deleted.

- **Typical cause:** Moved code that compiles in isolation but breaks the solution build, a test project that does not restore, or an unrelated broken build.
- **Fix:** Read the verification errors in the result; fix them or narrow the move, then run it again. `verify.onFailure: keep` leaves a failed move in place for inspection.

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

### OFR2151

**file changed since the move; rollback stopped** · error · move

A file the move wrote (a moved file, an edited project file, or a new file) changed after the move, so undoing it would lose that change. Nothing was rolled back.

- **Typical cause:** Edits made after `move tests --apply`, or a second move over the same files.
- **Fix:** Undo the later changes first (for example `git stash`), then roll back; or leave the move in place.

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
| OFR1203 | warning | pin kept a package below the otherwise-selected version |
| OFR1210 | error | pin conflicts with a transitive lower bound (chain attached) |
| OFR1211 | error | restore verification reported NU1605/NU1107/NU1608/NU1010 |
| OFR1220 | warning | family member lacks the family version |
| OFR1401 | info | loose DLL is another project's output |
| OFR1402 | info | loose DLL matched to a package |
| OFR1403 | warning | loose DLL unmatched |
| OFR1404 | error | loose Framework-only DLL with no replacement |
| OFR1501–1504 | info/warning | binding redirect added/changed/pruned/stale |
| OFR2001 | error | move would create a project reference cycle |
| OFR2010 | error | move crosses a solution slice boundary |
| OFR2101 | warning | file needs co-move |
| OFR2102 | error | required package unavailable for destination |
| OFR2105 | warning | Windows-only API in moved file |
| OFR2110 | info | partial type co-moved |
| OFR2111 | warning | destination excludes the file path |
| OFR2120 | warning | namespace differs from destination root namespace |
| OFR2150 | warning | file changed since plan |
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
| OFR9101 | error | MCP request outside allowed root |
