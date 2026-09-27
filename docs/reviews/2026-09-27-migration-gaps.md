# What a real migration still needs: a review of the process as of v0.15.0

A walk through a large .NET Framework migration, start to finish, against what
Offramp does today (specs in `docs/spec/`, behavior checked in the code where it
matters). The goal is not a one-click migration. It is to find the places where
the tool gives a misleading answer, the places where a person has to stitch
commands together by hand, and the parts of a real codebase nothing looks at yet.

Items are grouped by where they hurt and ordered within each group by how much
time they would save on a real repository. Each has: what happens today, why it
matters, a proposal, and a size (S: a day; M: a few days; L: a milestone).

## A. Answers that are wrong or misleading today

These come first because a tool that says `ready` when a project is not loses
trust on day one, and each is small.

### A1. Project references to projects outside the model vanish from the graph (S)

`GraphBuilder.Build` keeps only references whose target is in the model
(`src/Offramp.Workspace/Model/GraphBuilder.cs:24`). A reference to a C++/CLI
`.vcxproj`, a `.sqlproj`, a `.vbproj` that failed to evaluate, or a project
outside the current slice is dropped silently. `plan --frontier` then calls the
dependent `ready`, `report` counts it toward `next`, and the truth only surfaces
in a trial build. Large old codebases almost always have a C++/CLI interop
project in the middle of the graph.

Proposal: keep the unresolved references on the project
(`unresolvedReferences: [{ path, reason }]`), count them as blockers in `plan`
and `report` with a distinct reason (`external`), and emit a new loading
diagnostic (OFR0105, warning) once per project. `slice --for` should add them to
its output as a note so the user knows the slice is open.

### A2. MSTest v1 projects are not test projects to `move tests` (S)

`TestCodeClassifier` recognizes test frameworks by the attribute's assembly
(`src/Offramp.Analysis/TestCode/TestCodeClassifier.cs:64`), and MSTest is
`Microsoft.VisualStudio.TestPlatform.TestFramework`. Legacy MSTest projects
reference the GAC assembly `Microsoft.VisualStudio.QualityTools.UnitTestFramework`
instead, with the same namespace. The guide's applies-when fact already lists
that assembly (`GuideCatalog.cs:59`), so the step appears and then finds no
tests. `deps gac` reports the same reference as `unknown` because
`rules/framework-assemblies.yml` has no entry.

Proposal: add the assembly to the classifier as `mstest`, and to
`framework-assemblies.yml` as `package: MSTest.TestFramework` with a note about
`MSTest.TestAdapter` and the `[TestClass]` differences (no `TestContext`
property injection change, but `Assert.AreEqual` overloads moved).

### A3. No target framework floor check (S)

`netstandard2.0` libraries referenced from `net45`–`net47` projects bring the
System.Runtime facade and binding-redirect problems Microsoft documents; the
guidance is to be on `net472` or later before consuming .NET Standard. Offramp
never looks: the guide's `port` step proposes `net45;net10.0` for a `net45`
project, and `move plan` into a `netstandard2.0` destination succeeds in trial
compilation while the `net45` consumers break at runtime.

Proposal: a `doctor` check and a `plan` column for projects below `net472`
(OFR01xx, warning), a `prepare` guide step "raise .NET Framework to 4.8" running
`csproj modernize --project P --tfm net48` per project (the machinery exists),
and `move plan` warning when a `standard` destination will be consumed by a
project below the floor.

### A4. Every writer makes the model stale, and a rescan is a full rebuild (M)

After `move apply`, `move tests`, `deps consolidate`, `codemod run`,
`csproj modernize`, or `deps resolve-dlls`, the next command reports OFR0002 and
the fix is `scan`, which is a `--no-incremental` build of the solution. On the
5,000-project repository the performance envelope targets, that is hours per
iteration, which turns the overnight `--all` workflow into a two-night one. The
editor integration already solved this for itself: `LiveWorkspace` lays its own
moves and project edits over the recorded model so a second move needs no scan.

