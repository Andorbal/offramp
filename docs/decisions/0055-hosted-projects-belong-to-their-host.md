# 0055. Count a project that lands in a web project's folder as part of that application

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/02-workspace-model.md` (hosted projects), `docs/spec/commands/workspace.md` (`plan`,
  `report`), `docs/spec/commands/deps.md` (`redirects sync`), `docs/spec/commands/scaffold.md` (`web inventory`,
  `web scaffold`)

## Context

The spec called every console, service, web, and desktop project an application. Plugin hosts break that:
SmartStoreNET is one site (`SmartStore.Web`, the only `Global.asax`) whose admin area, a web project nested in the
site's folder, builds into the site's `bin/`, and whose 12 plugins, web projects too, build into
`SmartStore.Web/Plugins/<Name>/`; the site loads them at run time and nothing references them. `report` counted 16
applications, `plan --for SmartStore.Web` left out 13 projects and 104,877 lines the site needs, `redirects sync`
managed 13 `web.config` files the runtime never reads and computed the site's redirects without the plugins' packages,
and `web scaffold` on a plugin proxied to the plugin's Visual Studio URL, which serves nothing (SmartStoreNET field
test P1 #5). DotNetNuke's report counted 20 applications for one site and its modules ("Still open"); its modules
are web projects that build into their own `bin/` and copy their assembly into `Website/bin/` in an `AfterBuild`
target (`Module.build`'s `CopyBin`). nopCommerce's admin and plugins, and Orchard's modules, have the same shape.

The model did not record where a project's assembly goes, so none of this could be derived.

## Decision

The model records each project's output folder (`outputPath`: evaluated `OutDir`, which defaults to `OutputPath`,
repository-relative without a trailing slash, for the .NET Framework target when there is one, since that is the
build a System.Web host loads). A web or library project is **hosted** by a web project when its assembly lands
inside that project's folder and outside its own: its output folder is there, or its own build copies its assembly
there (a `Copy` of `<AssemblyName>.dll` or `.exe` recorded in the binary log). The host is the deepest such web
project (an area nested in a site hosts what builds into the area). A web project with a `Global.asax` of its own is
never hosted: it is an `HttpApplication` wherever it builds to. `scan` records `hostedBy: { project, evidence }`
with the evidence in words: where it lands, no `Global.asax`, a reference to the host, nothing referencing it.

Then an application is an application kind that is not hosted. `report` and `plan --for` take a host's closure as
the host, the projects it hosts (transitively), and everything they depend on; a project reached through a
reference brings its dependencies, not its own host's other guests (`plan --for` a plugin that references the site
lists the site, not the other plugins). `report` lists each application's hosted projects. `redirects sync` computes
the host's redirects from its packages and those of the projects it hosts, and leaves a hosted project's
configuration file alone (`OFR1507`). `web inventory` and `web scaffold` name the host and use its URL (`OFR0204`,
`OFR0205`).

## Alternatives considered

- **`Global.asax` or OWIN `Startup` alone** (a web project without one is not an application). It says nothing
  about which site loads the project, so the closure and the redirects could not follow, and a Web API project
  started by `[assembly: OwinStartup]`, which `scan` cannot see without the semantic model, would stop counting. It
  is kept as a veto only: a project with a `Global.asax` starts an application of its own.
- **References** (a project the site references is part of it). Plugins are loaded at run time precisely so that
  nothing references them; four of SmartStoreNET's plugins reference the site instead, the other way round.
- **Folder nesting** (a web project inside another's folder is part of it). The admin area is nested, the plugins
  are not; and a nested project that builds into its own `bin/` is a separate IIS application.
- **A plugin marker** (`Description.txt`, `plugin.json`, a `.dnn` manifest). Each host has its own; where the
  assembly lands is what every host has in common, and the build decides it.
- **Only the output folder.** DotNetNuke's modules build into their own `bin/` and are copied into the site by their
  build; without the copies its 20 applications would stay.

## Consequences

`report` counts one application for SmartStoreNET (plus its two WinForms tools), `plan --for SmartStore.Web` lists
the admin area and the plugins, and a plugin's redirects are managed in the site's `web.config`. The model's JSON
contract gains `outputPath` and `hostedBy` (both absent when unknown or not hosted); a model an older Offramp wrote
has neither until the next `scan`. The rule is structural: a project that merely copies its assembly into a site
for local debugging is hosted too, which is what the site loads in that build. A web project at the repository root
holds every other project's folder, so a library with a shared output folder elsewhere in the repository would be
hosted by it; no field-test codebase has that shape. Kind overrides in `offramp.yml` do not override hosting.
