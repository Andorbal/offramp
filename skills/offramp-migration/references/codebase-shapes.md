# Codebase shapes

What a codebase *is* changes which target is right, which Offramp answers to distrust, and what the
migration path looks like. Most real codebases mix shapes: an application with its own libraries,
a site with plugins, a desktop application with an SDK. Work out the shape of each part in
Phase 0, and check that Offramp's model agrees:

```bash
jq -r '.projects[] | [.id, .kind, .frameworkClass, (.hostedBy.project // "")] | @tsv' .offramp/workspace.json
```

A wrong `kind` distorts `plan`, `report`, `audit dead-code`, and `move tests`. Correct it in
`offramp.yml` (`projects: - path: ... kind: ...`) and `scan` again.

The first four shapes below are the field-tested ones: DotNetNuke, NHibernate, SmartStoreNET, and
Open Live Writer. The last is not field-tested yet, so check its results with extra care.

## A library shipped on NuGet (NHibernate)

**Target.** `netstandard2.0` next to the .NET Framework target (`net48;netstandard2.0`), as long as
.NET Framework applications consume it. Add `net10.0` later if the library needs APIs .NET Standard
lacks.
- Audit against every target the library will ship: `audit api --target netstandard2.0`.
- On NHibernate that found 114 missing APIs, 78 of them Reflection.Emit. Against `net10.0`, which
  has Reflection.Emit, it found 36.

**Public API is used where Offramp cannot see.**
- **Dead code.** Offramp treats a library as shipped, and rates its public symbols at most
  `medium`, when:
  - a `.nuspec` packs it;
  - it is packable;
  - no application in the solution uses it.

  A library that other *repositories* use needs `deadCode.externalConsumers` in `offramp.yml`.
  Before the fix, NHibernate showed what happens without this: 250 public APIs were called dead at
  high confidence. Among them were SQL dialects that users select by name in their configuration.
- **Moving tests.** `move tests` never moves a shipped project's public types. Check anyway that
  nothing public is in its plan.

**Keep binary compatibility.**
- `audit api-compat --project P --baseline <last release tag>` compares the public API with the
  last release.
- When `move extract` moves types to a new assembly, `forwarders` adds `TypeForwardedTo` so old
  binaries still load.
- Strong naming: `move extract` signs the new project with the source's key. `OFR2114` names friend
  assemblies (`InternalsVisibleTo`) that the new project does not grant yet.

**Checked-in DLLs** (`lib/*.dll` by `HintPath`). `deps resolve-dlls` matches them to packages.
- On NHibernate, 12 of 15 were byte-identical to a package. Accept those.
- Treat "newer" and "closest build" as proposals for the user.
- Outside Windows, a legacy project keeps its DLL references (`OFR1407`); convert it with `csproj
  modernize` first.

**Other traps:**
- **Generated shared files.** A build script's generated `SharedAssemblyInfo.cs` is left as it is
  by `csproj modernize` (`OFR4306`).
- **Test projects.** Test projects that reference NUnit by `HintPath` are recognized as tests.
  Check the `kind` anyway.
- **`seams`.** A central configuration class often ties most of a library into one component.
  `seams` then reports `OFR4031` instead of proposing to extract half the library. Work from the
  directly tainted types it lists. An API that a package supplies on the target (for example
  `ConfigurationManager`) is not a blocker (`OFR4032`).

## An ASP.NET MVC 5 or Web API application with plugins (SmartStoreNET)

The same shape covers nopCommerce 3.x, Orchard 1.x, and, in part, Umbraco 8.

**Plugins and areas belong to the site.**
- **How the model shows it.** A web project whose output lands in the site's folder, without its
  own `Global.asax`, is *hosted* (`hostedBy`, `OFR0204`).
- **What that changes.**
  - `plan --for <site>` includes the hosted projects: 18 projects on SmartStoreNET, instead of 5.
  - `report` counts one application, not one per plugin.
  - `redirects sync` manages the site's `web.config`.
- **What to check.** Confirm with the user that the hosted projects are really loaded by that
  site.

**Types found by reflection.** Registrars, EF6 mapping classes, route providers, startup tasks, and
consumers are found at run time by a type finder (`FindClassesOfType<T>()`) or an assembly scan.
- Offramp follows `typeof(T)`, `Type` parameters, `IsAssignableFrom`,
  `GetGenericTypeDefinition() == typeof(G<>)`, and EF's `AddFromAssembly`.
- A codebase's own discovery helper may still escape it.
- Before the fix, 110 EF mappings and 12 registrars (including the one that builds the whole
  container) were called dead at high confidence. After it, 70 high-confidence classes remained on
  SmartStoreNET. Open each one before anyone deletes it.

**The web layer is the bulk of the work.**
- **Size it.** With MVC and Web API visible from `packages.config`, `audit api` finds about 11,000
  uses on SmartStoreNET: System.Web.Mvc 6,989 and System.Web.Http 714.
- **Inventory it.** Run `web inventory` on the site first. It lists:
  - controllers and routes, including route helpers that wrap `MapRoute`;
  - areas;
  - filters, including those registered in the container;
  - bundles;
  - modules and handlers from `web.config`.