Proposal, in order of payoff:
1. Writers patch the model from their own change set: the journal records every
   rename, project edit, and created file, which is exactly the compile items,
   project references, package references, and input hashes the model stores.
   Refresh `inputs` for the files the writer touched so OFR0002 stays quiet for
   Offramp's own edits and still fires for anyone else's.
2. `verify` already produces a binlog for the projects it built; let it refresh
   those projects' compiler calls in the compiler log (`scan --merge-binlog`
   internally), so a verified move leaves the model current.
3. `scan --projects P1,P2` for a partial rescan through a generated slice.

## B. The port inner loop, where the days actually go

### B1. There is no per-project "what is left and which command fixes it" (L, mostly composition)

The guide's `port` step runs `csproj modernize --tfm "net48;net10.0"` and says
the dry run's errors are the to-do list. That is true, and it is also where a
person spends most of the migration: reading CS0234 errors, guessing which are
covered by a codemod, which by a package bump, which by `deps gac`'s mapping,
which need `ifdef wrap`, which are a seam. Each of those is a separate command
with its own input format, and nothing joins them.

Proposal: `offramp port --project P [--apply] [--tier packages|codemods|ifdef]`:
1. Trial-compile `P` for the target with the `audit api` machinery (it already
   builds the target reference set and maps errors to symbols).
2. Classify every error into a tier with the fix that clears it:
   - **package**: the symbol comes from a package whose `newestSupporting`
     version has it (from `deps audit`); fix is a version bump.
   - **reference**: a framework assembly with a `deps gac` mapping; fix is the
     package reference, conditioned on the target framework.
   - **codemod**: an audit rule with a codemod (`sqlclient`, `webclient`, ...).
   - **wrap**: the rest of the OFR3001 findings, through `ifdef wrap`.
   - **seam**: taint that `wrap` cannot isolate (OFR3601), pointing at `seams`.
   - **manual**: everything else, with the first errors.
3. `--apply` applies the mechanical tiers in that order through one journal,
   re-compiles, and prints the residue. `--json` for agents.
4. End with `csproj modernize --tfm` when the residue is empty.

The guide's `port` step would run this instead of the bare modernize.

### B2. `deps gac` is read-only (S)

It knows the mapping and the usage count per reference, so it knows exactly
which `Reference` items to delete (`builtin`, and any with zero usages) and
which package to add for the target. Proposal: `deps gac --apply` writing
through a journal: remove `builtin` and unused references, add `package`
mappings in an item group conditioned on `'$(TargetFrameworkIdentifier)' !=
'.NETFramework'` (the pattern the codemods already use), and report `none` and
`compat-pack` as before. This is the `reference` tier of B1.

### B3. Nothing runs the tests (M)

`verify.mode` is `build`, `command`, or `none`. The acceptance signal for a
ported project is "its tests pass on the new target", and every `audit behavior`
finding is a hypothesis until a test exercises it. `command` mode can run a
script, but then the result is a pass/fail with no test names, and the
comparison across targets is the user's problem.

Proposal: `offramp test --project P [--framework net10.0] [--baseline]`, also
reachable as `verify --mode test`:
- `dotnet test -f <tfm> --logger trx` for the test projects that reference `P`
  (or `P` itself when it is a test project), parsed into pass/fail/skip per
  test.
- `--baseline` records the result for a framework (typically `net48`, captured
  on Windows and committed like the verify baseline) and later runs compare the
  modern framework against it: tests that pass on `net48` and fail on `net10.0`
  are OFR5030, one per test, with the failure message. That list is the real
  behavior-change report.
- The guide's `port` stage gets a step "tests pass on the target" per ported
  project, and `report` gets the number.

Also worth checking: when a library is ported, its test project becomes ready
one wave later (its subject was framework-only until then). That is correct, but
the guide should say "now port and run `Foo.Tests`" rather than leave it to the
next `plan --frontier`.

### B4. The guide ends at `port`; the migration does not (M)

There is no stage for what happens after every project is dual-targeted:
dropping `net48`, and dismantling the bridges. Today that is a dozen manual
edits per project, and nobody remembers all of them:

