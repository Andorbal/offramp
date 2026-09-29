# Changelog

All notable changes to Offramp are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may
contain breaking changes to command output; every such change is called out
under **Changed** with a migration note.

Each pull request adds its entries under `[Unreleased]`. A release moves that
block under a version heading with the date. `docs/RELEASING.md` has the steps.

## [Unreleased]

### Added
- `OFR3015` (info): `audit api` could not find one of a project's packages on the feeds (NU1101,
  NU1102, NU1103), so it references the package's DLLs as recorded and does not check them.
- The workspace model records the packages each `packages.config` lists
  (`packagesConfigPackages`: id, version, target framework, development dependency), and the
  `packages` index includes them (ADR 0035). `deps audit` audits them: on DotNetNuke 9.13 it
  saw 23 packages before and 77 now, among them the blockers (WebFormsMvp, ClientDependency, SQL
  Server Compact) and the ASP.NET packages to replace. A test framework or Topshelf listed in
  `packages.config` sets the project's kind, which moved 12 DotNetNuke projects from `library`
  to `test`.
- Legacy (non-SDK) solutions build on macOS and Linux (ADR 0037): Offramp now supplies what
  they lack there, and `scan` names what it cannot supply. On a fresh DotNetNuke 9.13 checkout,
  the build still stops at DotNetNuke's own problems (`XCOPY` build targets, and on Linux paths
  in the wrong letter case), each named; with those fixed, all 71 projects load and 4 are partial,
  where before no legacy project could compile at all:
  - The compile-only block has a third section, for legacy projects outside Windows: they
    restore the `PackageReference` way and take the .NET Framework reference assemblies and the
    Visual Studio web targets from packages, as SDK-style projects do; a legacy Visual Basic
    project gets its runtime from the reference assemblies. `doctor --fix` adds it to a file
    with the first two sections.
  - `scan` restores what `packages.config` files list into the solution's packages folder,
    laid out as `nuget restore` lays it out, from the global packages folder or the feeds in
    `nuget.config`, and never overwrites a folder: `OFR0106` (info) lists what it wrote,
    `OFR0105` (warning) each package it could not find.
  - Outside Windows, the builds of `scan`, verification, and `csproj modernize` pass
    `RestorePackages=false`, so a `.nuget/NuGet.targets` does not run `NuGet.exe` through Mono.
  - New Windows-only build steps found from a failed build's errors: `OFR0117` (`path-case`) a
    path that exists only in another letter case (Linux), `OFR0118` (`inline-task`)
    `CodeTaskFactory`, as in `Microsoft.CodeDom.Providers.DotNetCompilerPlatform`, and
    `OFR0119` (`resources`) non-string resources. An `Exec` command written for cmd.exe that
    failed is `OFR0115` (`build-event`) also when a target runs it.
  - `docs/compiling-on-macos.md` explains each, with the `Directory.Build.targets` fix for the
    CodeDom provider's inline tasks.
- `OFR3012` (warning): a project an audit cannot read.
- `OFR1506` (warning): `redirects sync` skipped an application with a partial model.
- `OFR2112` (warning): a file compiles in the destination but raises warnings it treats as
  errors.
- `OFR4305` (warning): a converted project's build fails NuGet audit under warnings as errors.
- `OFR0121` (warning): a standard, modern, or dual project's portable target references a
  project that targets only .NET Framework. It builds only because a legacy project skips
  NuGet's compatibility check, and fails at run time.
- The workspace model records, for dual projects, the project references their standard and
  modern targets use (`modernProjectReferences`).
- `OFR1204` (info): `deps consolidate` selected a version a `packages.config` project does not
  use; consolidation writes `PackageReference` projects only, so it keeps its version.
- `rules/package-map.yml` maps `Microsoft.NETFramework.ReferenceAssemblies*` and
  `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` (not needed on modern .NET), SQL Server
  Compact, and the rest of the ASP.NET Web Pages packages.
- `tests/Offramp.Corpus.Tests`: corpus tests that run the CLI on real codebases pinned to a
  commit (`codebases.json`), starting with DotNetNuke Platform 9.13.10. Every codebase gets a
  standard sweep of read-only commands, checked for crashes, schema matches, and a deterministic
  model, then assertions from its field test. They run only when asked (`OFFRAMP_CORPUS`), and
  `corpus.yml` runs them on manual dispatch only, one job per codebase, with every command's
  output uploaded. The weekly job matched no test before and could not fail.
- How to do a field test (`docs/field-tests/README.md`) and add its codebase to the corpus
  (`tests/Offramp.Corpus.Tests/README.md`).
- The `webforms` fixture: a Web Forms web project whose control derives from `UserControl`
  through another project and calls a legacy Visual Basic library, as DotNetNuke modules do.
- `scan --msbuild` and `scan.builder: msbuild` in `offramp.yml`: `scan` builds with MSBuild.exe
  from Visual Studio or the Build Tools instead of `dotnet build`, for solutions with projects only
  .NET Framework's MSBuild builds (sgen, COM references). It runs the same never-incremental build
  (`-restore -t:Rebuild`, `verify.configuration`, `verify.properties`) and also restores
  packages.config projects, as Visual Studio does.
- `scan --msbuild-path PATH` and `scan.msbuildPath`: MSBuild.exe, a folder holding it, or a Visual
  Studio or Build Tools installation folder. Without one, scan uses the Developer Command Prompt's
  installation, else the newest one vswhere reports. `OFFRAMP_SCAN__BUILDER` and
  `OFFRAMP_SCAN__MSBUILD_PATH` set both per machine. ADR 0034.
- `OFR0017` (MSBuild not found): `--msbuild` found no MSBuild.exe, or could not start it; scan exits 3.

### Changed
- Every envelope's `effectiveConfig` has a `scan` section (`builder`, `msbuildPath`), and `init`
  writes it to `offramp.yml` with its defaults.

### Fixed
- `csproj modernize` reports what its verification found in full. A converted build that failed
  showed its first 10 errors and nothing else (NHibernate 4.1.2's `netstandard2.0` conversion had
  71): the result's `verification` now has `built`, `buildErrorCount`, and `buildErrorCodes` (the
  count per code, most frequent first), `OFR4303`'s message gives the count by code, and error
  paths are repository-relative instead of the scratch copy's. The terminal view says a project
  "does not build" instead of "compiles different inputs" when its converted build failed, with
  the error count by code (SmartStoreNET 4.2.0). `--all` passes every project to the conversion,
  so a legacy Visual Basic project is reported as not converted (`OFR4304`); it left NHibernate's
  `.vbproj` out without a word.
- `csproj modernize` converts to a project that compiles what the legacy one did (ADR 0043):
  - An SDK-style project compiles against its references' references too, so NHibernate 4.1.2's
    `TestDatabaseSetup` (which references `Test`, which references `DomainModel`) failed
    verification with "references added: NHibernate.DomainModel" (`OFR4303`). A converted project
    whose referenced projects have project references of their own sets
    `DisableTransitiveProjectReferences`.
  - The NuGet 2 restore import (`$(SolutionDir)\.nuget\NuGet.targets`) is removed with
    `RestorePackages`. It stayed while `SolutionDir` went, so 10 of SmartStoreNET 4.2.0's 11
    conversions failed with MSB4019. `SolutionDir`'s definition now stays while a build event or
    import still uses it (SmartStore.Data.Tests' post-build step uses
    `$(SolutionDir)packages\...`).
  - A `packages.config` version lower than the one a referenced project brings is raised to it,
    with `OFR4307` (warning): with `PackageReference` the higher version flows in, and the lower
    one is a package downgrade, NU1605, an error. It failed 5 of Open Live Writer 0.6.3's
    conversions (Newtonsoft.Json 10.0.2 in `PostEditor`, 13.0.1 from `BlogClient`).
  - A build event in a conditioned property group keeps the condition on its target; Open Live
    Writer's installer step, conditioned off for compile-only builds, ran unconditioned
    (MSB3073).
  - With a `-windows` target (`--tfm "net461;net10.0-windows"`), a project that references
    Windows Forms or WPF gets `UseWindowsForms` or `UseWPF`, and its .NET Framework references
    are conditioned on the .NET Framework target instead of applying to every target.
  - `OFR4308` (warning) names a target in the project body that a common target of the same name
    now overrides (Open Live Writer's empty `_CopyFilesMarkedCopyLocal`, which turned copy-local
    off), and an imported file that sets `TargetFrameworkVersion`, `OutputPath`,
    `IntermediateOutputPath`, or `MSBuildExtensionsPath` unconditionally (`writer.build.settings`).
    Both stopped working without a word.
