# Field test: SmartStoreNET 4.2.0

Offramp was run against a second real, large .NET Framework codebase, this time an ASP.NET MVC 5
application with plugins, to find the gaps that the fixtures and the DotNetNuke test do not show. This
page records what happened, ranks what needs fixing, and gives the exact workarounds that were needed.
SmartStoreNET stands in for the MVC 5 shape it shares with nopCommerce 3.90 (which it was forked from),
Orchard 1.x and Umbraco 8; [a section below](#other-mvc-5-codebases) says which findings apply to them.

- **Codebase:** [smartstore/SmartStoreNET](https://github.com/smartstore/SmartStoreNET) at tag `4.2.0`
  (annotated), commit `626ce90f4d3d5f0828be8eb781eec3d39273cd75`, the last release (December 2021). An
  e-commerce application on .NET Framework 4.7.2: ASP.NET MVC 5, Web API 2 with OData, EF6 code first,
  Autofac. `src/SmartStoreNET.sln` has 25 projects and about 387,000 lines of C#, plus 679 Razor views. All
  25 projects are legacy (non-SDK) and target `net472` only; 24 use `packages.config` (96 packages). 14 are
  web application projects: the site (`SmartStore.Web`), the admin area (`SmartStore.Admin`, a separate
  project nested in the site's folder) and 12 plugins that build into the site's `Plugins/` folder. There are
  5 NUnit 2 test projects, 2 WinForms tools and 4 libraries. `src/SmartStoreNET.Minimal.sln` holds 10 of them.
- **Machine:** Ubuntu 24.04 in a container with 4 cores and 15 GB of memory, the .NET 10.0.112 SDK from the
  Ubuntu archive, and no Mono. Two other field tests ran on the same machine at the same time.
- **Offramp:** `main` at `5106c05` (v0.16.0 plus the DotNetNuke fixes), built locally and run as
  `dotnet src/Offramp.Cli/bin/Release/net10.0/offramp.dll`.
- **Method:** each command was run the way a new user would, following the README and
  `docs/compiling-on-macos.md`, with `--json`. Results were checked against the SmartStoreNET sources, and
  root causes were traced in Offramp's code. One root cause (P0 #2) was confirmed with a probe: a local
  change to Offramp, measured on the codebase and then reverted.

## Summary

The DotNetNuke fixes carry over. `doctor --fix --apply` supplies the reference assemblies and web targets
to all 25 legacy projects, and `scan` restores the 96 `packages.config` packages itself. What remained were
SmartStoreNET's own problems on Linux: 14 paths in the wrong letter case, two `cmd.exe` post-build events,
and one build task that needs .NET Framework's MSBuild. With those worked around (a short list,
[below](#appendix-what-it-took-to-build-smartstorenet-on-linux)), `scan` succeeds: 25 projects, 387,169
lines, no diagnostics, 49 seconds. The dependency commands now see every package and give the right
answers about versions and binding redirects.

However, several commands give **wrong or dangerous answers** on this codebase:

1. `csproj modernize` would strip `AssemblyVersion` and five other attributes from two shared files that 19
   projects compile. The 14 web projects it does not convert would then build as version 0.0.0.0. Its
   verification cannot notice.
2. `audit api` does not see ASP.NET MVC or Web API at all. It reports 0 findings for `System.Web.Mvc` in a
   codebase where 363 files use it. The .NET Framework DLLs from `packages/` go into the target
   compilation unchanged. With that fixed (a probe), OFR3001 goes from 1,439 findings to 11,082.
3. `audit dead-code` marks 182 live classes as dead at **high** confidence: all 110 EF6 mapping classes, all
   12 dependency registrars (including the one that sets up the whole container), the route providers,
   database hooks and startup tasks. SmartStoreNET finds them by reflection through a generic
   `FindClassesOfType<T>()`, which Offramp does not follow.
4. `deps audit` proposes to "upgrade" EntityFramework.SqlServerCompact 6.4.4 to 4.3.1, a 2012 package with
   no assemblies. It calls Windows-only native packages (LibSassHost, V8) `ok`.

The common thread this time is the MVC 5 plugin architecture that nopCommerce, SmartStoreNET and their
relatives share: types found by a type finder, plugins that build into the site and are loaded at run time,
shared assembly-info files, and route helpers that wrap `MapRoute`. Offramp counts every plugin as an
application (16 applications where there is one site and two tools), and `plan --for SmartStore.Web`
leaves out the 13 projects the site loads at run time.

## Status after the fixes

Every finding was general to MVC 5 plugin hosts or to legacy projects, and all are fixed on the branch
that carries this report. The numbers are from SmartStoreNET after the fixes, measured on copies of
the checkout; the corpus test `smartstore` (`tests/Offramp.Corpus.Tests/Codebases/SmartStoreNetTests.cs`)
pins them.

| Finding | Fix | On SmartStoreNET now |
|---|---|---|
| P0 #1 `csproj modernize` strips shared assembly info | `1977a3b` | `src/AssemblyVersionInfo.cs` and `src/AssemblySharedInfo.cs` stay as they are; each converted project sets `GenerateAssembly…Attribute=false` and `OFR4306` names the shared file |
| P0 #2 `audit api` cannot see MVC or Web API | `1be4374`, `4028394` | `packages.config` packages are resolved for the target and their HintPath DLLs left out: 11,057 `OFR3001` (was 1,439), System.Web.Mvc 6,989 and System.Web.Http 714; `OFR3011` for the 23 projects whose packages have no modern build. Findings attributed to SmartStoreNET's own assemblies: 5 (was 133) |
| P0 #3 dead code misses the type finder | `ba2bb23`, `958a363` | discovery followed through `typeof(T)` and `Type` parameters, `GetGenericTypeDefinition() == typeof(G<>)`, and EF's `AddFromAssembly`; controller actions at most `medium`, matched in any letter case. High-confidence classes 70 (was 254; the 110 EF mappings and 12 registrars among those gone); removable lines 7,495 (was 12,802); high-confidence actions 0 (was 8) |
| P0 #4 `deps audit` and asset-less versions | `dbcddb9`, `9e72ddb` | a version without assemblies never replaces one with them, and a lower version is never an upgrade: EntityFramework.SqlServerCompact is `blocked`, not "upgrade to 4.3.1"; 5 packages are Windows-only (the 4 `*.win-x64`/`win-x86` native ones and MsieJavaScriptEngine) |
| P1 #5 plugins and areas are applications | `7255764` | the model records each project's output folder; a web project whose assembly lands inside another's folder, without a `Global.asax`, is hosted by it (`OFR0204`). `report`: 3 applications (was 16); `plan --for SmartStore.Web`: 18 projects (was 5); `redirects sync` leaves the 13 hosted `web.config` files alone (`OFR1507`) and keeps the MiniProfiler redirect a plugin needs |
| P1 #6 `SolutionDir` dropped, NuGet.targets kept | `684696b`, `6043f4d`, `9a9c229` | the NuGet 2 import goes with `RestorePackages`; `SolutionDir` stays while something uses it. With that fixed, the corpus run showed the next causes, now fixed too: verification allows the .NET Standard facades the legacy build added and the framework assemblies and package assemblies `PackageReference` adds, each listed with its evidence; imports under dot folders (`src/.nuget/`), the restored `packages/` build files and the task assemblies the build loaded reach the scratch copy; a converted build event can still be switched off. `csproj modernize --all`: 10 of 11 conversions pass verification (was 1 of 11) |
| P1 #7 letter case one build at a time | `e3c6c9a` | one pass over the project files before the build: the first scan names all 14 paths and all 19 projects that import `nuget.targets` (it took four scans) |
| P1 #8 two scans, two models | `8a4ef3e` | compiler calls are recorded by project and target framework, not by their position in the log; the corpus sweep now compares a second full scan |
| P1 #9 orphaned co-moves | `bdc5b89` | the closure is recomputed after every exclusion, and dropped co-moves are listed (`OFR2113`); tested on a fixture chain |
| P1 #10 Microsoft.Bcl.Build | `35eedc3` | named as a build step (`OFR0124`), and the compile-only block sets `SkipEnsureBindingRedirects` outside Windows; FacebookAuth builds |
| P1 #11 `web inventory` routes, areas, filters | `7354f7a` | route helpers followed through up to 5 calls, areas from `DataTokens` and defaults, the application's libraries read, Autofac filter registrations listed: SmartStore.Web has 71 routes (was 8; the site's 68, 2 `MapHttpRoute`, 1 OData), 5 container filters, 8 of 8 bundles, and the Admin and plugin areas |
| P2 polish | see the commits | `web scaffold` ports `EmptyResult`, `ModelState`, `ViewBag`, `HttpUnauthorizedResult` and `FormCollection` (`f02e54d`); `config convert` reads section groups and machine.config's sections (`f5bbbae`, `OFR4407`); `OFR3103` looks at path parameters only (`9f1044a`, 13 → 7); evidence made relative before it is shortened (`248d794`, `fbd2695`); `doctor` says the post-build events are overridden and checks legacy projects before the first scan (`377dca9`); `init` chooses `src/SmartStoreNET.sln` (`82a861b`); a build heartbeat (`546318a`); the terminal view of a failed conversion (`9dadcbe`); the `Builder` name heuristic (`ec0d6d5`) |

Still open: the eleventh conversion, SmartStore.Web.MVC.Tests, fails verification with `--all` only (alone it passes): the converted libraries bring `ru-RU` satellite folders and the legacy site `ru-ru` ones, and MSBuild's Copy task, which remembers the folders it created without regard to letter case, never creates the second on Linux (MSB3021/MSB3027). It is another letter-case problem of the codebase, which `OFR0117` does not name yet. `redirects sync --prune` removes the site's `System.Net.Http` redirect, as it does any redirect of an assembly no package provides; a framework assembly's unifying redirect deserves a closer look. `web scaffold` ports none of the site's 270 actions yet: 151 render Razor views, and 66
derive from controllers in SmartStore.Web.Framework, which the new project does not reference.
Route conditions such as `if (add)` are not evaluated. `report`'s area names still place the nested
admin project under its parent folder. SmartStoreNET's own letter-case problems and cmd.exe post-build
events stay harness adjustments in the corpus test, applied as the diagnostics prescribe.

## What worked well

- **The legacy-project support from the DotNetNuke fixes.** `doctor --fix --apply` wrote the compile-only
  block, and the next scan had none of the 12 MSB4019 (web targets) or the MSB3644 (reference assemblies)
  errors of the first. `scan` restored 96 packages into `src/packages/` (`OFR0106`) and passed
  `RestorePackages=false`, so the solution's `.nuget/NuGet.targets` did not try to run `NuGet.exe`. No
  manual restore, no `TargetFrameworkRootPath`, no `VSToolsPath`, no `global.json` edit was needed.
- **Naming the codebase's own problems.** `OFR0117` named each letter-case mismatch it saw with the exact
  path (`'Multimap.cs' is 'MultiMap.cs' on disk`), `OFR0115` both post-build events, `OFR0116` the web
  projects. Failed scans wrote no ledger snapshot, so the report's trend has one clean point.
- **The model.** Kinds are right: 14 web, 5 test (NUnit found through `packages.config`), 4 library,
  2 WinForms. `scan --no-build` gives a model identical to `scan`'s apart from `createdAt`.
- **Dependencies.** `deps audit` audits all 96 packages (DotNetNuke before its fix: 23 of 77).
  `deps consolidate --all` sees the 96 and correctly finds no version skew (checked by reading all 24
  `packages.config` files). `deps resolve-dlls` leaves 404 `packages.config` DLLs alone and reports exactly
  the 9 vendored DLLs as blockers (`lib/Telerik/Telerik.Web.Mvc.dll` ×8, `SmartStore.Licensing.dll`).
  `deps gac` maps plausibly (System.Drawing is Windows-only, System.Web has no port).
- **`redirects sync --prune`** removed no redirect of a package in use. In the site's `Web.config` it
  prunes 8 that nothing in the site's closure provides (AjaxMin, the OData v3 assemblies,
  System.Transactions, an old MiniProfiler version; see P1 #5 for the last one).
- **`audit api`'s attribution** (DotNetNuke P0 #1) holds: 5 findings against `mscorlib` and 22 against
  `System`, all real (`CallContext`, `AspNetHostingPermission`, `ApplicationSettingsBase`).
- **`audit serialization` and `audit native`** match the source exactly: 13 `BinaryFormatter` sites, the
  `kernel32` P/Invokes in `SymbolicLink`, `SetDllDirectory` in the plugin manager.
- **`audit dead-code` on views and literal discovery.** Razor views count as string sources (301
  candidates lowered by a mention in a `.cshtml` file). `typeof(IPlugin).IsAssignableFrom(t)` and
  `typeof(IPaymentMethod).IsAssignableFrom(t)` are recognized, so plugin classes and payment providers are
  `low`. `IConsumer` event handlers are `low`. The plugin views that the build copies into the site were
  not read twice.
- **`web inventory`** found the 29 controllers of the site and the 57 of the admin area, the 57 Web API and
  OData controllers of the WebApi plugin with the actions they override from a generic base, the
  `Global.asax` handlers, the HTTP module and handler from `Web.config`, and the authentication settings.
- **`codemod run`** reports one `OFR4501` per project (DotNetNuke P2 fixed) and says exactly which packages
  a `packages.config` project would need (`OFR4505`: Microsoft.Data.SqlClient, TimeZoneConverter).
- **`plan`, `graph`, `report`** answer in about a second; the HTML pages load nothing from the network.

## Findings, ranked

P0 means the command gives a wrong or dangerous answer. P1 means a gap that blocks real-world use. P2
means polish. Each finding says whether it is general or specific to SmartStoreNET.

### P0: wrong or dangerous results

#### 1. `csproj modernize` strips shared assembly-info files

The dry run of `csproj modernize --all` (and of `--project SmartStore.Core` alone) edits two files outside
any project folder:

- `src/AssemblyVersionInfo.cs` loses `AssemblyVersion`, `AssemblyFileVersion` and
  `AssemblyInformationalVersion`;
- `src/AssemblySharedInfo.cs` loses `AssemblyProduct`, `AssemblyCompany` and `AssemblyConfiguration`.

The values move into the converted project's properties, which is right for that project. But the model
shows 19 projects compiling both files, and 14 of them are web projects that `modernize` refuses to convert
(`OFR4304`). Once applied, SmartStore.Web, SmartStore.Admin and all 12 plugins build without an assembly
version (0.0.0.0) and without file or product information. Verification builds only the converted
projects, so it cannot notice. Today the conversions that make these edits fail verification for another
reason (P1 #6), so a plain `--apply` stops; `--apply --accept-diff` writes them now, and a plain `--apply`
will once P1 #6 is fixed.

- **Root cause:** `ModernizePlanner.ConvertAsync` runs the AssemblyInfo codemod (`OFRM013`) over every
  syntax tree of the project, linked files included, and keeps every edit it makes.
- **Fix:** do not edit a file that a project outside the conversion compiles. For the attributes that file
  declares, set `GenerateAssembly<Name>Attribute=false` in the converted project, and report that the file
  is shared. The same guard belongs in `codemod run --mod assemblyinfo`.
- **Scope:** general. A linked `SolutionInfo.cs`, `CommonAssemblyInfo.cs` or `GlobalAssemblyInfo.cs` is a
  common way to version a solution; DotNetNuke has one too.

#### 2. `audit api` does not see ASP.NET MVC or Web API in `packages.config` projects

`audit api` reports 1,439 `OFR3001` findings, none of them for `System.Web.Mvc` (363 files use it),
`System.Web.Http` (70 files), OData, `System.Web.Optimization` or the Autofac MVC integration. Every
controller, action result, filter and HTML helper passes as available on .NET 10.

- **Root cause:** `TargetCompilationBuilder` compiles each project against `net10.0` with the packages from
  `DirectPackages`. For a legacy project that list comes from the assets file, which the compile-only
  block's restore writes without any `packages.config` package, so none of them is resolved for the
  target (the model's `resolved.net472.packages` is empty for every project). Then every `HintPath` DLL is added as a loose file, and for these projects that
  is `packages/Microsoft.AspNet.Mvc.5.2.7/lib/net45/System.Web.Mvc.dll`: the .NET Framework assembly
  itself. This is the `audit api` part of DotNetNuke's P1 #7, which was not fixed.
- **Evidence:** a probe that passes the `packages.config` packages to the target resolution and leaves out
  their `HintPath` DLLs gives 11,082 findings: System.Web.Mvc 6,913, nunit.framework 1,238 (NUnit 2.6.3),
  System.Web 873, System.Web.Http 672, Rhino.Mocks 390, System.Web.OData 35, Autofac.Integration.Mvc 30. It
  also reports `OFR3011` for the 23 projects whose packages do not support the target (Microsoft.AspNet.Mvc,
  Autofac.Mvc5, Microsoft.AspNet.WebPages, and so on). The run took 242 seconds instead of 142.
- **Knock-on:** `seams` takes its unportable types from the audit. On SmartStore.Services, `IBlockHandler`
  (its signature exposes `HtmlHelper`) and `AuthorizeState` (uses `ActionResult`) are not tainted.
- **Fix:** resolve `packages.config` packages for the target like `PackageReference` ones, and skip the
  `HintPath` DLLs that `deps resolve-dlls` already recognizes as `packages.config` packages.
- **Scope:** general. Every ASP.NET MVC or Web API application on `packages.config` has this, which is
  nearly all of them.

#### 3. `audit dead-code` marks live, reflection-discovered classes as dead

Of 254 classes at high confidence, 182 (4,699 of the 12,802 "removable" lines) are live:

| Classes | Count | How SmartStoreNET finds them |
|---|---|---|
| EF6 mappings (`SmartStore.Data.Mapping.*Map : EntityTypeConfiguration<T>`) | 110 | `OnModelCreating` scans the assembly for `t.BaseType.GetGenericTypeDefinition() == typeof(EntityTypeConfiguration<>)` |
| Dependency registrars, including `SmartStore.Web.Framework.DependencyRegistrar` | 12 | `typeFinder.FindClassesOfType<IDependencyRegistrar>()` |
| Route providers (`*RouteProvider`, `GenericPathRoutes`, `LastRoute`) | 15 | `FindClassesOfType<IRouteProvider>()` |
| Database hooks (`AclEntityHook`, `MenuInvalidator`, ...) | 15 | `FindClassesOfType<IDbSaveHook>()` |
| Admin mappers (`IMapper<TFrom, TTo>`) | 16 | `typeFinder.FindClassesOfType(typeof(IMapper<,>))` |
| Startup tasks (`WebApiStartupTask`, `PluginStarter`, ...) | 6 | `FindClassesOfType<IApplicationStart>()` and `<IPostApplicationStart>` |
| Menus, menu resolvers, permission providers, bundles, Web API configuration | 8 | `FindClassesOfType<IMenu>()` and similar |

Deleting the mappings breaks the database model; deleting `SmartStore.Web.Framework.DependencyRegistrar`
leaves the application without services.

Seven of the eight controller actions at high confidence are also live: six
`OfflinePaymentController.*PaymentInfo` actions, whose names are built as `"{0}PaymentInfo"` at run time,
and `BoardsController.ActiveDiscussionsRss`, linked as `Url.Action("ActiveDiscussionsRSS")`. MVC matches
action names without regard to case; Offramp's string match is case-sensitive.

- **Root cause:** `DeadCodeAnalyzer.Index.DiscoveredBy` recognizes `typeof(X).IsAssignableFrom(t)` and
  `IsSubclassOf(typeof(X))` only with a literal type. SmartStoreNET's type finder calls
  `assignTypeFrom.IsAssignableFrom(t)` on a parameter, reached through the generic
  `FindClassesOfType<T>() => FindClassesOfType(typeof(T))`. The `GetGenericTypeDefinition()` comparison is
  not recognized at all. SmartStoreNET does not use Autofac's `RegisterAssemblyTypes`, so the "registers by
  convention" rule does not lower them either.
- **Fix:** treat a method as a discovery method when it passes its type parameter or a `Type` parameter to
  those checks (follow a few levels), and count the type argument at every call of it as discovered.
  Recognize `GetGenericTypeDefinition() == typeof(G<>)`, EF6's `Configurations.AddFromAssembly` and EF
  Core's `ApplyConfigurationsFromAssembly`. Never rate a public controller method high, and match action
  names in strings without regard to case.
- **Scope:** general. nopCommerce has the same type finder and the same mapping loop by construction; the
  type-finder pattern is common in plugin hosts (Umbraco's `TypeLoader.GetTypes<T>()` is another).

#### 4. `deps audit` counts package versions without assemblies as supporting the target

- **A downgrade called an upgrade.** EntityFramework.SqlServerCompact is in use at 6.4.4. The audit says
  "6.4.4 does not support net10.0; 4.3.1 does" (`OFR1002`), with status `upgrade`. Version 4.3.1 is from 2012
  and contains only `Content/*.transform` files and a PowerShell install script. NUnitTestAdapter 2.2.0, a
  `build/`-only NUnit 2 adapter, is `ok` for the same reason.
- **Windows-only native packages are `ok`.** LibSassHost.Native.win-x64 and win-x86, and
  JavaScriptEngineSwitcher.V8.Native.win-x64 and win-x86, have only `runtimes/win-*/native/*.dll`. The audit
  reports them `ok` with `windowsOnly: false`; nothing else in Offramp (`audit native` included) says the site's
  Sass and JavaScript engines will not load on Linux.
- **Root cause:** `TargetSupport.Supports` returns true when a version has no assets and no dependency
  groups, and `DepsAuditor` takes the first such version walking down from the newest, even below the one in
  use. `TargetSupport.WindowsOnly` looks only at `lib/` and `ref/`.
- **Fix:** a version without assemblies does not replace one with assemblies; never propose a version lower
  than the one in use as an upgrade; treat a package whose only runtime assets are `runtimes/win*/native` as
  Windows-only, and name the `linux-x64` package when the feed has one.
- **Scope:** general. Old packages that were once content-only or tools-only are common, and native
  `*.win-x64` packages come with every JavaScript engine, Sass compiler and image library of that era.

### P1: gaps that block real-world use

#### 5. Plugins and areas are counted as applications

SmartStoreNET is one web application. Only `SmartStore.Web` has a `Global.asax`. `SmartStore.Admin` builds
into `..\bin\` (the site's `bin`) and is an MVC area. The 12 plugins build into
`..\..\Presentation\SmartStore.Web\Plugins\<Name>\` (`OutputPath` in Debug and Release), carry a
`Description.txt`, reference what the site already has with `Private=False`, and are loaded by the site at
run time; nothing references them. Offramp has no notion of this:

- `report` counts 16 applications (all 14 web projects and the two tools), all `blocked`. This is the
  DotNetNuke "Still open" item, and here it is the headline number.
- `plan --for SmartStore.Web` lists 5 projects (Core, Data, Services, Web.Framework, Web). It leaves out the
  admin area and the 12 plugins: 13 projects and 104,877 lines the site needs to run.
- `redirects sync` manages 13 plugin and area `web.config` files, whose binding redirects the runtime never
  reads. For the site it computes redirects from the site's closure only: the stale MiniProfiler redirect
  (to 3.2.0.157) is pruned, although the DevTools plugin, loaded into the site, uses MiniProfiler 4.0.0.0.
  With the plugins in the closure it would be updated instead. (Pruning is harmless here, since the old
  redirect does not cover 4.0.0.0; the rule is still wrong for plugin hosts.)
- `web scaffold --project SmartStore.Tax` proposes a strangler-fig application for a plugin, proxying to
  `http://localhost:56865/`, the plugin project's Visual Studio URL, which serves nothing.
- The model does not record `OutputPath`, so none of this can be derived from it today.

**Fix:** record each project's output folder in the model. A web project whose output lies inside another
web project's folder is hosted by it (supporting evidence: no `Global.asax` or OWIN `Startup`, references to
the host). `report` and `plan --for` then count only hosts as applications and put hosted projects in the
host's closure; `redirects sync` manages the host's configuration with the packages of its hosted projects;
`web scaffold` and `web inventory` say when a project is hosted. This needs an ADR.

**Scope:** general for plugin hosts: nopCommerce's Nop.Admin (and its plugins, where they are web
projects), Orchard modules, DotNetNuke modules.

#### 6. `csproj modernize` drops `SolutionDir` but keeps what uses it

10 of the 11 conversions fail verification (`OFR4303`) with
`MSB4019: The imported project "/.nuget/nuget.targets" was not found`. The converter drops
`<SolutionDir Condition="...">..\..\</SolutionDir>` and `RestorePackages` (both on its list of legacy
properties), but keeps `<Import Project="$(SolutionDir)\.nuget\nuget.targets" />`. Verification builds the
project on its own, where `$(SolutionDir)` is empty. The converted post-build target of SmartStore.Data.Tests
also keeps `$(SolutionDir)packages\...`. The one conversion that passes, SmartStore.WebApi.Client, imports
the file only under an `Exists` condition.

- **Fix:** remove the NuGet 2 restore import (`.nuget\NuGet.targets`) with `RestorePackages`, since
  `PackageReference` restore replaces it; keep `SolutionDir`'s definition while anything still uses it.
- **Scope:** general. "Enable NuGet Package Restore" put this import into Visual Studio 2012–2013 era
  projects, often without a condition.

#### 7. Letter-case problems surface one build at a time, and not deterministically

It took four scans to find the 14 paths in the wrong letter case:

- 19 projects import `$(SolutionDir)\.nuget\nuget.targets`; the file is `NuGet.targets`. The first scan named 7
  of them (`OFR0117`), the second 13. SmartStore.Core fails on this import when built alone, yet got past it
  in both solution builds. The most likely reason is that MSBuild's cache of loaded project files ignores
  case, so the import succeeds on a build node that has already loaded `NuGet.targets`. Which projects fail
  therefore depends on scheduling.
- The 11 `Migrations\*.designer.cs` items (`.Designer.cs` on disk) in SmartStore.Data appeared only in the
  third scan, once the projects before them built.

A static check of the project files' `Import`, `Compile`, `EmbeddedResource`, `None` and `Content` paths finds
all 14 at once (a 60-line script did), plus three `Content` items in SmartStore.Web that affect only
publishing. **Fix:** do that check before or after the build, as the DotNetNuke report suggested; today
`OFR0117` comes only from build errors (`WindowsOnlyBuildSteps.FromBuildErrors`). **Scope:** general on
Linux.

#### 8. Two full scans give different models

Two full `scan` runs of the same tree differ in `compilerCalls.<tfm>.index` for 10 of the 25 projects. The
index is the position of the compiler call in `.offramp/build.complog`, which follows the order in which the
parallel build finished its compilations (`ScanRunner`, where it records `call.Index`). `scan --no-build`
reuses the binary log, so it matches `scan`, and that is all the corpus harness compares. This breaks the
first non-negotiable in `CLAUDE.md`, and it makes `.offramp/workspace.json` change on every scan.

- **Fix:** store the project and target framework and resolve the index when the compiler log is read, or
  write the compiler calls in a sorted order. The corpus harness should compare two full scans.
- **Scope:** general for any solution the build parallelizes.

#### 9. `move plan` keeps co-moves whose reason was removed

SmartStore has no portable project to move into, so `move extract` (dry run) was used to plan a move of
`SmartStore.Core/Collections/*.cs` (10 files) into a new `netstandard2.0` project. One requested file
moves and nine are excluded, as expected, but 40 other files move with it: domain enums, `Logging/*`,
`Html/CodeFormatter/*`, and `ComponentModel/SerializationUtils.cs`, which uses `BinaryFormatter` (it
compiles for `netstandard2.0` and throws at run time on .NET 10). For 35 of the 41 moves, the file named in
`coMoveOf` neither moves nor is excluded.

- **Root cause:** `MovePlanner.Exclude` removes the co-moves of an excluded file only one level deep
  (`candidates.Remove(dependent)`), without reporting them, so their own co-moves stay in the plan.
- **Fix:** remove co-moves transitively, or recompute the closure from what remains, and list the dropped
  co-moves in `excluded`.
- **Scope:** general; any deep dependency chain in a large project.

#### 10. `Microsoft.Bcl.Build`'s build task is not detected

SmartStore.FacebookAuth stayed partial with `MSB4062: The "EnsureBindingRedirects" task could not be loaded
... Microsoft.Build.Utilities.v4.0`. The task comes from Microsoft.Bcl.Build 1.0.21 and needs .NET
Framework's MSBuild. Offramp reports only the generic `OFR0130`. The package's own switch,
`SkipEnsureBindingRedirects=true`, fixes it.

- **Fix:** a Windows-only build step for Microsoft.Bcl.Build (from the import or from MSB4062 naming
  `Microsoft.Bcl.Build.Tasks.dll`), and the property in the compile-only block. On Windows the task writes
  redirects into the project's configuration file, so the switch belongs only to compile-only builds.
- **Scope:** general. Microsoft.Bcl.Build comes with Microsoft.Net.Http and Microsoft.Bcl.Async, common in
  2013–2016 codebases.

#### 11. `web inventory` misses most routes, all areas, and container-registered filters

- **Routes:** 8 of the 68 routes the site declares in code. 57 go through SmartStoreNET's own helpers,
  which wrap `MapRoute` or `new Route(...)`: `routes.MapLocalizedRoute(...)` (49), `CreateLocalizedRoute`
  (6, the SEO slug routes), `MapGenericPathRoute` and `routes.Add(...)`. Four media routes go through a
  local function that calls `MapRoute` with computed arguments; the inventory lists that call once, with an
  empty name and template.
- **Areas:** none, in any project. The admin area is declared by its route (`area = Admin`), and each plugin
  by `.DataTokens["area"] = "SmartStore.Tax"`, not by `AreaRegistration` classes.
- **Web API and OData routes** (`MapHttpRoute` ×2, `MapODataServiceRoute`) are registered in
  SmartStore.Web.Framework, a library, so no inventory shows them.
- **Filters:** 15 filters registered with Autofac (`AsActionFilterFor<T>`, `AsResultFilterFor<T>`) are
  missing, including the ones PayPal and Amazon Pay attach to the site's `CheckoutController`. Only the two
  `GlobalFilters.Filters.Add` calls in `Global.asax` are listed.
- **Bundles:** 7 of 8 (`bundles.Add(scriptBundle)`, with a variable, is missed).

For the strangler fig this matters: `web scaffold` has no route list to work from. **Fix:** follow an
extension on `RouteCollection` that calls `MapRoute` (as in P0 #3, one level of indirection), read
`DataTokens["area"]` and `area` defaults, include route and filter registrations from the application's
closure and hosted projects, recognize Autofac's filter registrations, and show computed templates as
computed. **Scope:** general; nopCommerce has the same `MapLocalizedRoute` extension by construction.

### P2: polish

- **`OFR0115` evidence is cut before it is made relative.** `WindowsOnlyBuildSteps.FirstLine` truncates the
  command to 120 characters, and only then does `scan` strip the repository root. With a long root, the
  evidence for SmartStore.Data.Tests is an absolute path cut off mid-way, and for DevTools it is
  `del "src/Pres…`. Strip first, then truncate. (DotNetNuke's P2 fix for absolute paths does not cover this.)
- **`doctor` says "No project needs Windows to build"** once `verify.properties` sets `PostBuildEvent` to empty:
  the override empties the property in the evaluations the check reads. The events are still in the
  projects.
- **`config convert`** says "bundleTransformer is left out: it is not declared in configSections" (`OFR4401`).
  It is declared, as a `<sectionGroup>`: `ConfigConverter.SectionTypes` keys the sections by their own names,
  and the lookup uses the group's. `system.net`, `system.data` and `system.codedom` get the same message,
  although they are declared by `machine.config`.
- **`audit behavior` `OFR3103`:** 6 of 13 "Windows path" findings are `TextWriter.Write(@"\t")` and similar
  JavaScript escapes in `StringExtensions.cs`. `WindowsPathMatcher` accepts every `System.IO` method; it should
  look only at path parameters.
- **`audit api` extension methods:** 133 findings are attributed to SmartStore.Core and
  SmartStore.Web.Framework with "No known mapping for the assembly", such as
  "System.Web.HttpRequestBase.IsHttps() (SmartStore.Core) does not exist on the target". They are extension
  methods on a missing System.Web type; attribute them to System.Web.
- **`web scaffold` reasons** say "no one-to-one ASP.NET Core counterpart" for `EmptyResult` (18 actions),
  `ModelState.AddModelError` (9), `ViewBag` (8), `TempData`, `ViewData`, `RedirectResult`,
  `HttpUnauthorizedResult` (50) and `FormCollection` (14). All have direct counterparts.
- **`report` areas:** the area "src/Presentation/SmartStore.Web" (1 project, 45,760 lines) is SmartStore.Admin,
  nested in the site's folder; the site itself is counted under "src/Presentation".
- **The build phase has no heartbeat** (DotNetNuke "Still open"): the first scan was silent for 74 seconds.
- **`init --defaults`** leaves `solution: null` when `src/` holds `SmartStoreNET.sln` (25 projects) and
  `SmartStoreNET.Minimal.sln` (10 of them). The superset is the obvious default.
- **`doctor` before the first scan** passes the reference-assemblies check ("the first net4x build downloads
  it") and has no legacy-project check, because both need the model. The README's order (`doctor`, `init`,
  `scan`) therefore leads to a failed first scan (25 of 25 projects partial); only the second `doctor`
  shows `OFR0018` and the fix. Reading the solution's project files would be enough to tell.
- **`csproj modernize`'s terminal view** (`Render` in `CsprojCommands.cs`, read, not run) says a project
  "compiles different inputs" and has a "different compile set" when its converted build failed (P1 #6);
  the JSON carries the build error.
- **`move tests`** calls `LinqContainsPredicateBuilder` test support by its name (DotNetNuke "Still open",
  the `Builder` heuristic); it is only a medium candidate, so nothing moves.

## Other MVC 5 codebases

Which findings would apply to the codebases SmartStoreNET stands in for. "By construction" means the same
code or build pattern is known to be there (nopCommerce 3.90 is SmartStoreNET's ancestor); "likely" means
the pattern is typical but was not checked in that codebase.

| Finding | nopCommerce 3.90 | Orchard 1.x | Umbraco 8 |
|---|---|---|---|
| P0 #1 shared assembly info | if it links a shared file | if it links a shared file | if it links a shared file |
| P0 #2 MVC invisible to `audit api` | yes, `packages.config` MVC 5 | yes, `packages.config` MVC 5 | yes wherever MVC comes from `packages.config` |
| P0 #3 reflection discovery | by construction: `FindClassesOfType<T>()`, EF6 mapping loop | partly: `typeof(IDependency).IsAssignableFrom` is recognized; NHibernate, not EF | likely: `TypeLoader.GetTypes<T>()`, composers |
| P0 #4 package versions without assemblies | general | general | general |
| P1 #5 plugins as applications | by construction for Nop.Admin (nested, builds into Nop.Web); plugins if they are web projects | likely: modules are web projects inside Orchard.Web | little: one web project |
| P1 #6 `SolutionDir` and NuGet.targets | likely (same era) | likely | no if it uses `PackageReference` |
| P1 #7 letter case on Linux | unknown until built | unknown | unknown |
| P1 #8 determinism | general | general | general |
| P1 #11 routes, areas, filters | by construction: `MapLocalizedRoute`, route-provider pattern | likely: routes come from `RouteDescriptor` providers | partly |

## Command log

Times are wall-clock on the 4-core container, shared with two other field tests. Exit 1 is expected
for commands that report errors (`--fail-on error`).

| Command | Time | Exit | Result |
|---|---|---|---|
| `doctor` | 1 s | 0 | fine; no legacy check before the first scan |
| `init --defaults` | <1 s | 0 | `OFR0020`: two solutions; rerun with `--solution src/SmartStoreNET.sln` |
| `scan` (as shipped) | 76 s | 1 | 25/25 partial; 20 errors (MSB4019 ×19, MSB3644); OFR0106 (96 packages), OFR0116 ×12, OFR0117 ×7, OFR0115 |
| `doctor --fix --apply --yes`, then `scan` | 1 s + 18 s | 1 | 17 partial; 6,506 errors (cascade of 13 MSB4019 and 2 CS2001); OFR0117 ×15 |
| `scan` after 3 case links | 16 s | 1 | 21 partial; 11 × CS2001 (`designer.cs`) |
| `scan` after 11 more links | 51 s | 1 | 1 partial; MSB3073 ×2 (post-build), MSB4062 (P1 #10) |
| `scan` (with workarounds) | 49 s | 0 | 25 projects, 387,169 LOC, 0 diagnostics |
| `scan --no-build` | 7 s | 0 | model identical to `scan`'s apart from `createdAt` |
| `scan` again | 50 s | 0 | model differs in 10 compiler-call indexes (P1 #8) |
| `graph --format json/html`, `plan`, `plan --for SmartStore.Web`, `report` | 1 s each | 0 | 6 waves; 2 ready; 16 applications (P1 #5) |
| `deps audit` | 183 s | 1 | 96 packages: 49 ok, 8 upgrade, 15 replace, 24 blocked; P0 #4 |
| `deps resolve-dlls` | 5 s | 1 | 404 `packages.config` DLLs, 9 blockers (vendored) |
| `deps consolidate --all` | 5 s | 0 | 96 packages, no skew, no changes (correct) |
| `deps gac` | 52 s | 0 | plausible |
| `redirects sync` / `--prune` | 2 s each | 0 | 34 changed, 54 stale (pruned), 280 unchanged; P1 #5 |
| `audit api` | 142 s | 1 | 1,615 findings, OFR3001 1,439; P0 #2 (probe: 11,082 OFR3001, 242 s) |
| `audit behavior` | 100 s | 0 | 841 findings in 19 rules; HttpContext.Current ×45; P2 (OFR3103) |
| `audit serialization` | 62 s | 1 | BinaryFormatter ×13, correct |
| `audit native` | 54 s | 0 | 5 P/Invokes, correct; native packages not covered (P0 #4) |
| `audit dead-code` | 94 s | 0 | 4,325 candidates, 743 high, 12,802 lines; P0 #3 |
| `csproj modernize --all` (dry run) | 106 s | 1 | OFR4303 ×10, OFR4304 ×14; P0 #1, P1 #6 |
| `csproj modernize --project SmartStore.Core` | 23 s | 1 | same shared-file edits (P0 #1) |
| `web inventory` (Web, Admin, Tax, WebApi) | 28 / 26 / 4 / 10 s | 0 | controllers right; routes, areas, filters P1 #11 |
| `web scaffold` (Web, Tax; dry run) | 36 / 5 s | 0 | 0 endpoints ported of 270 actions; P1 #5, P2 |
| `config convert --project SmartStore.Web` | 3 s | 0 | 36 app settings; P2 (sectionGroup) |
| `codemod list`, `codemod run --mod http-context` | 1 s, 53 s | 0 | 47 sites skipped, one OFR4501 per project |
| `codemod run --mod all` (dry run) | 175 s | 0 | 15 sites in 12 files; OFR4505 ×6 |
| `seams --project SmartStore.Services` | 65 s | 0 | 740 types, 99 tainted, 40 seams; see P0 #2 |
| `move tests --project SmartStore.Core` | 60 s | 0 | no moves; 3 medium candidates |
| `move extract` (Collections to `netstandard2.0`, dry run) | 76 s | 0 | 41 moves for 1 requested file; P1 #9 |
| `ifdef report` | 6 s | 0 | one `#if DEBUG` region |
| `verify --projects SmartStore.Core` | 4 s | 0 | passed |
| `guide` | 2 s | 0 | fine |

The standard corpus sweep (`doctor --fix` through `csproj modernize`) takes about 12 minutes on the
adjusted checkout. Not exercised: `audit api-compat`, `service`, `remote`, `extract interface`,
`forwarders`, `slice`, `mcp serve`, `ide check` and `ide serve`, the VS Code extension, and every `--apply`.

## Appendix: what it took to build SmartStoreNET on Linux

In the order they were hit. Everything the DotNetNuke appendix needed beyond these is now done by
`doctor --fix --apply` and `scan`.

1. **Solution.** `offramp init --defaults --solution src/SmartStoreNET.sln` (two solutions in `src/`).
2. **Compile-only block.** `offramp doctor --fix --apply --yes` (writes `Directory.Build.props` at the
   repository root, which MSBuild finds from `src/`).
3. **Letter case** (Linux only), as symbolic links; renaming the files or fixing the project files in git is
   the real fix:
   - `src/.nuget/nuget.targets` → `NuGet.targets` (imported in that spelling by 19 projects);
   - `src/Libraries/SmartStore.Core/Collections/Multimap.cs` → `MultiMap.cs`;
   - `src/Presentation/SmartStore.Web.Framework/Theming/Assets/AutoprefixPostProcessor.cs` →
     `AutoPrefixPostProcessor.cs`;
   - 11 files `src/Libraries/SmartStore.Data/Migrations/<id>.designer.cs` → `<id>.Designer.cs`, for the
     migrations `202008181949580_RenamedCustomerRoleOrderTotal` through `202112171231491_V420Resources`.
4. **`offramp.yml`:**

   ```yaml
   verify:
     properties:
       PostBuildEvent: ""                  # DevTools: del ... /q /s; Data.Tests: if not exist ... md, xcopy (OFR0115)
       SkipEnsureBindingRedirects: "true"  # Microsoft.Bcl.Build 1.0.21 in FacebookAuth (MSB4062, P1 #10)
   ```

The `audit api` root cause (P0 #2) was confirmed by changing `TargetCompilationBuilder` locally to pass the
`packages.config` packages to the target resolution and to skip their `HintPath` DLLs, running `audit api`
again, and reverting the change.