- `ifdef strip --symbol NETFRAMEWORK --keep false` (exists).
- Remove the `'$(TargetFrameworkIdentifier)' == '.NETFramework'` item groups the
  codemods and `deps gac --apply` added, and the packages they carried
  (`System.Text.Json` on the framework side, `Microsoft.Windows.Compatibility`).
- Remove `app.config` when only `<assemblyBinding>` remains, and the
  `Properties/AssemblyInfo.cs` that is now empty.
- Remove the compile-only block from `Directory.Build.props` when no project has
  Windows-only steps left, and `Microsoft.NETFramework.ReferenceAssemblies`.
- Retire the YARP catch-all when every legacy route is served by the new
  application (see D4), and the `remote` host when its seam has been rewritten.
- `csproj modernize --tfm net10.0` last.

Proposal: `offramp retire --tfm net48 (--project P | --all) [--apply]` doing the
above through one journal with the compile-set verification `csproj modernize`
already has, and a `finish` stage in the guide with it, `ifdef strip`, and the
proxy and host retirements as steps.

## C. Audit rules that are missing and cheap

Each of these is a `rules/*.yml` entry plus, where noted, a small matcher and a
class in the `behavior` fixture. They are chosen for how often they appear in
codebases of this vintage and how quietly they fail.

### Behavior pack

| Proposed | Detects | Why |
|---|---|---|
| OFR3121 warning | `Encoding.Default` | ANSI code page on .NET Framework, UTF-8 on modern .NET; every `File.ReadAllText(path, Encoding.Default)` silently changes meaning. Symbol match. |
| OFR3122 info | `string.GetHashCode()` whose result is stored, returned, or sent to a database, outside a `GetHashCode` override | Randomized per process on modern .NET; breaks sharding keys, cache keys, and persisted hashes. Matcher over data flow out of the call. |
| OFR3123 warning | `ClientBase<T>` parameterless constructor or `ChannelFactory<T>(string)` / `ClientBase<T>(string endpointConfigurationName)` | The WCF client packages on modern .NET do not read `system.serviceModel` from configuration; the endpoint must be built in code. Fails at runtime, compiles fine. |
| OFR3124 info | `TransactionScope` and `Transaction.Current` | Distributed transactions are Windows-only on .NET 7+ and off by default; a scope over two connections escalates. |
| OFR3125 warning | `X509Store` with `StoreLocation.LocalMachine`, `CspParameters` / `CngKey` key containers | No machine store on Linux; key containers are CAPI/CNG. `ProtectedData` is already caught by CA1416 at trial compilation. |
| OFR3126 warning | `Process.Start` of `cmd`, `powershell`, `*.bat`, `*.exe`, or a path with a drive letter | Extends OFR3107 from the shell-execute default to the Windows executables themselves. |
| OFR3127 info | `Environment.CurrentDirectory`, `Directory.GetCurrentDirectory`, `Assembly.Location` / `CodeBase` used to build paths | Services and containers start with a different working directory; `CodeBase` is obsolete and `Location` is empty in single-file publish. Recommend `AppContext.BaseDirectory`. |
| OFR3128 warning | `AppDomain.CurrentDomain.SetupInformation.*`, `AppendPrivatePath`, `SetData("APPBASE")`, `ConfigurationFile` | Probing paths are `AssemblyLoadContext` concerns now; several throw. |
| OFR3129 warning | a string literal naming a file that exists in the repository with different casing | Case-insensitive on Windows, a `FileNotFoundException` on Linux. Deterministic: compare literals that look like relative paths against the tree. |
| OFR3130 info, experimental | `Directory.GetFiles` / `EnumerateFiles` results used without ordering | NTFS returns names sorted; ext4 does not. Noisy by nature, so `info` and opt-in. |

### Serialization pack

