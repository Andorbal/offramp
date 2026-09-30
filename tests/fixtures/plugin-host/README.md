# plugin-host

One web application in the shape of SmartStoreNET, nopCommerce, and DotNetNuke: a site that loads projects
nothing references. Legacy (non-SDK) `net48` web projects, built on any OS like `mvc5` (the reference assemblies
come as a package, `WebApplication.targets` only when Visual Studio's web tooling is installed; the tests fill
`packages/` from `packages.config`):

| Project | Shape | What Offramp must conclude |
|---|---|---|
| `src/Site` | the site: `Global.asax`, `Web.config` with a Newtonsoft.Json binding redirect, IIS URL `http://localhost:52001/` | the only application |
| `src/Site/Admin` | an area in a project of its own, nested in the site's folder, `OutputPath` `..\bin\` | hosted by the site (its output lies in the site's `bin/`) |
| `src/Plugins/Tax` | a plugin: `OutputPath` `..\..\Site\Plugins\Tax\`, `Description.txt`, Core referenced with `Private=False`, Newtonsoft.Json from `packages.config`, a `Web.config` with its own redirect, its own IIS URL | hosted by the site; its packages count for the site's redirects; its `Web.config` is left alone; `web scaffold` proxies to the site's URL |
| `src/Modules/Html` | a DotNetNuke module: `OutputPath` `bin\`, an `AfterBuild` copy of its assembly into `..\..\Site\bin` (DotNetNuke's `Module.build`) | hosted by the site (its build copies its assembly into the site's `bin/`) |
| `src/Core` | a plain `net48` library the others reference | not hosted (it builds into its own folder) |

Without the site's knowledge of what it hosts, `report` counted 4 applications, `plan --for Site` listed Core and
Site only, and `redirects sync --prune` removed the site's Newtonsoft.Json redirect as stale.