- `csproj modernize` and `codemod run --mod assemblyinfo` edit only a project's own AssemblyInfo
  files (ADR 0039). They stripped the version, company, and product attributes from any file the
  project compiled: NHibernate 4.1.2's git-ignored `SharedAssemblyInfo.cs`, which NAnt writes
  and six projects link (one outside the solution); SmartStoreNET 4.2.0's two shared files, which
  19 projects compile, 14 of them web projects `csproj modernize` does not convert (they would
  have built as version 0.0.0.0); and, one `--project` at a time as the guide converts, Open Live
  Writer 0.6.3's `GlobalAssemblyInfo.cs`, linked into 20 projects. A file outside the project's
  folder, compiled by another project, ignored by git, added to the compilation by a build target
  (Open Live Writer's generated `GlobalAssemblyVersionInfo.cs`, which caused CS0579 in 20
  conversions), or generated code (an `<auto-generated>` header) now keeps its attributes, and
  the project sets `GenerateAssembly<Name>Attribute` to `false` for each of them so the SDK does
  not generate them again. `OFR4306` (info) names each such file and why.
- `audit api` compiles a project for `net10.0-windows` when it uses Windows Forms or WPF: it
  references `System.Windows.Forms`, `PresentationFramework`, `WindowsBase` or `System.Xaml`, or
  sets `UseWindowsForms` or `UseWPF` (ADR 0040). Only `winforms` and `wpf` projects, which are
  applications, were, so on Open Live Writer 18 class libraries of forms and controls were
  compiled for `net10.0`: 13,374 of 13,910 `OFR3001` findings were Windows Forms and
  `System.Drawing` APIs that `net10.0-windows` has, and all 140 `OFR3002`. On `-windows`, the
  Windows Forms types .NET keeps only as shims that throw (`MenuItem`, `ContextMenu`, `DataGrid`:
  `[Obsolete]` `WFDEV006`) are `OFR3003`. Open Live Writer has 590 `OFR3001` now (none from
  Windows Forms, 13 from `System.Drawing`), no `OFR3002`, and 165 `OFR3003` for the shims. The
  guide's `csproj modernize --tfm` suggestion uses the same rule.
- `audit api` compiles `packages.config` projects against their packages as the target sees them
  (ADR 0040). It passed only the assets file's packages, which a `packages.config` project does
  not have, and referenced every `HintPath` DLL as it was, so the .NET Framework
  `System.Web.Mvc.dll` went into the `net10.0` compilation: on SmartStoreNET 4.2 no finding named
  ASP.NET MVC or Web API although 363 files use MVC. Every package `packages.config` lists
  (development dependencies aside) is now resolved for the target, and the `HintPath` DLLs in
  their `packages/<Id>.<Version>/` folders are left out: SmartStoreNET has 11,057 `OFR3001`
  instead of 1,439, 6,989 of them `System.Web.Mvc` and 714 `System.Web.Http`, and `OFR3011`
  names the packages without target support in 23 projects (the audit takes 201 s instead of
  142 s). This was the `audit api` part of DotNetNuke's P1 #7.
- A library that references a test framework's assembly (`nunit.framework`, `xunit`, the MSTest
  assemblies, usually a DLL checked in and referenced by `HintPath`) is a `test` project, with the
  evidence `Reference nunit.framework + OutputType=Library` (ADR 0038). NHibernate 4.1's three
  NUnit projects were `library`, so `audit dead-code` called 249 NUnit fixtures dead and `guide`
  proposed moving the tests out of the test projects. The rule comes after the web rules, so a web
  application project that references a test framework stays `web`.
- `deps resolve-dlls` takes a DLL's package and version from the `packages/<Id>.<Version>/`
  folder its `HintPath` goes through when the project's `packages.config` lists it, as the new
  `packagesConfig` resolution, and leaves it alone. It matched by assembly version before and
  picked the lowest package version shipping it, so `--apply` would have written 184 wrong
  versions on DotNetNuke (Newtonsoft.Json 13.0.3 as 13.0.1, Castle.Core 5.1.1 as 5.0.0), and it
  reported DLLs of packages that support the target (NUnit 4.2.2, BouncyCastle 1.9.0) as
  blockers (OFR1404): 172 blockers before, 6 now. `--apply` no longer adds `PackageReference`
  items to `packages.config` projects. The result's `summary` has `packagesConfig`.
- `redirects sync` counts the packages `packages.config` deploys (the application's and those of
  the projects it references), read from the solution's `packages/` folder. It called the
  redirects of every `packages.config` application stale, and `--prune` would have removed live
  ones (Newtonsoft.Json, BouncyCastle) from DotNetNuke's `web.config`.
- `redirects sync` skips an application whose model is partial, itself or through a project it
  references (new `OFR1506`). On a fresh DotNetNuke checkout on Linux, module projects whose
  evaluation failed had no references in the model, so `--prune` would have removed their live
  Newtonsoft.Json and BouncyCastle redirects.
- `redirects sync` never writes a redirect down to an older deployed version than one referenced
  (new `OFR1505`, warning), and a configuration file naming an assembly twice no longer ends the
  command with an exception.
- `deps audit` no longer calls a package Windows-only because its assembly references
  `Microsoft.Win32.Registry`, which ships with .NET on every OS (NUnit was OFR1004). Cached
  inspections are recomputed.
- `schemas/v1/workspace.json` accepts the `web-targets` Windows-only build step that `OFR0116`
  records.
- `audit api` no longer blames the wrong API when a type is missing on the target. Roslyn
  reports a missing base type (a Web Forms `UserControl` or `Page`) at every name looked up
  inside the derived class, and a missing parameter type at every call of the method, so
  `System.Convert`, `System.Exception`, and the solution's own types were reported as
  "does not exist on the target". A name that still exists on the target is no longer a
  finding. On DotNetNuke 9.13 this removed about 5,200 false findings, among them 1,240
  against `mscorlib` and 4,060 against DotNetNuke's own assemblies.
- `audit api` references a Visual Basic project from a C# project's target compilation as
  recorded, instead of leaving it out and reporting every type used from it as missing.
- `audit dead-code` reads ASP.NET markup. A class a page or control names in `Inherits`
  (and a handler's `Class`, a Razor view's `@model`) is used, not dead: on DotNetNuke the page
  class behind `Default.aspx` was a high-confidence candidate. Other names in markup, `Page_`
  handlers that `AutoEventWireup` calls by name, `Application_` handlers of `Global.asax`, types
  a plugin host finds with `typeof(X).IsAssignableFrom(t)`, and names in XML under other
  extensions (plugin manifests) now rate `low`.
- `audit dead-code` no longer stops at a file it cannot read (OFR0099); it lists it under
  `skipped`. Build output is left out in any letter case (`Bin/`), so documentation XML copied
  there no longer counts as a mention.
- `plan`, `graph`, `report`, and the guide no longer count a standard, modern, or dual project
  as `done` when a framework-only project sits behind the references its portable targets use.
  It is `blocked` by those projects and gets a wave after them (ADR 0036). On DotNetNuke 9.13,
  two `netstandard2.0` projects referencing `net472` legacy projects, and a
  `netstandard2.0;net472` project referencing three unconditionally, were wave 0 and `done`
  while their `blockers` listed framework-only projects. A dual project's `net4x`-only
  references no longer block it or the projects that reference it.
- `doctor`'s reference assemblies check no longer passes for legacy (non-SDK) projects outside
  Windows because the package is in the cache: the SDK gives it to SDK-style projects only. It
  now warns (`OFR0018`) until the compile-only block has its legacy section.