| Proposed | Detects | Why |
|---|---|---|
| OFR3212 error | `.resx` entries whose `type` is not `System.String` / `System.Resources.ResXFileRef`, or whose `mimetype` is `application/x-microsoft.net.object.binary.base64` | These are BinaryFormatter-serialized resources (WinForms designer images, icons, `System.Drawing` types). The SDK's `GenerateResource` warns about them (MSB3822/MSB3825), reading them needs `System.Resources.Extensions`, and BinaryFormatter-encoded entries throw on .NET 9 unless the unsafe switch is on. Read from the XML, located in the `.resx`; nothing else audits resources. |

### Mapping tables

`rules/framework-assemblies.yml` reports these common references as `unknown`:
`Microsoft.VisualStudio.QualityTools.UnitTestFramework` (A2),
`System.Net.Http.Formatting` (→ `Microsoft.AspNet.WebApi.Client`, works on
.NET Standard), `System.ServiceModel.*` as a prefix (`Channels`, `Discovery`,
`Routing`, `Activation`), `PresentationFramework.*` themes, `System.Printing` /
`ReachFramework` (Windows, with WPF), `UIAutomationClient*` (Windows),
`System.Windows.Forms.DataVisualization` (→ `WinForms.DataVisualization`,
Windows), `System.Data.Services*` (→ `Microsoft.OData.Core`),
`System.IdentityModel.Services` (WIF: none; `Microsoft.Identity.Web`),
`System.AddIn*` (none), `Microsoft.SqlServer.Smo` / `ConnectionInfo` /
`Management.Sdk.Sfc` (→ `Microsoft.SqlServer.SqlManagementObjects`),
`Microsoft.SqlServer.Types` (→ `Microsoft.SqlServer.Types` 160+),
`Microsoft.ReportViewer.*` (none), `Oracle.DataAccess` (→
`Oracle.ManagedDataAccess.Core`), `System.Data.SqlServerCe` (none; SQLite),
`Microsoft.Office.Interop.*` / `stdole` / `Microsoft.mshtml` (COM, Windows).

`rules/package-map.yml` lacks: `Unity` / `Microsoft.Practices.Unity` (→
`Unity.Container` or `Microsoft.Extensions.DependencyInjection`), `Ninject`,
`StructureMap` (→ `Lamar`), `CommonServiceLocator`, `Common.Logging` (→
`Microsoft.Extensions.Logging`), `Microsoft.Practices.EnterpriseLibrary.*`
(none), `Microsoft.IdentityModel.Clients.ActiveDirectory` (ADAL →
`Microsoft.Identity.Client`), `WindowsAzure.ServiceBus` (→
`Azure.Messaging.ServiceBus`), `Microsoft.Azure.KeyVault` (→
`Azure.Security.KeyVault.*`), `Microsoft.Azure.DocumentDB` (→
`Microsoft.Azure.Cosmos`), `Microsoft.Azure.Storage.*` (→ `Azure.Storage.*`),
`Oracle.ManagedDataAccess` (→ `.Core`), `RhinoMocks` (→ Moq / NSubstitute),
`MiniProfiler` (→ `MiniProfiler.AspNetCore.Mvc`), `elmah` (→ `ElmahCore`),
`Microsoft.AspNet.Web.Optimization` / `WebGrease` / `Antlr` (→ LibMan or a
bundler), `jQuery` / `bootstrap` / `Modernizr` / `Microsoft.jQuery.Unobtrusive.*`
(client libraries via NuGet → LibMan or npm), `WebActivatorEx` (→ `Program.cs`),
`Microsoft.CodeDom.Providers.DotNetCompilerPlatform` and
`Microsoft.Net.Compilers` (drop), `EntityFramework.SqlServerCompact` (none),
`Microsoft.Data.Edm` / `Microsoft.Data.OData` (→ `Microsoft.OData.Core`),
`DotNetOpenAuth.*` (→ ASP.NET Core authentication handlers), `iTextSharp` (→
`itext7`, license change worth a note), `Glimpse` (none).

## D. The web surface, which is the largest thing nothing touches yet

### D1. Razor views (M)

`web scaffold` ports controller actions that do not render a view; the views,
which are most of an MVC 5 application, are neither audited nor copied
(`WebPorter.cs:217`). Most `.cshtml` files port with small, mechanical edits, and
a few need real work; today nobody knows which is which until they try.

