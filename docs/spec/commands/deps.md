# Dependency commands: `deps audit`, `deps consolidate`, `deps resolve-dlls`, `deps gac`, `redirects sync`

All commands read the workspace model. Feed access goes through
`NuGet.Protocol` using the repository's `nuget.config` (private feeds and
credentials included), or exactly the sources in `deps.feeds`. A version already
in the global packages folder is read from there. Package inspection results are
cached under `.offramp/cache/packages/<id>/<version>.json` and never expire unless
`--no-cache` (a published version is immutable); version lists, listing state, and
deprecation are asked for on every run.

## Determining "supports target"

For a package version and a target framework:

1. Download the nupkg (or read it from the global packages folder if present).
2. Collect the frameworks that have assets: `lib/<tfm>/`, `ref/<tfm>/`,
   `runtimes/*/lib/<tfm>/`, `build/<tfm>/`, `buildTransitive/<tfm>/`, and
   `contentFiles/*/<tfm>/`. A package with only `dependencies` groups and no
   assets (meta-package) uses its dependency group frameworks.
3. `supports(target)` = `DefaultCompatibilityProvider.Instance.IsCompatible(target, f)`
   for any collected `f`, via `NuGetFramework`. `netstandard2.0` is compatible
   with `net10.0`; `net48` assets are not. Files directly under `lib/` count as
   .NET Framework (NuGet's legacy rule); files directly under `build/` carry no
   framework. A package with neither assets nor dependency groups supports every
   target.
4. **Windows-only detection**: for each managed assembly under a compatible
   folder, read `System.Reflection.Metadata` assembly references and the
   `SupportedOSPlatform` assembly attribute. Referencing `System.Windows.Forms`,
   `PresentationFramework`, `System.Web` (the Framework one), `System.Drawing`
   (Framework), `Microsoft.Win32.Registry`, or `System.DirectoryServices`
   marks the version `windowsOnly: true`. Only the assets NuGet would pick for
   the target count (the nearest `lib/` folder, else `ref/`): System.Drawing.Common
   8.0 is Windows-only for `net10.0` but not for `netstandard2.0` consumers. This is
   a warning (`OFR1004`), not a fail.
5. **Deprecated/unlisted**: read from the registration index; deprecation
   reasons and alternate packages are surfaced.

Known-mapping table `rules/package-map.yml` (embedded, user-extendable in
`offramp.yml` with `deps.packageMap: [{ package | prefix, replacement }]`; an
exact id beats any prefix, the longest prefix wins, configuration beats the
built-in table) lists Framework-era packages and their modern successors, e.g.
`Microsoft.AspNet.WebApi.Core → Microsoft.AspNetCore.Mvc (framework built-in)`,
`Topshelf → Microsoft.Extensions.Hosting`, `System.Data.SqlClient → Microsoft.Data.SqlClient`,
`Microsoft.Owin.* → ASP.NET Core middleware`, `Swashbuckle → Swashbuckle.AspNetCore`,
`EntityFramework (≥6.4 supports netstandard2.1)`, `WindowsAzure.Storage → Azure.Storage.*`,
`Microsoft.AspNet.Identity.* → Microsoft.AspNetCore.Identity`,
`System.Runtime.Serialization.Formatters (compat, unsupported)`.

## `deps audit`

```
offramp deps audit [--target N] [--package ID] [--project P] [--include-prerelease] [--format table|json|markdown]
```

Result (`schemas/v1/deps-audit.json`):

```jsonc
{
  "target": "net10.0",
  "sources": ["nuget.org (https://api.nuget.org/v3/index.json)"],
  "packages": [
    {
      "id": "Newtonsoft.Json",
      "inUse": [ { "version": "9.0.1", "projects": ["src/Customer.Api/Customer.Api.csproj"], "pinned": true, "deprecated": null },
                 { "version": "13.0.1", "projects": ["src/Shared/Shared.csproj"], "pinned": false, "deprecated": null } ],
      "target": "net10.0",
      "supportsTarget": { "inUseVersions": { "9.0.1": true, "13.0.1": true } },   // null: the feeds could not provide it
      "lowestSupporting": "9.0.1",
      "newestSupporting": "13.0.3",
      "newest": "13.0.3",
      "noVersionSupports": false,
      "windowsOnly": false,
      "windowsOnlyEvidence": null,       // "8.0.0 lib/net8.0/System.Drawing.Common.dll: [SupportedOSPlatform(\"windows6.1\")]"
      "deprecated": null,                // { reasons, message, alternateId, alternateRange } of the newest version
      "replacement": null,               // { match, replacement, source } from the package map
      "status": "ok|upgrade|replace|blocked|unknown"
    }
  ],
  "assemblyReferences": [ { "project": "...", "name": "System.Web", "kind": "framework|file", "hintPath": null,
                            "mapping": { "kind": "none", "package": null, "windowsOnly": false, "note": "..." } } ],
  "summary": { "ok": 120, "upgrade": 14, "replace": 3, "blocked": 2, "unknown": 0 },
  "partial": false
}
```

Candidates are listed stable versions (prerelease with `--include-prerelease`
or `deps.includePrerelease`) plus every in-use version. `newestSupporting` walks
down from the newest candidate; `lowestSupporting` is found by binary search
below it, assuming support, once added, is kept, so a package needs about
log₂(versions) inspections rather than one per version. Every version it returns
was inspected and supports the target. `newest` is the newest listed candidate.

`status`: `ok` every in-use version supports the target; `upgrade` some
version does; `replace` none does but a mapping exists; `blocked` none does and
no mapping; `unknown` no feed has the package, or the feeds could not be reached.
`--format table` (the terminal view) sorts blocked first, then replace, upgrade,
unknown, and ok, each by number of projects; `--format markdown` prints the same
table as Markdown and `--format json` the result alone (`--json` gives the
envelope). `assemblyReferences` lists the model's non-package references with
the `deps gac` mapping for framework assemblies (file references are
`deps resolve-dlls`' job).

Diagnostics: `OFR1001` no version supports target (error), `OFR1002` in-use
version does not support target, `OFR1003` package or in-use version deprecated,
`OFR1004` windows-only assets, `OFR1005` package not found on any feed,
`OFR1006` feed unreachable (result marked partial, exit 4).

## `deps consolidate`

One version per package across the solution, respecting pins, families, and
transitive constraints, validated by NuGet's own restore.

```
offramp deps consolidate (--package ID | --all | --family PREFIX) [--prefer newest|lowest]
                          [--cpm] [--apply] [--dry-run] [--verify restore|build|none]
```

Algorithm:
1. For each package, gather constraints: every direct `PackageReference` range
   (per project, per tfm), every transitive dependency range from every
   resolved package's dependency groups for the relevant tfm (from
   `project.assets.json`), and pins from config (exact equality constraints,
   scoped to a project or global).
2. Candidate = the lowest version ≥ every lower bound that `supports(target)`
   for every tfm in the participating projects, and satisfies every pin; with
   `--prefer newest`, the newest stable satisfying the same.
3. Families (`deps.families`): all members share one version; the family
   candidate is the maximum of member candidates; each member must have that
   version published (Microsoft.Extensions.* does; report `OFR1220` when a
   member lacks it).
4. Pins conflicting with a lower bound from another dependency produce
   `OFR1210` with the full constraint chain (`A → B 3.0 → Newtonsoft.Json ≥ 11`)
   and a suggestion (isolate the pinned project, or add `NoWarn NU1605` for
   that project with a recorded reason). `dotnet nuget why` output is included
   when the SDK supports it.
5. Emit the change set: `Directory.Packages.props`/`deps.cpm.file` with
   `PackageVersion` items, `PackageReference` items stripped of `Version`,
   `VersionOverride` for pinned projects, and for projects opting into a
   non-default CPM path, the two properties (`ManagePackageVersionsCentrally`,
   `DirectoryPackagesPropsPath`) in each project or the shared props file
   they already import (`--opt-in-via PATH`).
6. Verification (`--verify restore`, default): copy the affected project files
   and props into a scratch worktree, run `dotnet restore`, parse
   `NU1605` (downgrade), `NU1107` (version conflict), `NU1608` (outside
   dependency constraint), `NU1010` (missing PackageVersion). Any of these →
   not applied, `OFR1211` with the warnings verbatim. `--verify build` runs
   `verify` afterwards.

CPM preflight (`--cpm`, also in `doctor`): walk the repository for `*.csproj`
files **not** in the solution whose ancestor directories contain a file named
`Directory.Packages.props` (`OFR1301` would inherit CPM unexpectedly),
multiple `Directory.Packages.props` files where a nested one shadows the root
(`OFR1302`), and projects in the solution still using `packages.config`
(`OFR1303`, CPM does not apply to them). The recommended layout for a
monorepo with unrelated projects is a non-default file name plus per-project
opt-in; consolidate defaults to that when `OFR1301` fires.

Result: per package `{ id, current: [...], selected, reason, constraints: [...], changes: [...] }`,
`unsatisfiable: [...]` with chains, and the change set preview.

## `deps resolve-dlls`

Loose assembly references (`HintPath`) → package or project references.

```
offramp deps resolve-dlls [--project P] [--apply]
```

For each `Reference` with a `HintPath`:
1. Read assembly name, version, public key token, `TargetFrameworkAttribute`,
   and referenced assemblies with `System.Reflection.Metadata`.
2. If the assembly name matches a project's `AssemblyName` in the solution →
   propose `ProjectReference` (`OFR1401`).
3. Else search the global packages folder and configured feeds for a package
   whose assets contain that assembly name; rank by version match, then by
   public key token match; propose `PackageReference` with the lowest package
   version whose assembly version ≥ the referenced one and that supports the
   target (`OFR1402`). No match → `OFR1403` with the metadata so the user can
   decide.
4. Report DLLs whose `TargetFrameworkAttribute` is `.NETFramework` and that
   have no package replacement as blockers for the target (`OFR1404`).

## `deps gac`

Framework assembly references → their modern equivalents.

```
offramp deps gac [--project P] [--target N]
```

Rules table `rules/framework-assemblies.yml` maps each Framework assembly to
one of: `builtin` (in the shared framework, remove the reference),
`package: <id>` (e.g. `System.Configuration → System.Configuration.ConfigurationManager`,
`System.ServiceProcess → System.ServiceProcess.ServiceController`,
`System.DirectoryServices → System.DirectoryServices (Windows only)`,
`System.Drawing → System.Drawing.Common (Windows only on net6+)`),
`compat-pack` (`Microsoft.Windows.Compatibility`), or `none`
(`System.Web`, `System.Runtime.Remoting`, `System.EnterpriseServices`,
`System.Workflow.*`) with the recommended direction; names outside the table
are `unknown`. A trailing `*` matches a prefix. Result (`schemas/v1/deps-gac.json`)
lists references by project with the mapping and a count of usages from the
compilation when available (so an unused `System.Web` reference is distinguished
from a real dependency):

```jsonc
{
  "target": "net10.0",
  "projects": [ { "project": "src/Billing/Billing.csproj", "targetFrameworks": ["net48"],
                  "references": [ { "name": "System.Drawing",
                                    "mapping": { "kind": "package", "package": "System.Drawing.Common", "windowsOnly": true, "note": "..." },
                                    "usages": 0 } ] } ],
  "summary": { "builtin": 0, "package": 2, "compatPack": 0, "none": 1, "unknown": 0, "unused": 1 }
}
```

`usages` counts the names in C# source that bind to a type or member defined in
the assembly, in the compilation rebuilt from the compiler log for the project's
.NET Framework target; `null` when there is no compiler log or the project is not
C#.

## `redirects sync`

Binding redirects for `net48` applications, computed from the resolved graph
instead of accumulated by hand.

```
offramp redirects sync [--app PATH ...] [--apply] [--prune]
```

- For each application project (exe, service, web, test) targeting `net48`:
  compute, from `project.assets.json` and the assembly versions inside the
  resolved packages, the set of assemblies with more than one referenced
  assembly version, and the unified (highest) version.
- Rewrite the `<assemblyBinding>` section of the app's `app.config`/`web.config`
  preserving everything else byte-for-byte. `--prune` removes redirects for
  assemblies no longer referenced. Redirects the SDK would auto-generate for
  exe projects are still written for web projects, which the SDK does not
  handle.
- Diagnostics: `OFR1501` redirect added, `OFR1502` redirect changed,
  `OFR1503` redirect pruned, `OFR1504` redirect points at a version not in the
  graph (stale).
- After `deps consolidate`, `redirects sync` typically deletes most redirects;
  the summary says how many.