- The evidence of Windows-only build steps in `scan`'s messages uses repository-relative paths
  (a `PostBuildEvent` quoted the checkout's absolute path).
- `schemas/v1/workspace.json` accepts the `aspnet-compiler` Windows-only build step.
- `csproj modernize` keeps item attributes: metadata written as attributes and `Condition`.
  A source generator's `ProjectReference` (`ReferenceOutputAssembly="false"
  OutputItemType="Analyzer"`) became a plain reference, so the generator no longer ran.
- `csproj modernize` keeps a single-target project's output where the legacy project wrote it
  (`AppendTargetFrameworkToOutputPath=false`); the SDK's `bin/net472/` broke `AfterBuild` copies
  and sibling projects' `HintPath`s into `bin/`.
- `csproj modernize` no longer calls a conversion failed when the converted project's build
  stops only at NuGet audit (known vulnerabilities, made errors by `TreatWarningsAsErrors`): 42
  of the 43 failures on DotNetNuke. They are reported as `OFR4305` and verification runs with
  audit off.
- `move plan` names the first compiler error in `OFR2103`'s message (it was only in
  `data.details`), and tells the destination's warning policy from portability: a file whose
  only errors are warnings the destination treats as errors is `OFR2112`. On DotNetNuke, 287 of
  389 `OFR2103` exclusions were CS1591 (missing XML comment) in a destination that builds its
  documentation with warnings as errors.
- `move plan` no longer plans moves that rely on an `InternalsVisibleTo` item a destination
  ignores: an SDK-style project with `GenerateAssemblyInfo=false` (a shared `SolutionInfo.cs`)
  does not turn the item into an attribute, so verification failed with CS0122 and rolled back.
  Files that need internals then stay, with the reason; the same for a strong-named assembly or
  a legacy project without `Properties/AssemblyInfo.cs`.
- `codemod run` reports a skip reason once per project, with the number of sites, instead of at
  every site (367 identical notices for `http-context` on a System.Web project); the result
  still lists each site.
- `doctor`'s global.json check names the least permissive `rollForward` that selects an
  installed SDK. With `latestMinor` and only SDK 10 installed it suggested `latestFeature`, which
  is stricter; its message no longer embeds a cut-off line of `dotnet`'s output.
- `doctor`'s CPM check reports `packages.config` projects (`OFR1303`) only once central package
  management is in use; it raised 64 warnings on a repository without a `Directory.Packages.props`.
- `OFR0101` names the referenced project that failed when MSBuild never evaluated a project,
  instead of "no evaluation for it in the build log".
- `OFR0130` counts the errors per code (`CS2001 ×99, MSB4019 ×37, ...`) in its message and
  `data.byCode`.
- `scan` writes no ledger snapshot when the build failed, so `report`'s trend no longer shows
  a partial model as a drop in framework code (`ledgerSnapshot` is null).
- `audit` and `audit dead-code` report a project they cannot read (Visual Basic, F#, or no
  compiler call) as `OFR3012` instead of listing it silently under `skipped`.
- Compilations of legacy (non-SDK) Visual Basic projects rebuilt from the compiler log get
  `mscorlib` from the recorded `/sdkpath`; `vbc` adds it by itself, so the log did not name it
  and nothing in them bound.
- A legacy (non-SDK) project's `assemblyReferences` no longer lists `System.Core`, which
  MSBuild adds to every legacy project (`AdditionalExplicitAssemblyReferences`), declared or not.
  An evaluation on Windows listed it for a project that does not declare it and one on Linux did
  not, so the same project had a different model on each.

## [0.16.0] - 2026-09-28

### Added
- `OFR0116` (build step needs Windows: ASP.NET web application targets): a project that imports
  `$(VSToolsPath)/WebApplications/Microsoft.WebApplication.targets` from Visual Studio, such as
  every `MSBuild.SDK.SystemWeb` project, or that sets `MvcBuildViews=true`. It is found from the
  import, or from the MSB4019 error when evaluation stopped at that import, so `scan` and `doctor`
  now name the cause instead of reporting a partial `library` and "No project needs Windows".
- The compile-only block (`doctor --fix`, offered by `init`) has a second section for ASP.NET
  (System.Web) projects, outside Windows only: the web targets come from the
  `MSBuild.Microsoft.VisualStudio.Web.targets` package (an implicit reference, so it needs no
  `PackageVersion` under central package management and stays out of package analysis),
  `MvcBuildViews` is off, and the Web Deploy targets, which break `Clean` and rebuilds under
  .NET's MSBuild, are not imported. A file with the first section from an earlier version gains
  only the new one. ADR 0030; `docs/compiling-on-macos.md` explains it.
- The `systemweb` fixture: an `MSBuild.SDK.SystemWeb` site, built by tests before and after the
  block.
- `init`'s result (`schemas/v1/init.json`) has `values.verifyCommand` and `values.cpmScope`.

### Changed
- The HTML graph (`graph --format html`) routes edges orthogonally around the project boxes
  instead of drawing curves through them (ADR 0033). An edge that skips layers gets a lane in
  each layer it crosses; edges of one kind into one project share their lanes and arrive as one
  line; tracks in each gap are ordered so that edges going the same way do not cross; rows are
  ordered for fewer crossings and placed so long edges run straight. Drawings are larger and
  orderly. Hovering a project highlights its edges. `GraphLayoutTests` runs the page's layout
  with Node.js on the fixtures and a generated ninety-project solution and checks that no edge
  crosses a box.
- The `init` interview explains each question in a line above it and checks typed answers
  (ADR 0032). The verify choices say what `build`, `command`, and `none` do, and `command` asks
  for `verify.command`, which was never asked before. The central package versions question says
  nothing is written until `deps consolidate --cpm`, suggests the path consolidation would use for
  the chosen solution, and takes a path from the repository root (a folder gets
  `Directory.Packages.props`, a bare name is written with `scope: repo`). A pin asks for a NuGet
  package id, a version, an existing project or none, and a reason, with examples, and refuses
  answers that are not one. `offramp.yml`'s comments for `verify`, `deps.pins`, and `deps.cpm`
  say the same.
- `docs/ROADMAP.md` marks M0 through M15 released: they first shipped in v0.15.0.
- CI skips its build, test, container, VS Code extension, and pack jobs for a change that only
  adds or edits documentation: top-level Markdown files and `docs/`, except
  `docs/diagnostics.md` and `docs/spec/03-configuration.md`, which tests read. Deleting or
  renaming a file, or any other path, runs everything. `eng/ci-changes.sh` decides; the
  changelog check still runs.

### Fixed
- `deps consolidate` on projects already under central management wrote to the nearest
  `Directory.Packages.props` even when the projects import another file, as `--cpm` sets them up
  when OFR1301 fires. After such a conversion, the next consolidation edited the unrelated root
  `Directory.Packages.props` (or reported it without writing). It now uses the props file with
  `PackageVersion` items the project imports, then the recorded `DirectoryPackagesPropsPath`, then
  the nearest `Directory.Packages.props` (ADR 0031).
- `deps.cpm.file` with a folder (`eng/Packages.props`) is repository-relative, as the spec's
  example shows; it was placed under the solution's folder, so `init` answers doubled up
  (`apps/Legacy/apps/Legacy/Directory.Packages.props`). A bare name still goes where
  `deps.cpm.scope` says.
- `deps consolidate --cpm` checks a new `Directory.Packages.props` for projects outside the
  solution below it before creating it (OFR1301), not only existing ones, so a repository with
  unrelated projects no longer gets a root file that reaches all of them; the file is named
  `<Solution>.Packages.props` instead and the solution's projects opt in. Opt-in also covers a
  default-named file that is not in a folder above every project.
- `init` detected a root `Directory.Packages.props` but wrote it as a bare name with
  `scope: solution`, which pointed next to a solution in a folder instead.

## [0.15.0] - 2026-09-27

### Added
- Editor integration (`docs/spec/commands/ide.md`, ADR 0029), for developers who are not on the
  migration: new code in .NET Framework-only projects stays migration-friendly.
  - `Offramp.Ide`: for each file, the lines that differ from the merge base of `HEAD` and
    `ide.newCode.base` (`auto`: `origin/HEAD`, else `HEAD`); the `audit api` findings on those
    lines; the types the file declares; and whether the file moves as it is to each counterpart,
    or the code that says why not. Counterparts are the portable projects a .NET Framework
    project can reference and that do not depend on it. They come from the new top-level
    `projectMap` (names or paths, `*`/`?`, `{name}` conventions), else from the portable projects
    the project already references. The engine reads the recorded compilations with the
    editor's text laid over them, so source edits need no scan.
  - `offramp ide check [--file PATH ...] [--base REF] [--scope lines|files|all]`: those reports
    as JSON (`schemas/v1/ide-check.json`); with `--fail-on`, a gate for pull requests. Also an
    MCP tool (`offramp_ide_check`).
  - `offramp ide serve`: a Language Server Protocol server. It sends diagnostics on new code,
    `Move to …` lenses on files that move as they are, quick fixes and refactorings, and
    `offramp.move` (plan, confirm, `move apply` with verification and a journal, open the moved
    file). It also sends `offramp/status` and answers `offramp/fileReport`. It offers to run
    `offramp scan` when there is no model. After its own moves it needs no new scan, even when a
    move edited a project file.
  - `editors/vscode`: the VS Code extension. It is on by default only in repositories with an
    `.offramp` folder, and `offramp.enabled` (`auto`, `on`, `off`) sets it per user or per
    workspace. It uses the repository's local tool, else the global tool, else
    `offramp.server.path`. It has a status bar item and commands, and CI builds the `.vsix`.
    Stable releases publish it to the Visual Studio Marketplace as `AndrewBenz.offramp`,
    signed in with Microsoft Entra ID through GitHub's OIDC token (`vsce publish
    --azure-credential`; no stored token, since Azure DevOps PATs are retired), and to Open VSX
    when `OVSX_PAT` is set. The **Marketplace identity** workflow, run by hand during setup,
    prints the identity's Marketplace member ID and checks that it can publish.
  - `MovePlanner.Assess` (the planner's rules for one file with `--co-move none` and nothing
    but a move) and an `ICompilationSource` for the planner and the `audit api` target build.
  - Configuration: `projectMap`, `ide.newCode.base`, `ide.newCode.scope`,
    `ide.implicitCounterparts` (they appear in every envelope's `effectiveConfig`).
  - Diagnostics: OFR6001 new type could live in its counterpart, OFR6002 project map entry does
    not resolve, OFR6003 more than a move, OFR6004 counterpart cannot take the project's code,
    OFR6005 no counterpart, OFR6006 file not in the workspace model, OFR6007 new-code base
    unavailable, OFR6008 file has unsaved changes, OFR6009 project needs a new scan.
  - Projects that need a scan are announced, not skipped: a file report's `scan` says when its
    project was added after the scan, has no recorded compilation, or changed since. The server
    pushes it per open file (`offramp/fileStatus`), and VS Code's status bar shows
    `Offramp: scan <project>` with a click to scan. The first edit in such a project asks once
    per session whether to scan; `ide check` reports OFR6006 or OFR6009 once per project.
  - Fixture: `ide-counterpart`. Roadmap: M15 (this), M16 Visual Studio, M17 Rider.
- `offramp guide [--run|--done|--skip|--reset STEP] [--project P] [--apply]`: a walk through the
  migration for people who have not done one before. A fixed checklist of Offramp's commands in
  four stages (get set up; see what you have; tidy up while still on .NET Framework; port, a wave
  at a time), each with a plain-language reason and one simple fact about the repository that
  decides whether it applies. On a terminal it explains the next step and asks what to do (run it,
  mark it done, skip it, stop), and asks which step first when several are open; otherwise it
  reports where things stand. The first run runs `doctor` and creates `.offramp/guide.json`
  (sorted records, no timestamps or absolute paths; `schemas/v1/guide-state.json`). Steps run in
  process through the same command tree; with `--json` each step's envelope is embedded in the
  result. Steps that change the repository are dry runs unless the guide is started with
  `--apply`. `init`, `scan`, `compile-only`, and `port` are done when the repository shows it, and a
  stale workspace model brings `scan` back first. Also an MCP tool (`offramp_guide`). Schema:
  `schemas/v1/guide.json`. Diagnostics: OFR0040 progress file unreadable, OFR0041 step needs a
  project, OFR0042 step did not complete, OFR0043 step cannot be skipped or marked done.
  ADR 0028.

### Changed
- Releases push to nuget.org with trusted publishing: `NuGet/login` exchanges GitHub's OIDC
  token for an API key that lasts an hour, so the `NUGET_API_KEY` secret is no longer used. The
  `nuget` environment needs the variable `NUGET_USER` and nuget.org a trusted publishing policy
  (`docs/RELEASING.md`).
- Moving a file git does not track (for example one created after the last commit) is a plain
  move instead of a failed `git mv`; its bytes still do not change.
- `move plan` maps a source project's files to its compiled trees by path lookup instead of a
  scan per file, which matters for projects with thousands of files.

### Fixed
- The release workflow's tests no longer fail on Windows without a failing test. They use CI's
  300-second test host connection timeout and hang guard, leave Docker tests to CI's container
  job, and on Windows run one test assembly at a time, because a test process started while
  others ran could miss xunit's fixed 60-second limit. The first two `v0.15.0` runs failed this
  way, before anything was published.

## [0.14.0] - 2026-09-26

### Added
- `offramp mcp serve [--allow-apply] [--root PATH]`: Offramp as a Model Context Protocol server
  over stdio (`Offramp.Mcp`, the official C# SDK). One tool per command
  (`offramp_<group>_<command>`) with an input schema generated from the command's options, run
  in process with `--json` so the answer is the command's envelope; progress as MCP progress
  notifications; `offramp_help` with the suggested workflow; resources `offramp://workspace`,
  `offramp://ledger`, `offramp://plan/<id>`, `offramp://diagnostics/<code>`. Every call is a dry
  run unless the server was started with `--allow-apply`; paths outside `--root` are refused
  (OFR9101).
- `Offramp.Llm`: `ILlm` with adapters for any OpenAI-compatible endpoint (model discovery,
  schema-constrained JSON) and the Anthropic Messages API (a forced answer tool), with
  timeouts, retries, and token logging at `--verbose`.
- `--llm` gates at the permitted sites, each with its deterministic fallback (OFR9001 when a
  call fails or an answer is not usable): `naming` for `seams` interface names and
  `extract interface` without `--name`; `ranking` for `deps audit` when several package-map
  entries match; `summarizing` for the summary paragraph of `report --format markdown`;
  `classifying` for low-confidence `audit dead-code` candidates (evidence only). What the model
  produced is marked `"source": "llm"`.
- An architecture test for the layering rule: nothing but `Offramp.Cli` references
  `Offramp.Llm` or `Offramp.Mcp`, and `Offramp.Core` references nothing in `src/`.

### Changed
- `report --format markdown` starts with a summary paragraph (a template sentence with the
  headline numbers, or the model's with `--llm`), and its result has `summary`.
  Migration: tools that compare markdown reports see one new paragraph after the title; the
  JSON result only gains a property.
- The package map keeps every matching entry; `deps audit` still shows the first (exact before
  prefix, configuration before the rule file) unless `--llm` ranks them.

## [0.13.0] - 2026-09-26

### Added
- `offramp csproj modernize --project P|--all [--tfm] [--nullable] [--accept-diff]`: legacy
  (non-SDK) C# projects become SDK-style without `try-convert`: properties the SDK sets are
  dropped, Compile/resource/None items become globs when those give the same files (else the
  list stays, OFR4301), packages.config becomes PackageReference, build events become targets
  (OFR4302), and AssemblyInfo attributes the SDK generates are removed. Every run builds the
  converted projects in a scratch copy and compares each target's compiler inputs (sources,
  references, resources) with the original build; a difference is OFR4303 and blocks
  `--apply` unless `--accept-diff`. Web application projects are left alone (OFR4304).
  Schema: `schemas/v1/csproj-modernize.json`.
- `offramp config convert --project P [--out] [--sections] [--shim]`: App.config or
  Web.config to `appsettings.json` (appSettings as root keys, ConnectionStrings, custom
  sections shaped by their section classes) with an options class per section, transforms
  to `appsettings.{Environment}.json`, and with `--shim` a `ConfigurationManagerShim`.
  OFR4401–4406. Schema: `schemas/v1/config-convert.json`.
- Codemod `config-manager-shim` (OFRM014, opt-in): points the ConfigurationManager call
  sites `config-manager` cannot inject into at the shim.
- `offramp web inventory --project P [--format table|json|markdown]`: controllers, actions,
  verbs, attribute and convention routes, filters, areas, modules, handlers, Global.asax,
  bundles, Web Forms, session and output-cache use, web.config settings, and the System.Web
  API surface per file. Schema: `schemas/v1/web-inventory.json`.
- `offramp web scaffold --project P --new DIR [--proxy yarp|none] [--adapters] [--legacy-url]`:
  a strangler-fig ASP.NET Core project. Controller actions that port are copied with mapped
  names; the project is compiled in memory and actions the compiler rejects stay with the
  legacy application, with reasons. Convention routes become `MapControllerRoute`; YARP
  forwards everything else to the legacy application (or `--proxy none` lists the paths for
  an ingress); `--adapters` shares session and authentication through the System.Web
  adapters. Modules become middleware stubs, handlers endpoint stubs (OFR4202), Web Forms
  stay behind the proxy (OFR4201); OFR4203 when the project does not compile, OFR4204 when
  the folder has files. Schema: `schemas/v1/web-scaffold.json`.
- `offramp move extract --from P --types T1,T2 | --files GLOB --new NAME [--tfm] [--dir]`:
  creates a project from a template (the source's language settings, analyzers, and framework
  references), plans the move into it with `move plan`'s rules against an in-memory
  compilation, and with `--apply` creates, moves, and verifies in one journal that
  `move rollback` undoes. OFR2006–2008. Schema: `schemas/v1/move-extract.json`.
- Fixtures `legacy-csproj` and `mvc5`, which build on every OS.

### Fixed
- Legacy projects now get their compiler call in the workspace model, so commands that load
  their compilation (`web inventory`, `csproj modernize`, `config convert`) find it.
- Journals record deleted files, so `move rollback` restores them.

## [0.12.0] - 2026-09-26

### Added
- Codemods (`docs/spec/commands/codemod.md`): OFRM001–013 as Roslyn analyzers
  (`Offramp.Analyzers`, netstandard2.0) and code fixes (`Offramp.Analyzers.CodeFixes`):
  `sqlclient`, `config-manager`, `http-context`, `webclient`, `javascript-serializer`,
  `binaryformatter-clone`, `thread-abort` (experimental), `process-start-url`,
  `string-comparison` (opt-in), `codepages`, `timezone-ids`, `service-controller`, and
  `assemblyinfo`.
  - A site that is not rewritten is still reported, with the reason.
- `offramp codemod list`: the catalog with each codemod's packages.
- `offramp codemod run --mod NAME|ID|all`: runs the fixers over the recorded compilations.
  - A dry-run diff by default.
  - Adds the packages the rewritten code needs. Framework- and modern-only packages are
    conditioned on `TargetFrameworkIdentifier`; central package management is honored.
  - `assemblyinfo` moves attribute values to project properties.
  - `--apply` writes through a journal and verifies with a build (`--verify end|none`),
    rolling back on failure.
  - `--format-mode` delegates to `dotnet format analyzers --diagnostics OFRM###` in
    projects that reference the package.
  - Diagnostics: OFR4501 (site skipped), OFR4502 (unknown codemod), OFR4503 (experimental),
    OFR4504 (source changed since the scan), OFR4505 (package not added), OFR4506 (no
    analyzer package), OFR4507 (verification failed, rolled back), OFR4508 (dotnet format
    failed), OFR4510 (SqlClient encrypts by default).
  - Schemas: `schemas/v1/codemod-list.json`, `schemas/v1/codemod-run.json`.
- The `Offramp.Analyzers` NuGet package: both assemblies as analyzers, every rule a
  suggestion by default, and a `sample.editorconfig`. It is packed by CI and by the
  release workflow.
- Fixture `codemods`. ADR 0025.

### Changed
- `docs/spec/commands/codemod.md`:
  - `binaryformatter-clone` applies to the serialize-then-deserialize idiom itself instead
    of waiting for `audit serialization`.
  - `config-manager --shim` is deferred until `config convert` (M12).

### Fixed
- `offramp scan` builds with `--no-incremental`. A scan right after a build used to record
  no compiler calls for up-to-date projects, which left them `partial`.

## [0.11.0] - 2026-09-26

### Added
- `offramp service --project P`: turns a Windows service into a worker project for the
  generic host.
  - Detects `ServiceBase` classes (lifecycle overrides, installer settings: account, start
    type, dependencies, description) and Topshelf `HostFactory.Run` configurations, with
    their timers, logging, and `ConfigurationManager` keys.
  - Writes `NAME.Worker` (`netN.0`) with one `BackgroundService` per service.
    - `ServiceBase` code is lifted as text into the worker's methods.
    - A `System.Timers.Timer` that only ticks becomes a `PeriodicTimer` loop that honors
      the stopping token.
    - `EventLog.WriteEntry` becomes logging.
    - Other `ServiceBase` uses stay in `#if OFFRAMP_SERVICEBASE` regions (OFR4102).
  - Topshelf services are wrapped with their start and stop calls.
  - The code a service uses from its project is compiled as links, and the worker is
    compiled in memory first (OFR4106).
  - `--host linux|windows|both`: `AddWindowsService`, `install.ps1`/`uninstall.ps1`
    (`sc.exe`), and `AddSystemd` with a systemd unit.
  - `--health` (`/health` from the workers' heartbeats), `--dockerfile` (multi-stage,
    non-root), `--k8s` (ConfigMap, Deployment, probes, grace period), and
    `--logging json-console|simple`.
  - Removals are reported (installers, `System.ServiceProcess`, Topshelf,
    `requestedExecutionLevel`).
  - Diagnostics: OFR4101–4105 (pause and custom commands, compatibility regions,
    multiple services, service dependencies, session and power events), OFR4107 (no
    service), OFR4108 (output exists). Schema: `schemas/v1/service.json`.
- CI job `Container (service image)`: builds the generated Linux image on ubuntu and calls
  its health endpoint (`Category=Docker`, `OFFRAMP_DOCKER_TESTS=1`).
- Fixture `windows-service`. ADR 0024.

### Changed
- `docs/spec/commands/scaffold.md`: `service --in-place` is deferred until `csproj
  modernize`; the removal list is reported instead.

## [0.10.0] - 2026-09-26

### Added
- `offramp seams --project P`: the smallest boundary around code that cannot port.
  - Unportable symbols come from `audit api` findings (`--unportable-from audit`) or from
    `--symbols` and `seams.unportableSymbols`.
  - Taint spreads through inheritance and public signatures, never through calls.
    Reference cycles move together.
  - The minimum cut closest to the taint gives the seams. Each seam lists its callers and
    the members they call (call sites, wire-friendliness, static), a proposed interface
    name, a score, and whether it is an articulation point.
  - OFR4001 no seam, OFR4002 member not wire-friendly, OFR4003 static member on the
    boundary.
  - `--format table|json|dot|html` (the graph with taint and cut edges), `--max-cut`.
    Schema: `schemas/v1/seams.json`.
- `offramp extract interface --project P --type T` (or `--from-seams seams.json#seam-1`).
  - Writes `IName.cs` next to the type and adds the interface to the type.
  - Retypes callers' injected constructor parameters, fields, and properties when
    everything they do goes through the interface.
  - Reports callers that create the type with `new` (OFR4010).
  - The edit is compiled in memory before anything is written: OFR4011 type not found,
    OFR4012 would not compile, OFR4013 seam not found.
  - `--di microsoft|autofac|none` prints the registration. Dry run until `--apply`
    (journal, `move rollback`). Schema: `schemas/v1/extract-interface.json`.
- `offramp remote --interface I`: an HTTP boundary for a seam interface.
  - Boundary audit: OFR4002 errors unless `--skip-member`; OFR4020 per synchronous member.
  - A netstandard2.0 contracts project with routes, requests, DTOs, and
    `RemoteInvocationException`.
  - A Windows host: net10.0-windows minimal API when the implementation's files compile
    for it with Microsoft.Windows.Compatibility (checked by trial compilation, linked
    sources, `/health`, problem details). Otherwise the net48 OWIN/Web API 2 fallback
    (OFR4022).
  - A client with a typed `HttpClient`, the local/remote DI switch, `--async-variant`,
    and `--serializer stj|newtonsoft`.
  - `--container` adds a Windows Dockerfile and a Kubernetes Deployment and Service.
  - Only new files are written (OFR4021 when a target directory exists); OFR4023 when the
    interface or implementation is not found. Package versions are pinned in
    `rules/scaffold-packages.yml`. Schema: `schemas/v1/remote.json`.
- `Offramp.Scaffolding`: generators built from raw string templates.
- Fixture `seams`. ADR 0023.

### Changed
- `docs/spec/commands/seams.md`: `--transport grpc` is planned, not in v1 (OFR4030 stays
  reserved), and `extract interface --rewrite-new` is deferred. New options:
  `--from-seams` for extract; `--project`, `--contracts-dir`, `--skip-member`, and
  `--async-variant` for remote.

## [0.9.0] - 2026-09-26

### Added
- `offramp audit dead-code`: types and members nothing in the solution references, from one
  index of every name the semantic model binds (by documentation ID, including the members the
  compiler calls for `foreach`, `await`, and initializers). Each candidate (OFR3401) has a
  confidence: `high` for private and internal code and public code in assemblies that are not
  packed, `medium` for public code in packable projects or ones in
  `deadCode.externalConsumers`, and `low` for anything reflection could reach (names in strings,
  resources, or configuration; convention registrations; controllers and handlers; serializer
  attributes; entry points; public properties). Candidates list their evidence and the lines
  they span; the summary counts removable lines per confidence. `--include-tests` reports code
  only tests use (OFR3402). `--scope public|all`, `--min-confidence`, `--project`,
  `--format table|json|markdown`.
  Schema: `schemas/v1/dead-code.json`.
- `offramp audit api-compat`: builds two targets of a project (or the working tree and
  `--baseline REV` in a scratch work tree) into `.offramp/cache/` and runs Microsoft's ApiCompat
  tool at the SDK's version in strict mode, reporting members missing from one side (OFR3501)
  and other incompatibilities (OFR3502); nothing to compare is OFR3503 and a tool or build
  failure OFR3504. Schema: `schemas/v1/api-compat.json`.
- Fixture `dead-code`. ADR 0022.

### Changed
- CI gives the test host's data collector five minutes to connect
  (`VSTEST_CONNECTION_TIMEOUT`); a slow Windows runner aborted a run after the default 90
  seconds.

## [0.8.0] - 2026-09-26

### Added
- `offramp audit api|behavior|serialization|native`: read-only code audits driven by rule packs
  (`rules/audit-*.yml`: core, web, desktop, data, serialization, native).
  - `audit api` compiles each .NET Framework project against the target's reference
    assemblies, resolved by the SDK and NuGet in a scratch project, and reports missing APIs
    with their assembly mapping (OFR3001), Windows-only APIs (OFR3002), APIs that throw
    (OFR3003), and removed technologies (OFR3004–3009), plus a porting ledger per project and
    the top namespaces. Packages without target support are left out and named (OFR3011); a
    target that cannot be restored is OFR3010.
  - `audit behavior`: OFR3101–3120 (culture, code pages, Windows paths and time zones, the
    registry, ambient ASP.NET context, `Process.Start`, SqlClient, floating-point formatting,
    legacy networking, `app.config` runtime settings, and more).
  - `audit serialization`: BinaryFormatter and relatives (OFR3201, an error from .NET 9); each
    use classified as a transient deep clone (OFR3202) or persisted/transported data with its
    evidence (OFR3203); the types carried (OFR3204); `[Serializable]` types nothing serializes
    (OFR3205); legacy JSON and XML serializers (OFR3210, OFR3211).
  - `audit native`: P/Invoke inventory (OFR3301), ANSI string marshalling (OFR3302),
    `LibraryImport` candidates (OFR3303), COM (OFR3310), SEH interop (OFR3320).
  - `--format table|json|sarif|markdown` (SARIF 2.1.0 for code scanning), `--group-by`,
    `--all-locations`, `--pack`, `--project`. Severity overrides from `offramp.yml` mark findings
    `overridden`; each rule with findings in a project is one diagnostic, so `--fail-on` gates
    on audits. Schema: `schemas/v1/audit.json`.
- `offramp ifdef report|wrap|strip`.
  - `report`: `#if` regions and guarded lines per symbol and project.
  - `wrap --findings audit.json`: wraps the statement or member behind each `audit api`
    finding in `#if NETFRAMEWORK` (or `--symbol`), inserting whole lines only; members that
    code on every target needs are left for a real port (OFR3601), stale findings are skipped
    (OFR3602).
  - `strip --symbol S --keep true|false`: removes the regions a symbol decides, keeping the
    selected branch; regions that also depend on other symbols stay (OFR3603).
  - Dry run by default; `--apply` writes through a journal. Schemas: `ifdef-report.json`,
    `ifdef-wrap.json`, `ifdef-strip.json`.
- Fixture `behavior`: one class per audit rule with `Positive` and `Negative` members.

### Changed
- `rules/framework-assemblies.yml` maps `mscorlib` and `System.Activities`, so `deps gac` and
  `deps audit` report a mapping for them instead of `unknown`.

## [0.7.0] - 2026-09-26

### Added
- `offramp deps consolidate (--package ID | --all | --family PREFIX)`: one version per package
  across the solution.
  - Constraints come from direct references, every resolved package's dependency ranges (each
    with its chain), the chosen versions' own dependencies, and pins.
  - The version is the lowest (or `--prefer newest`) that supports every target framework of
    the projects using it.
  - Families share the highest member version. Pins keep their project on its version
    (`VersionOverride` under central package management).
  - Versions are written in place, into the existing central file, or, with `--cpm`, into a
    new one (named after the solution, with per-project opt-in, when projects outside the
    solution would inherit it).
  - Nothing is applied unless NuGet's own restore of the proposal, in a scratch worktree,
    reports no new NU1605, NU1107, NU1608, NU1010, or restore error.
  - Schema: `schemas/v1/deps-consolidate.json`.