Proposal: `offramp web views --project P` (and a section in `web inventory`),
parsing each view with `Microsoft.AspNetCore.Razor.Language` rather than a
regex, reporting per view: `@helper` (gone), `@Scripts.Render` /
`@Styles.Render` (bundling), `Html.Action` / `RenderAction` (view components),
`Html.Partial` / `RenderPartial` (async or tag helper), `@using System.Web.*`,
`MvcHtmlString` (→ `IHtmlContent`), `Request.*` / `Session[...]` /
`Server.MapPath` / `Response.Write`, `[ChildActionOnly]` targets, and a verdict:
`asIs`, `mechanical` (with the edits), or `rewrite`. Then `web scaffold` can
copy `asIs` views with a `_ViewImports.cshtml` and port the actions that render
them, which moves a large share of pages behind the new application in one step.

### D2. `system.webServer` has ASP.NET Core equivalents that are not generated (S–M)

`config convert` reports `system.web` / `system.webServer` as OFR4403 and drops
them; `web scaffold` does not read them. Several sections map directly:

- `<rewrite><rules>` → `app.UseRewriter(new RewriteOptions().AddIISUrlRewrite(...))`,
  which reads the same XML; write the rules to `iis-rewrite.xml` in the new
  project.
- `<httpProtocol><customHeaders>` → a header middleware.
- `<security><requestFiltering maxAllowedContentLength>` and
  `<httpRuntime maxRequestLength>` → Kestrel `MaxRequestBodySize` and `FormOptions`.
- `<staticContent><mimeMap>` → `FileExtensionContentTypeProvider`.
- `<httpErrors>` / `<customErrors>` → `UseStatusCodePagesWithReExecute`.
- `<authentication mode="Windows">` → a Negotiate note; `<sessionState timeout>`
  → session options.

### D3. `Application_Start` and global filters (S)

Modules and handlers are carried into the new project inside marked regions;
`Application_Start` (container setup, AutoMapper, `FilterConfig`,
`GlobalConfiguration.Configure`) is only mentioned in a next step
(`WebScaffolder.cs:581`). Carry its body into `Program.cs` under
`#if OFFRAMP_APPLICATION_START` the same way, and register global filters whose
types ported in `AddControllers(o => o.Filters.Add<...>())`.

### D4. Route coverage over time (S–M)

For a strangler-fig migration the stakeholder number is "how many routes does
the new application serve". `web inventory` knows the legacy routes and the
scaffold knows what it ported; nothing tracks the number afterwards. Proposal:
`web inventory --new src/Foo.Web.Core` diffs the legacy route table against the
new application's `MapControllerRoute` / attribute routes, writes the count to
the ledger, and `report` draws it.

### D5. WCF services hosted in IIS (M–L)

`audit api` says "move to CoreWCF" and `remote` builds HTTP boundaries for seams,
but there is no generator for the common case: a `.svc` service with
`[ServiceContract]` types, bindings in `system.serviceModel`. A `wcf scaffold`
analogous to `web scaffold` (CoreWCF host, same contracts, `.svc` paths mapped,
bindings translated to code, the client-side OFR3123 rule above pointing at it)
would cover a category that is large in enterprise codebases and small in the
tool today.

## E. Process, CI, and teams

### E1. Audit baselines, so CI can ratchet (S–M)

`verify --baseline` exists for build errors; `ide check` gates new lines for API
findings only. There is no way to say "no new behavior, serialization, or
dead-code findings since the last release" across the repository. Proposal:
`audit <kind> --baseline` (record and compare), keyed by the SARIF partial
fingerprint (rule, project, symbol; line-independent), failing on new findings
with a diagnostic of its own, plus `plan --baseline` for "the framework-only project count went
up" and "a portable project gained a framework-only dependency". Same shape as
the verify baseline, committed with `git add -f`.

### E2. `doctor` checks for large repositories on Windows (S)