- **Port it.** `web scaffold --project <site> --new <folder> --proxy yarp` sets up a new ASP.NET
  Core project in front of the old site. It ports only simple actions: none of SmartStoreNET's 270 actions yet, since 151 render
  Razor views and 66 derive from the application's own base controllers. Plan the port area by
  area with the user, and treat each area as manual work that the inventory sizes.

**Other traps:**
- **Shared assembly-info files.** Files that many projects link (`OFR4306`).
- **NuGet 2 restores.** `.nuget/NuGet.targets` with `RestorePackages` and `SolutionDir`.
  `csproj modernize` removes the import together with `RestorePackages`.
- **`Microsoft.Bcl.Build`.** `OFR0124`.
- **Native packages.** `deps audit` reports native packages (`*.win-x64`) as Windows-only. That is
  right, and it matters for a Linux container.
- **Binding redirects.** `redirects sync --prune` may remove a unifying redirect for a framework
  assembly such as `System.Net.Http`. Keep it unless the user decides otherwise.
- **Letter case.** SmartStoreNET had 14 paths spelled differently from the disk.

## An ASP.NET Web Forms application with modules (DotNetNuke)

**There is no Web Forms on modern .NET.**
- The UI layer is a rewrite.
- The migration is a strangler: a new ASP.NET Core application in front, with a YARP proxy, taking
  over routes one at a time.
- The libraries underneath port first, wave by wave, while the site keeps running on .NET
  Framework.
- Set that expectation early.

**Sizing the coupling.** `audit behavior` counts `HttpContext.Current`, 628 uses on DotNetNuke.
That number measures how much code assumes a request context.

**Dead code in Web Forms.** Code-behind classes are reached from `.aspx`, `.ascx`, and `.master`
markup, from `AutoEventWireup` handlers, and from module manifests (XML).
- Offramp reads all of these. Before the fix, it called `Default.aspx`'s page class dead at high
  confidence, so check a sample anyway.

**Other traps:**
- **Modules that copy into the site.** Modules that copy their assembly into `Website/bin/` after
  building are hosted by the site.
- **Portable only on paper.** A `netstandard2.0` project that references a .NET Framework-only
  project is not portable (`OFR0121`). `plan` shows it as `blocked`, not `done`.
- **`HintPath`s into other projects' `bin/` folders** (`OFR1401`). These make build order matter.
  Replace them with project references before porting.
- **Binding redirects on a partial model.** `redirects sync` skips an application whose model is
  partial (`OFR1506`). Fix the build first.

## A WinForms or WPF desktop application with COM and P/Invoke (Open Live Writer)

**Target.** `net10.0-windows`.
- **What Offramp does.** It compiles a project that references Windows Forms or WPF (or sets
  `UseWindowsForms`/`UseWPF`) for `-windows`. Check the `target` in each audit result.
- **Before the fix.** 13,374 of 13,910 `audit api` findings (96%) were Windows Forms APIs that
  `-windows` has.
- **A remaining gap.** `deps audit` still audits a desktop application's packages against
  `net10.0`.

**Controls that .NET removed.** `MenuItem`, `ContextMenu`, `Menu`, and `DataGrid` compile against
shims that throw at run time (`OFR3003`: 165 on Open Live Writer). This is real UI work; list it
for the user by form.

**Interop.**
- **P/Invoke and `[ComImport]` interfaces.** These generally keep working on `net10.0-windows`.
  `audit native` lists them: 314 and 244 on Open Live Writer.
- **COM references that need `tlbimp`.** These need Windows to build (`OFR0111`).
- **Methods that JavaScript calls on a `[ComVisible]` object** (`window.external`). These have no
  C# caller. Offramp rates them `low`; never delete them on dead-code evidence.
- **A plugin SDK shipped with a `.nuspec`.** Its public API is used by plugins outside the
  repository.

**Behavior.**
- **Opening URLs.** `Process.Start(url)` no longer opens a URL, because `UseShellExecute` is false
  by default. The `process-start-url` codemod rewrites exactly those calls: 11 on Open Live Writer.
- **Persisted data.** `BinaryFormatter` data written to the registry or to disk (`OFR3203`) needs
  a data migration plan, and that decision is the user's.

**Build.**
- **Native projects in the solution.** These are not in the model (`OFR0024`). A solution-level
  dependency on one can keep the application from building outside Windows.
- **Other steps outside Windows.** Build-time generators (`OFR0115`), a shared settings file that
  overrides `MSBuildExtensionsPath` (`OFR0122`), images in `.resx` files (`OFR0119`), and MSTest v1
  (`OFR0125`). All are covered in [building-anywhere.md](building-anywhere.md).

## Services, WCF, and consoles (not field-tested at scale)

- **Windows services.** Services built on `ServiceBase` or Topshelf become hosted .NET worker
  services with `offramp service --project P --host linux|windows|both`. Review the generated
  worker against the original `OnStart` and `OnStop` behavior.
- **WCF.** WCF clients have packages (`System.ServiceModel.*`). A WCF service host moves to
  CoreWCF, gRPC, or HTTP APIs, and `audit api` flags it. Choosing among them is the user's decision,
  because it changes what clients see.
- **Configuration.** `config convert` turns `app.config` and `web.config` into
  `appsettings.json`. `system.serviceModel` is reported (`OFR4402`), not converted.

This shape has not had a field test on a large codebase. Check its results against the source even
more carefully, and tell the user that you did.
