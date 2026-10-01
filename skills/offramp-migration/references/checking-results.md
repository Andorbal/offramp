# Checking results before you report them

Every field test found answers that looked right and were wrong: dead code that was alive, a
"missing" API that was the wrong API, an upgrade to an older package, portable projects that were
not. Offramp fixed each one it found, but the lesson stands. **Before a number reaches the user,
check a sample of what is behind it, and say what you checked.**

A useful habit is to pick the 5 to 10 findings with the most impact (the most lines, the most
dependents, the most dangerous action) and open each one in the source. Text search is fine for
this: finding a name somewhere else is evidence *against* a claim of dead code. The absence of a
match, however, is not proof that nothing uses the code.

## First: is the model whole and current?

```bash
jq '.result | {buildSucceeded, projects, partial: (.partial|length), notLoaded: (.notLoaded|length)}' scan.json
jq -r '.projects[] | select(.partial) | .id' .offramp/workspace.json
```

- **A partial project undercounts every audit.** `OFR3012` means the project was not audited;
  `OFR3016` means it was audited from a failed build. Quote such numbers as lower bounds, or fix
  the build first ([building-anywhere.md](building-anywhere.md)).
- **A stale model** (`OFR0002`) describes old code. Run `offramp scan --if-stale` after any change.
- **Kinds and hosts** (`kind`, `hostedBy`) drive `plan`, `report`, dead code, and `move tests`.
  Check them against what you learned in Phase 0.
- **Offramp's output is deterministic.** Two runs on the same tree give the same result apart from
  `startedAt` and `durationMs`. If they differ, that is an Offramp bug; report it.

  ```bash
  diff <(jq -S 'del(.offramp.startedAt, .offramp.durationMs)' a.json) <(jq -S 'del(.offramp.startedAt, .offramp.durationMs)' b.json)
  ```

## `plan`

- **`done`**: the project is portable, and nothing .NET Framework-only is reachable through its
  modern targets. A `netstandard2.0` project that references a .NET Framework-only one is
  `blocked` (`OFR0121`), not done.
- **`ready`**: a first-wave candidate. Before promising it, run a dry run of `csproj modernize
  --project P --tfm "<current>;net10.0"`. Its build errors say whether "ready" means an afternoon
  or a month.
- **Cycles.** Projects with `inCycle` (`OFR0120`) share a wave. Break the cycle before porting any
  of them.

## `deps audit`, `deps resolve-dlls`, `deps consolidate`

- **A package "supports" a target only when it has assemblies for that target.** Look at the
  package's folders:

  ```bash
  ls ~/.nuget/packages/<id-lowercase>/<version>/lib ~/.nuget/packages/<id-lowercase>/<version>/ref 2>/dev/null
  ```

  A version with no assemblies is never an upgrade. Before the fix, Offramp proposed
  EntityFramework.SqlServerCompact 6.4.4 → 4.3.1, a 2012 package with no assemblies.
- **Native packages** (`*.win-x64`, `*.win-x86`) are Windows-only, whatever the target.
- **`resolve-dlls` proposals.** Accept one when the package's DLL is the same build as the
  checked-in one:

  ```bash
  sha256sum lib/Foo.dll ~/.nuget/packages/foo/1.2.3/lib/net40/Foo.dll
  ```

  - Check that the diff keeps each reference's condition. On NHibernate, two `Debug`-only
    conditions were dropped before the fix.
  - Treat "newer" and "closest build" matches as proposals for the user.
  - `--apply` verifies the build and rolls back when it fails (`OFR1408`).
- **`consolidate`.** For `packages.config` projects, read the `packages.config` files named in a
  finding before trusting a skew report. Every pin needs a reason from the user (`OFR0051`).

## `redirects sync`

Read every removal in the dry run's diff:
- A redirect for a package that is still referenced must stay.
- Keep a framework assembly's unifying redirect, such as `System.Net.Http`, unless the user
  decides otherwise.
- An application with a partial model is skipped (`OFR1506`). Hosted projects' `web.config` files
  are left alone (`OFR1507`).

## `audit api`

- **Check `result.target` first.** WinForms and WPF projects need `-windows`, and a library may
  need `netstandard2.0`. A wrong target made 96% of Open Live Writer's findings false before the
  fix.
- **Check attribution.** Open a sample of findings at `file:line`. The API named must be the one
  used there. On DotNetNuke, before the fix, about half named the wrong API (`System.Convert` as
  "missing").
- **`OFR3011`** lists packages with no build for the target. Their APIs cannot be audited, and the
  package itself is the blocker. Read this list with `deps audit`.