Moves deepen paths (`Foo.Tests/Service/...`, `Foo.Core/...`) and monorepos are
already near `MAX_PATH`. Check `LongPathsEnabled` in the registry and
`git config core.longpaths`; warn on `core.ignorecase=false` on a
case-insensitive file system (renames differing only by case); mention
`core.autocrlf` so a reader knows Offramp's writers keep line endings anyway.

### E3. Repositories with many solutions (M)

`scan` wants one solution (OFR0020 with several); real monorepos have dozens
that overlap, and a move across two of them is OFR2010. Proposal: `scan
--projects "src/**/*.csproj"` generating `.offramp/all.slnx` (or `--solutions
a.sln,b.sln` with the union of projects), recorded as the model's solution so
`slice` and `verify` keep working. Without it the graph is partial in exactly
the repositories the tool is for.

### E4. Journals are invisible until something goes wrong (S)

`move rollback --journal PATH` needs a path the user has to find under
`.offramp/journal/`. Add `offramp journal list` (command, time, files, status,
verified) and `move rollback --last`.

### E5. A playbook for people (S)

The guide explains each step in the tool, and `docs/spec` is a contract. There
is no page a migration lead can hand to a team: the base-PR-plus-stacked-PR
pattern for moves, working in slices, who runs `scan` and when, committing
`guide.json` and the ledger, when to `ifdef`, when to seam, when to `remote`,
how to read an `OFR` code. `docs/playbook.md`, mostly assembled from the `why`
paragraphs and the ADRs.

### E6. Dual-target ergonomics after the port (S)

Once a project is `net48;net10.0`, every developer writes C# 13 and the `net48`
side (C# 7.3 by SDK default) breaks the build. `csproj modernize --tfm` should
set `LangVersion` explicitly and add `PolySharp` (`PrivateAssets="all"`) so
`init`, `required`, records, and index/range compile on `net48`; `ide check`
already catches API use, so this closes the language side.

## F. Configuration and desktop

### F1. `applicationSettings` / `userSettings` (S–M)

`Settings.settings` (`Properties.Settings.Default`) is the standard
configuration mechanism in WinForms applications and services of this era.
`config convert` treats the sections as custom sections whose class it cannot
map (OFR4401). `ApplicationSettingsBase` works on modern .NET through the
`System.Configuration.ConfigurationManager` package, so the conversion is:
values to `appsettings.json`, an options class, and a note that the generated
`Settings` class keeps working from `app.config` until callers move.

### F2. `runtimeconfig.template.json` from `<runtime>` (S)

OFR3116 flags GC and thread-pool settings in `app.config`; nothing converts
them. `config convert` can write `runtimeconfig.template.json` with
`System.GC.Server`, `System.GC.Concurrent`, and the `AppContextSwitchOverrides`
that have runtimeconfig knobs, and note the ones that do not.

## G. Checked and fine

For completeness, things I looked at expecting a gap and found handled:
OFR3001 comes from a real trial compilation against the target reference pack
(so `System.Web.HttpUtility`, which exists on modern .NET, is not a false
positive); `csproj modernize` proves the compile set is identical from binlogs;
`deps consolidate` verifies with a real restore and has the CPM preflight; the
purity contract, journals, and resume; `ifdef wrap` refuses members used on both
sides; `deps audit` inspects assets rather than dependency groups; the
`-windows` handling for desktop projects in the guide.

## Suggested order

If the next day is a dogfooding day, the order that removes the most doubt from
what the tool prints:

1. **A1, A2, A3.** Small, and each turns a wrong or empty answer into a right one.
2. **C: `Encoding.Default`, the `.resx` rule, the WCF client rule, the file-name
   case rule,** and the two mapping tables. Each is a rule entry, a matcher
   where noted, and a fixture class.
3. **B2 (`deps gac --apply`) and E4 (`journal list`).** Small, and both are
   things a person reaches for on the first real project.
4. **B3 (tests) and E1 (audit baselines).** These make the port verifiable and
   make CI hold the line.
5. **B1 (the port loop), B4 (the finish stage), D1 (views).** The big ones.
   B1 is mostly composition of what exists; D1 is the largest untouched surface.