- `offramp redirects sync [--app PROJECT] [--prune]`: binding redirects for .NET Framework
  applications, computed from the assemblies the resolved packages deploy.
  - Redirects are added, changed, or (with `--prune`) removed entry by entry; every other byte
    of `web.config`/`app.config` stays.
  - Schema: `schemas/v1/redirects-sync.json`.
- `offramp deps resolve-dlls [--project PROJECT]`: loose `HintPath` references become a
  `ProjectReference` (the DLL is a project's output) or a `PackageReference`. The package must
  ship the assembly with the same public key, at the referenced version or higher, for every
  target framework. .NET Framework DLLs with no replacement are reported as blockers
  (`schemas/v1/deps-resolve-dlls.json`).
- Package inspection records dependency groups and assembly identities (cache format 2).
- Diagnostics OFR1200, OFR1203, OFR1210–1212, OFR1220, OFR1401–1404, and OFR1501–1504.
- Fixtures `loose-dlls` (stub DLLs from a generator) and `cpm-shadowing`, both in the scan
  snapshots.
- ADR 0020 (consolidation decides and restore verifies, the opt-in import, loose DLL candidates,
  redirect rules).

### Changed
- Unified diffs print removed lines before added ones, as git does.

### Fixed
- Commands no longer wait indefinitely for the output of a `dotnet` or `git` process that has
  exited while a process it started (an MSBuild node, the compiler server) still holds its
  output pipes. Output is drained for at most ten seconds after exit, and MSBuild node reuse
  is off for the processes Offramp runs.
- Scratch copies (`verify`, restore verification) live under the canonical temporary directory,
  so paths in tool output map back to the repository on macOS, where `/var` is a link to
  `/private/var`.

## [0.6.0] - 2026-09-26

### Added
- `offramp move plan --from SRC --to DEST (--files GLOB... | --files-from LIST | --all)`: a
  deterministic, reviewable plan for moving files between projects, with no repository
  changes. It partitions what each file uses with the semantic model, brings along what it
  needs (`--co-move closure`, or excludes it with `none`), and proposes project and package
  references. Cycles are rejected with their path, and packages without assets for the
  destination keep the file. Each file is proven to compile in every destination target
  framework, the source to compile without it, and projects depending on the source to still
  see moved types. Windows-only APIs are reported with the destination's own CA1416 analyzer.
  Partial types and resource pairs move together, destination `Compile Remove` patterns are
  respected, and namespace mismatches can warn or block. `--out` writes the plan
  (`schemas/v1/move-plan.json`; result `schemas/v1/move-plan-result.json`).
- `offramp move apply --plan PATH`: checks the workspace hash and every file's hash, journals
  each step, performs project edits then pure renames (`git mv`), and verifies per the policy.
  - Policies: `none`, `end`, `per-project`, or `batch:N`. Batches never split files that
    need each other.
  - On failure it rolls the whole run back, or with `--on-failure keep` leaves it for
    `--resume`, which finishes an interrupted journal.
  - `--force` applies a plan made from another workspace model.
  - Result schema: `schemas/v1/move-apply.json`.
- `offramp forwarders --from SRC --to DEST [--since REF] [--apply]`: writes `TypeForwarders.cs`
  in the source for public types that now live in the destination (read from the last scan's
  compilation, or from the source at a commit), and adds the source's reference to the
  destination. It reports strings that name moved types with the old assembly, in C# string
  literals and configuration or data files (`schemas/v1/forwarders.json`).
- Diagnostics OFR2001, OFR2003–2005, OFR2101, OFR2102, OFR2105, OFR2110, OFR2111, OFR2120,
  OFR2150, OFR2152, OFR2301, and OFR2302.
- Fixture `move-cases` (one case per planning rule, also in the scan snapshots) and a generated
  500-file `hollow` fixture for the overnight `--all` run.
- ADR 0019 (needs and batches, whole-run rollback, resumable journals, dependents, forwarders
  from the scan).

### Changed
- Journals (`schemas/v1/journal.json`) record the bytes each create or edit step writes
  (`after`) and the plan they apply (`plan`), so an interrupted run can be finished.
- A file changed between planning and applying now fails with a journal conflict instead of a
  purity violation (the rename never happens either way).

### Fixed
- `scan` no longer records the ProjectReference items the SDK adds for transitive references
  (which logs made on Windows keep with the evaluation) as a project's own references: only the
  references restore saw declared (the assets file's restore metadata) are kept.

## [0.5.0] - 2026-09-26

### Added
- `offramp move tests`: finds test code in a production project semantically (test-framework
  attributes and base types; helpers by a fixpoint over which files use which, across the
  project and its dependents, requiring test-support evidence) and moves it, byte for byte, to
  its test project: `--to`, the `<Name>.Tests` naming rule, or `--create` (a new SDK-style test
  project for the detected framework, added to the solution). Each file is proven to compile in
  the destination by trial compilation and the source to compile without it; the destination
  gets the project and package references it needs and the source an `InternalsVisibleTo`.
  Dry run by default with a unified diff; `--apply` journals, stages renames with `git mv`,
  leaves project edits unstaged, verifies, and rolls back on failure. `--include-helpers`,
  `--prune-packages` (`schemas/v1/move-tests.json`).
- `offramp move rollback --journal PATH`: undoes an applied move exactly, and refuses when a file
  changed since (`schemas/v1/move-rollback.json`, `schemas/v1/journal.json`).
- `Offramp.Refactoring`: change sets (new files, edits, renames) with a unified-diff preview, a
  journal written before every step, purity checks on every rename, and rollback; project-file
  editing with Microsoft.Build's construction model (formatting, byte order mark, and line
  endings kept); solution editing.
- Diagnostics OFR2002, OFR2050, OFR2103, OFR2104, OFR2151, OFR2201–2206, and OFR2210.
- Fixture `tests-in-prod`, also added to the scan and graph snapshots.
- ADR 0018 (helper evidence, trial compilation, rollback that refuses to clobber).

## [0.4.0] - 2026-09-26

### Added
- `offramp plan`: a leaf-first migration order with each project's framework class, blast
  radius (transitive dependents), framework-only blockers, readiness, and wave (0 already
  portable, 1 portable today, n after wave n-1; cycle members share a wave). `--frontier`,
  `--for PROJECT` (the framework-only closure to port for one project), `--waves` (grouped
  view), and `--exclude-kind` (`schemas/v1/plan.json`).
- `offramp verify`: builds the selected projects with the repository's own toolchain
  (`verify.configuration`, `verify.properties`, `noWarn`, `warnAsError`, `restore`) through one
  `dotnet build` of the solution or a generated solution filter, or runs `verify.command` with
  `OFFRAMP_VERIFY_PROJECTS`/`_TARGET`/`_CHANGESET` and merges a JSON envelope it prints.
  Errors come from the binary log, grouped by code with the first occurrence; per-project
  status; `--projects`, `--affected-by PATHS` (owners plus direct dependents), `--all`,
  `--mode build|command|none`, and `--baseline` (`.offramp/verify/baseline.json`; later runs
  fail only on errors it does not list) (`schemas/v1/verify.json`,
  `schemas/v1/verify-baseline.json`).
- Diagnostics OFR5001 (verification failed), OFR5002 (timed out), OFR5010 (new error code
  relative to the baseline), OFR5020 (finding from the verification command), and OFR5090
  (verification skipped).
- A scratch work tree helper (a detached `git worktree` of `HEAD` with working-tree files
  copied over it) for trying changes without touching the user's working tree.
- ADR 0017 (plan waves, verify selection, baselines, merged findings).

## [0.3.0] - 2026-09-26

### Added
- `offramp deps audit`: for every package in use, whether each in-use version supports
  the target, the lowest and newest versions that do, the newest version, Windows-only
  assets (with the assembly and the reason), deprecation, and a known successor, with a
  status (ok, upgrade, replace, blocked, unknown) and OFR1001–1006. Feeds come from the
  repository's `nuget.config` (or `deps.feeds`); nupkg inspections are cached under
  `.offramp/cache/packages/`. `--package`, `--project`, `--include-prerelease`, and
  `--format table|json|markdown` (`schemas/v1/deps-audit.json`).
- `offramp deps gac`: .NET Framework assembly references and their modern equivalents
  (built in, a package, the Windows compatibility pack, or none), with how often source
  uses each, from compilations rebuilt from the compiler log (`schemas/v1/deps-gac.json`).
- Rule tables `rules/package-map.yml` and `rules/framework-assemblies.yml`, and
  `deps.packageMap` in `offramp.yml` to extend the first.
- `Offramp.NuGet` and `Offramp.Analysis` projects.
- Fixture `versions` (five projects, mixed package versions, a pin, `web.config` binding
  redirects) with a recorded feed (`feed.json`, `eng/record-feed.cs`) so package tests
  never depend on nuget.org.
- ADRs 0014 (how `deps audit` searches versions) and 0015 (recorded feeds).
- `offramp graph`: the project graph as data (`--format json`, `schemas/v1/graph-document.json`),
  Graphviz DOT, Mermaid, or a single self-contained interactive HTML page (layered layout,
  search, kind and framework-class filters, focus with a depth slider, clusters, cycle list,
  SVG/PNG export, light and dark). Nodes carry readiness (ready, blocked, done), their
  framework-only blockers, and dependents; `--focus`/`--depth`/`--direction`,
  `--include-kind`/`--exclude-kind`, `--cluster`, `--highlight cycles|frontier|blockers`, and
  `--edges project`. With a format and no `--out`, stdout is the document itself, ready to
  pipe into `dot`. Diagnostic OFR0201 flags Mermaid graphs above 300 projects.
- `offramp report`: the stakeholder progress page from the committed ledger and the current
  model: headline numbers, a burn-down of lines of code by framework class across scans,
  framework class by area, applications with what is left in their closure and what to port
  next, and the projects ready to port today. `--format html` (one script-free file with SVG
  charts, light and dark, print-friendly; `--with-graph` embeds the interactive graph),
  `markdown`, or `json` (`schemas/v1/report-data.json`), `--since`, and `--title`.
  Diagnostic OFR0202 names ledger files that are not snapshots. ADR 0016.

### Changed
- `offramp slice` without `--out` now writes only the solution filter to stdout (diagnostics go
  to stderr), so `offramp slice --for Foo > foo.slnf` works.

### Fixed
- Concurrent scans in one process could read an empty build from a binary log
  (MSBuild.StructuredLogger returns each read's result through a static field); reads are now
  serialized.

## [0.2.0] - 2026-09-26

### Added
- `offramp scan`: builds the solution with a binary log (or reads `--binlog`,
  `--binlog` with `--complog`, or `--complog` alone), converts it to a compiler
  log, and writes the workspace model (`.offramp/workspace.json`,
  `schemas/v1/workspace.json`) and a ledger snapshot (`schemas/v1/ledger.json`).
  The model has project kinds with evidence, framework classes, packages and
  the resolved package graph from `project.assets.json`, assembly and COM
  references, define constants per target, Windows-only build steps, and the
  project graph with cycles and a leaf-first order. `--no-build` reuses the last
  log; `--if-stale` rescans only when needed (`schemas/v1/scan.json`).
- Logs captured on another machine or checkout (for example a Windows agent)
  are mapped onto the local checkout; CI proves a model built from Windows logs
  of `dual-target` equals the native one on ubuntu, macOS, and Windows.
- `offramp slice`: writes a solution filter (`.slnf`) or a SlnGen command for a
  project closure, with `--include-dependents` and `--include-tests`
  (`schemas/v1/slice.json`).
- `offramp doctor`: checks for model freshness, Windows-only build steps, and
  central package management hazards; `--fix` previews and `--fix --apply`
  writes the compile-only block to `Directory.Build.props`. `init` offers the
  same block on a terminal when the model shows Windows-only steps.
- `--fail-on-stale` global option: a stale model is an error instead of a warning.
- Diagnostics OFR0002–0004, OFR0021, OFR0022, OFR0101–0104, OFR0110–0115,
  OFR0120, OFR0130–0132, and OFR1301–1303.
- Fixtures `netfx-only`, `dual-target`, `cycle`, and `windows-only-build-steps`
  (with a committed binary log), each snapshot-tested.
- ADRs 0007–0012: log-reading libraries, no `scan --fast`, staleness by content
  hash, scanning logs from elsewhere, machine-independent model rules, and
  `doctor --fix`/`slice` behavior.

### Changed
- `doctor` reports two more checks (`windows-only-build-steps`, `cpm`) and a
  `fix` field; `init` results carry `compileOnlyFix`. Consumers that assert on
  the exact check list need the two new ids.
- The envelope's `solution` shows the solution the model was built from when
  none is configured.
- The workspace model schema adds `inputs`, `source.complog`, and per project
  `language`, `defineConstants`, `packagesConfig`, and `partial`
  (`docs/spec/02-workspace-model.md`).

## [0.1.0] - 2026-09-25

### Added
- Handoff pack: README, contributor contract (CLAUDE.md), specification
  (`docs/spec/`), roadmap, release process, CI and release workflows.
- Solution scaffold: `Offramp.slnx`, `global.json` (.NET 10 SDK), central package
  management, `.editorconfig`, `.gitattributes`, and `Offramp.Core`,
  `Offramp.Workspace`, `Offramp.Cli` with one test project each plus
  `Offramp.Fixtures` for shared test support.
- `offramp` global tool (`net8.0;net10.0`, `RollForward=LatestMajor`) built on
  System.CommandLine and Spectre.Console: all global options, exit codes
  (0/1/2/3/4/130), the JSON envelope (`schemas/v1/envelope.json`), NDJSON progress
  on stderr with `--json` (`schemas/v1/progress.json`), a live progress display on
  terminals, plain progress lines otherwise, and `NO_COLOR`/`TERM=dumb` support.
- `offramp doctor`: checks the .NET SDKs, `global.json` selection, the target,
  .NET Framework reference assemblies, git, the repository, `offramp.yml`, and the
  workspace model, each with a remedy (`schemas/v1/doctor.json`).
- `offramp init`: writes `offramp.yml` with detected values (interview on a
  terminal, `--defaults` without), adds Offramp's state to `.gitignore`, previews
  with `--dry-run`, and never overwrites without `--force` (`schemas/v1/init.json`).
- `offramp --version` and `--help` with examples for every command.
- `offramp.yml` loading with the documented precedence (defaults, file,
  `OFFRAMP_*`, flags), validation against `schemas/v1/config.json`, positions on
  every configuration diagnostic, and the merged `effectiveConfig` in every envelope.
- Diagnostic catalog with codes OFR0001, OFR0010–0016, OFR0020, OFR0030,
  OFR0050–0056, OFR0099, and OFR1006; `docs/diagnostics.md` is now generated from
  it (`eng/gen-diagnostics.sh`), and tests fail when a code lacks documentation or
  a test that produces it.
- Workspace model records (`docs/spec/02-workspace-model.md`), `ICache`,
  `IGitService` (`git mv`, porcelain status), and a git-compatible unified diff
  renderer for dry runs.
- ADRs 0002–0006: CLI foundations, configuration loading, `init` writes, test
  stack, and the doctor contract.

### Changed
- `Directory.Build.props` no longer produces a separate symbols package: PDBs
  are embedded, so `dotnet pack` failed with NU5017 when asked for a `.snupkg`.

[Unreleased]: https://github.com/Andorbal/offramp/compare/v0.16.0...HEAD
[0.16.0]: https://github.com/Andorbal/offramp/compare/v0.15.0...v0.16.0
[0.15.0]: https://github.com/Andorbal/offramp/compare/68f5511b14b047def139a5de50f8f0dd7919174e...v0.15.0
[0.14.0]: https://github.com/Andorbal/offramp/compare/6f08ae0eb5cfccb69f7610cde61f4bf09943d081...68f5511b14b047def139a5de50f8f0dd7919174e
[0.13.0]: https://github.com/Andorbal/offramp/compare/35ac927acc082f7b1015acb6e90271765a6fff8f...6f08ae0eb5cfccb69f7610cde61f4bf09943d081
[0.12.0]: https://github.com/Andorbal/offramp/compare/071a64810b05bf0f372ed9320d3b5554639e4408...35ac927acc082f7b1015acb6e90271765a6fff8f
[0.11.0]: https://github.com/Andorbal/offramp/compare/f19c1688a9a4ed5f1f351507d159d746a7108066...071a64810b05bf0f372ed9320d3b5554639e4408
[0.10.0]: https://github.com/Andorbal/offramp/compare/fedc3afc4065315d1cf499797f82e4e6884f89b9...f19c1688a9a4ed5f1f351507d159d746a7108066
[0.9.0]: https://github.com/Andorbal/offramp/compare/a1b8af48de84a374951ff1cdddb2db8fd0b5b3f1...fedc3afc4065315d1cf499797f82e4e6884f89b9
[0.8.0]: https://github.com/Andorbal/offramp/compare/6572c58908fcc30611854cbc6e5c56c439461b5f...a1b8af48de84a374951ff1cdddb2db8fd0b5b3f1
[0.7.0]: https://github.com/Andorbal/offramp/compare/c24e44b749d7b397cb90e7b7d16d8b5ff69bf68d...6572c58908fcc30611854cbc6e5c56c439461b5f
[0.6.0]: https://github.com/Andorbal/offramp/compare/dd6b0c5815f28f23e3c959d31af9b7961b88a224...c24e44b749d7b397cb90e7b7d16d8b5ff69bf68d
[0.5.0]: https://github.com/Andorbal/offramp/compare/73cfe40c7e5b4e24c8e2443e21fa1bb5a53adb8e...dd6b0c5815f28f23e3c959d31af9b7961b88a224
[0.4.0]: https://github.com/Andorbal/offramp/compare/9d178f5cfb5dac323b7314e08f575ff0a065579a...73cfe40c7e5b4e24c8e2443e21fa1bb5a53adb8e
[0.3.0]: https://github.com/Andorbal/offramp/compare/40bf799790fd4b3b0d21e65f2400cde4c7b401f7...9d178f5cfb5dac323b7314e08f575ff0a065579a
[0.2.0]: https://github.com/Andorbal/offramp/compare/d6e341009a47e438ba4997a4356372f63cbe649f...40bf799790fd4b3b0d21e65f2400cde4c7b401f7
[0.1.0]: https://github.com/Andorbal/offramp/tree/d6e341009a47e438ba4997a4356372f63cbe649f