- **Summarize from the data, not from the first page:**

  ```bash
  jq -r '.result.summary[] | "\(.rule) \(.findings) \(.title)"' api.json
  jq -r '.result.topNamespaces[:15][] | "\(.findings)\t\(.namespace)"' api.json
  ```

## `audit behavior`, `audit serialization`, `audit native`

- **`audit behavior` rules are places to look, not verdicts.** Examples: culture-sensitive
  comparisons, `Process.Start(url)`, `HttpContext.Current`. Decide each rule with the user. A rule
  they turn off goes in `offramp.yml` with a reason.
- **`audit serialization`** separates data that `BinaryFormatter` persists or transports
  (`OFR3203`, which needs a data migration plan) from deep clones (`OFR3202`, which just need
  rewriting). Confirm the persisted ones by finding where the bytes are stored.
- **`audit native`** counts should match the declarations in the compiled files. Uncompiled files
  on disk are left out, as they should be.

## `audit dead-code`

It is the most dangerous audit to trust, because acting on it deletes code.

```bash
jq -r '.result.summary' dead-code.json
jq -r '[.result.projects[] | .project as $p | .candidates[] | select(.confidence=="high") | {p:$p, symbol, kind, loc, file, line}]
       | sort_by(-.loc)[:20][] | "\(.loc)\t\(.symbol)\t\(.file):\(.line)"' dead-code.json
```

For each sampled candidate, check the ways code is reached that no compiler call shows. Start with
the ones that fit the codebase's shape:

- **Reflection and discovery.** For example:
  - `Activator.CreateInstance`, `Type.GetType("...")`;
  - `Assembly.GetTypes()` or type finders (`FindClassesOfType<T>()`);
  - convention-based DI (`RegisterAssemblyTypes`, `Scan`, `AsImplementedInterfaces`);
  - EF6 mapping loops (`AddFromAssembly`).
- **Names in files.** `.config`, `.xml` (NHibernate's `.hbm.xml`, module manifests), `.json`,
  `.aspx`/`.ascx`/`.cshtml` markup, `.js`/`.htm` files.

  ```bash
  rg -n -w 'TypeName' --glob '!*.cs'
  ```
- **Public API of a shipped library, or of an SDK or plugin interface.** Check
  `deadCode.externalConsumers`.
- **COM-visible members** called from script (`window.external`), and serialization-only
  members.
- **Controller actions**, which are reached by routing, not calls. Offramp caps them at `medium`.

Report dead code with its confidence and your sample, for example: "70 high-confidence classes; I
checked the 10 largest: 10 are unreferenced, including by reflection and configuration." Deletion
is the user's decision, one reviewable change at a time.

## `move tests`, `move plan`, `move apply`, `move extract`

- **Read the plan before applying.** Check that:
  - no public type of a shipped project is moving;
  - co-moves have reasons (`OFR2110` for partial types, `OFR2113` for co-moves that are no longer
    needed);
  - nothing would create a reference cycle (`OFR2001`).
- **After applying, the change must be renames only:**

  ```bash
  git status --short               # R entries for the moved files, M only for project files
  git diff --cached -M --summary   # "rename ... (100%)" for every moved file
  ```

- **Verification.** It builds the affected projects and rolls back on failure. A journal lets you
  undo: `offramp move rollback --journal PATH`.

## `csproj modernize`

- **The compile set must be identical.** `OFR4303` is an error when it is not.
- **Read each project's diff for:**
  - build events kept for review (`OFR4302`);
  - conditions kept;
  - output folders kept (`AppendTargetFrameworkToOutputPath=false`);
  - shared or generated assembly-info files left alone (`OFR4306`);
  - package versions raised to match a reference (`OFR4307`);
  - projects not converted (`OFR4304`, for example Visual Basic).
- **Verification runs in a scratch copy.** `OFR4309` lists files that copy needed which HEAD does
  not have (generated files, local links). Tell the user; a teammate's clone lacks them too.
- **With `--tfm`**, the dry run's build errors for the new target are the to-do list. Group them by
  code and file for the user instead of pasting them.

## `seams`, `web inventory`, `report`

- **`seams`.** An extraction larger than a quarter of the project is replaced by `OFR4031` and a
  list of the directly tainted types. Work from those.
- **`web inventory`.** Compare its controller and route counts with a quick look at the code. On
  SmartStoreNET, before the fix, it found 8 of the site's 68 routes; the application's own helpers
  that wrap `MapRoute` hid 57 of them. Its 3 Web API and OData routes, registered in a library,
  were missing too.
- **`report` application counts.** Check that hosted plugins and areas are not counted as
  applications of their own.
