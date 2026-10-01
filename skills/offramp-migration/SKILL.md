---
name: offramp-migration
description: Guide a team through an incremental migration of a .NET Framework codebase to modern .NET with the Offramp CLI. Use when someone wants to assess, plan, prepare, or port a .NET Framework solution (legacy csproj, packages.config, ASP.NET MVC or Web Forms, WCF, WinForms or WPF, a library shipped on NuGet) to .NET 8 or later or to .NET Standard, or wants such a solution to build on macOS or Linux.
---

# Guiding a .NET Framework migration with Offramp

You are helping a team move a real .NET Framework codebase to modern .NET, one reviewable step at
a time. Offramp does the analysis and the mechanical changes. Your job is to:

- run it in the right order;
- check what it reports against the code;
- explain the results in plain terms;
- bring each decision to the people who own it.

This guide comes from running Offramp on four large open-source codebases of different shapes:
- DotNetNuke, a Web Forms CMS;
- NHibernate, a library shipped on NuGet;
- SmartStoreNET, an MVC 5 site with plugins;
- Open Live Writer, a WinForms application with COM interop.

On every one of them, some numbers looked plausible and turned out wrong when checked against the
source. Before you report any finding to the user, read [Check before you trust](#check-before-you-trust).

Reference files, loaded when needed:

| File | Read it when |
|---|---|
| [references/building-anywhere.md](references/building-anywhere.md) | getting the solution to build on macOS, Linux, or with `dotnet build` on Windows: every `scan` diagnostic and its fix |
| [references/codebase-shapes.md](references/codebase-shapes.md) | you know what the codebase is (library, web application, plugin host, desktop, service) and need to know what that changes |
| [references/checking-results.md](references/checking-results.md) | before you report numbers from an audit, `deps`, `plan`, or `move` command |
| [references/commands.md](references/commands.md) | you need a command's syntax, what it writes, how long it takes, or a `jq` recipe for its output |

## Ground rules

1. **Nothing is written without consent.** Every Offramp command that changes the repository is a
   dry run unless it gets `--apply`. Run the dry run, show the user the diff and the counts, and
   apply only after they agree. There are two exceptions: Offramp's own state in `.offramp/`, and
   `init`, which writes `offramp.yml` and `.gitignore` unless it gets `--dry-run`.
2. **Never commit, push, or open a pull request unless asked.** Offramp stages its renames (`git
   mv`) and leaves everything else in the working tree for the user to review. Leave it that way.
3. **One step, one change set.** Each applied step should be small enough to review as one pull
   request: one command's result, verified, before the next command runs.
4. **Verify with the real toolchain after every write.**
   - Build: `dotnet build` of the solution, or `offramp verify`.
   - Test: run the tests that can run on this machine.
   - Inspect: `git status` and `git diff --stat` must show only the files you expected.
5. **A build is not a run.** A build on macOS or Linux proves the code compiles; it cannot run
   .NET Framework output, including its tests. Keep a Windows build and test job, and tell the
   user which claims only that job can prove.
6. **Use the command, not a hand edit, when a command exists for the change.** Moving files,
   consolidating versions, converting project files, syncing redirects: the commands verify and roll
   back. For anything else, follow the fix the diagnostic documents.
7. **Never move and edit in one step.** A move renames files and changes nothing inside them. If a
   moved file needs a namespace change or any other edit, that is a separate, later change.
8. **Find code by its meaning, not its spelling.** To decide what uses what, use Offramp's
   analyses, which read the compiled semantic model; do not act on the results of a text search.
   Searching text is fine for finding your way around.

## Running Offramp as an agent

- **Prefer the MCP server when your client supports it.** Register it once:

  ```bash
  claude mcp add offramp -- offramp mcp serve
  ```

  - Without `--allow-apply`, every tool call is a dry run, and the response says so. Add
    `--allow-apply` only after the user agrees that you may apply changes; even then, Offramp never
    commits.
  - Call `offramp_help` first.
  - Tools are named `offramp_<group>_<command>` (for example, `offramp_deps_audit`).
  - `offramp://workspace` is the model, and `offramp://diagnostics/<code>` is a diagnostic's
    documentation.
- **On the command line, pass `--json -q` and redirect stdout to a file.** The JSON envelope goes
  to stdout; `-q` keeps the progress events (one JSON object per line on stderr) out of your
  context.
  - Summarize the file with `jq`. Outputs of several megabytes are normal on a large solution
    (`audit api`, `seams`). Never paste raw output into the conversation.
  - Don't use `--out x.json` to save an envelope. For the audits and `seams`, a `.json` file
    written with `--out` holds the result alone, without the envelope, and the envelope then goes
    to stdout. Use `--out` for pages people read (`graph`, `report`).
- **Exit codes:**
  - `0`: success.
  - `1`: finished with findings. That is the point of an audit, not a failure.
  - `2`: usage error.
  - `3`: environment error.
  - `4`: partly applied; the envelope says what was skipped.
  - `130`: interrupted.
- **Every diagnostic has a stable code** (`OFR####`) with a documented meaning, cause, and fix: the
  envelope's `help` link, or the MCP diagnostics resource. Apply the documented fix before you
  invent one.
- **Plan for the time things take.** On a solution of about 400,000 lines:
  - a `scan` whose build succeeds takes about a minute; one whose build fails can take several;
  - each audit takes a few minutes;
  - `plan`, `graph`, and `report` take about a second.

  Give long commands a generous timeout, or run them in the background.
- **Use `offramp guide --json` as the shared checklist.** It reports the stages, the open steps, and
  what is done, and keeps progress in `.offramp/guide.json`, which the team can commit. Move it
  along with `--run STEP`, `--done STEP`, and `--skip STEP`. The guide is deliberately simple; this
  skill adds the judgment it lacks.

## The migration, phase by phase

### Phase 0: learn what the codebase is

Before any command writes anything, find out:

- **Which solution is the real one?** Look at `CONTRIBUTING.md`, the build scripts, and the CI
  configuration.
  - When there are several solutions, `init` and `scan` choose one by rule and say why
    (`OFR0023`): the only one without a Web Site project, the one that contains all the others,
    or the one with the most projects. Check that choice against the build scripts.
  - On a tie (`OFR0020`), `init` leaves `solution:` empty and `scan` stops.
  - Either way, `--solution` or `solution:` in `offramp.yml` settles it.
- **What does it ship?** An application (web, desktop, service), a library on NuGet, plugins
  loaded at run time, or an SDK other repositories use. The shape changes:
  - which target is right;
  - how to read the dead-code audit;
  - what `move tests` may move;
  - what `plan` counts as an application.

  See [codebase-shapes.md](references/codebase-shapes.md).
- **How is it built today?** Look for:
  - build scripts (NAnt, psake, Cake, FAKE) that generate files before MSBuild runs;
  - post-build steps (ILRepack, `xcopy`, installers);
  - native projects;
  - T4 templates and EDMX models.
- **What is the goal?** The .NET version, and a target per project:
  - `net10.0`;
  - `net10.0-windows` for WinForms and WPF;
  - `netstandard2.0` for a library that keeps serving .NET Framework consumers.
- **Where does it run, and who builds it where?** Windows servers or containers, the operating
  systems the developers use, and whether CI has a Windows agent.

Write the answers down for the user. Most later decisions depend on them.

### Phase 1: make it build everywhere

```bash
offramp doctor --json -q > doctor.json          # environment and repository checks
offramp init --defaults [--solution PATH]        # writes offramp.yml
offramp doctor --fix --json -q > fix.json       # dry run: the diffs it would apply
offramp doctor --fix --apply                     # once the user agrees
offramp scan --json -q > scan.json
```

What `doctor --fix` writes:
- the compile-only block in the root `Directory.Build.props`;
- a condition on each Windows-only setting in the project files that set it;
- for `packages.config` solutions, `Offramp.PackagesConfig.targets` and `Directory.Solution.targets`.

With these, a plain `dotnet build` on macOS or Linux does what `scan`'s build does, and `dotnet
build` on Windows compiles the same way. Visual Studio's own build is unchanged. Problems that
belong to the repository itself remain, and the next loop finds them: letter case, generated
files, native projects, Web Site projects.

These files change every developer's build, so the team must review and commit them. Before the
user decides, show them `doctor`'s "Builds without Offramp" check (`plain-build`). It lists what a
build outside Windows skips, such as sgen, precompiled views, and build events.

Then repeat until the build succeeds:
1. Run `scan`.
2. Read its `OFR01xx` diagnostics.
3. Apply each documented fix.
4. Run `scan` again.

Keep going until `buildSucceeded` is true and no project is partial, or until the user accepts the
partial projects that remain. [building-anywhere.md](references/building-anywhere.md) gives each
diagnostic's fix and what the field tests needed. An audit of a partial model counts less than is
there (`OFR3012`, `OFR3016`), so say so whenever you quote numbers from one.

Finish the phase the way a new developer would start: build a fresh clone (a new `git worktree`
or clone, not a cleaned working tree) with a plain `dotnet build` and no Offramp. Report the
result.

### Phase 2: understand (read-only)

```bash
offramp plan --waves --json -q > plan.json
offramp graph --format html --out graph.html --exclude-kind test
offramp deps audit --json -q > deps.json
offramp audit api --json -q > api.json
offramp audit behavior --json -q > behavior.json
offramp audit serialization --json -q > serialization.json
offramp audit native --json -q > native.json
offramp audit dead-code --json -q > dead-code.json
offramp web inventory --project P --json -q > web.json      # each web application
offramp report --format html --out report.html
```

Summarize the results for the user. Cover:
- the size of the codebase;
- the waves and the blockers;
- packages with no modern build;
- the areas with no port (System.Web, WCF server, remoting, AppDomains, `BinaryFormatter` data
  in storage);
- behavior that will change silently;
- dead code, with its confidence and what you checked.

Check a sample of every headline number against the source first
([checking-results.md](references/checking-results.md)), and say that you did. `report` produces
an HTML page stakeholders can read, and each successful scan adds a point to its trend.

### Phase 3: prepare, still on .NET Framework

Each of these steps is a separate, reviewable change that keeps the solution on .NET Framework. Run
the dry run, check it, apply it with the user's consent, verify it, and hand it over for review.

1. **`move tests --project P`**: tests that live in production projects move out, as pure renames.
2. **`csproj modernize --all`** (or `--project P`): SDK-style project files, same target. It verifies
   each conversion in a scratch copy.
   - Read what it reports about shared assembly-info files (`OFR4306`) and build events (`OFR4302`).
   - Check any conversion that fails verification one project at a time.
3. **`deps consolidate --all`**: one version per package. A version that must stay becomes a pin in
   `offramp.yml`, and every pin needs a reason from the user.
4. **`deps resolve-dlls`**: checked-in DLLs become package references. Accept a proposal only when
   the package's DLL is the same build as the checked-in one.
5. **`redirects sync`**: binding redirects match the packages. Review every redirect `--prune`
   would remove before applying.
6. **`codemod list`, then `codemod run --mod NAME`**: one mechanical rewrite at a time. Read each
   mod's notes; `sqlclient`, for example, changes the default connection encryption (`OFR4510`).

### Phase 4: port, a wave at a time

- **Find what can port today.** `plan --frontier` lists the projects whose dependencies are all
  portable. Port those first; the leaves come before the projects that use them.
- **Add the modern target next to the old one.** Run `csproj modernize --project P --tfm
  "net48;net10.0"` as a dry run.
  - The dry run fails until the code compiles for `net10.0`. Its errors are the to-do list.
  - Fix them while the project is still on .NET Framework, so applying never leaves the repository
    unable to build.
  - `--accept-diff` fixes forward instead. That is the user's call.
- **Libraries others consume:**
  - target `netstandard2.0` alongside the .NET Framework target;
  - audit with `audit api --target netstandard2.0`;
  - check the public API against the last release with `audit api-compat --project P --baseline
    <tag>`.
- **Code that cannot port:**
  - find the smallest boundary around it with `seams`;
  - hide it behind an interface with `extract interface`;
  - `remote` puts that boundary behind HTTP;
  - move the portable part out with `move extract`, and keep old binaries working with
    `forwarders`.
- **Tests:** keep them dual-targeted. Run the modern target anywhere; run the .NET Framework
  target on Windows.
- **Applications come last:**
  - `config convert`: from `web.config` or `app.config` to `appsettings.json`;
  - `service`: Windows services become hosted services;
  - `web scaffold` with a YARP proxy: a strangler-fig setup, after `web inventory`. It ports little
    automatically; views and controllers that inherit from the application's own base classes are
    manual work, so plan for that;
  - WinForms and WPF go to `net10.0-windows`.

### Phase 5: keep it from sliding back

- **In CI:** `offramp ide check --base origin/main --fail-on error` flags new code that adds
  .NET Framework-only API.
- **In the editor:** the VS Code extension does the same while people type.
- **Track progress:**
  - `offramp report --since DATE` shows the trend;
  - `offramp verify --baseline` records today's known errors, so only new ones fail.

## Check before you trust

Every field test found plausible, wrong answers. The traps, in short
([checking-results.md](references/checking-results.md) has the checks):

- **High-confidence dead code is not proof that code is unused.** Code can be reached where no
  compiler sees it:
  - reflection-based type finders;
  - DI registration by convention;
  - types named in configuration or XML mappings;
  - markup;
  - JavaScript calling a COM-visible object;
  - a library's public API, which its consumers use.

  Open the evidence for a sample before anything is deleted.
- **`audit api` is only as right as the target it compiled for.**
  - WinForms and WPF need `-windows`.
  - A library's real goal may be `netstandard2.0`.
  - MVC and Web API from `packages.config` must be visible to the audit.

  Check the `target` in the result.
- **Version proposals need evidence.** Check that the proposed package's DLL is the checked-in
  build, not just the same assembly version, and that conditions were kept.
- **Project kinds drive everything downstream.** A test project that references NUnit by
  `HintPath`, an MSTest v1 project, a plugin that builds into the site: check `kind` and `hostedBy`
  in the model. Correct a wrong kind with `projects: - path: ... kind: ...` in `offramp.yml`.
- **A partial model undercounts.** Numbers from partial projects are lower bounds.
- **Say what you checked.** For example: "I opened 10 of the 70 high-confidence dead classes; 10
  of 10 are unreferenced, including by reflection." A number with no check behind it is only a
  guess.

## Decisions that belong to people

Ask; don't decide. Give the context, the options, and your recommendation with its reason.

- The target for each project: `net10.0`, `-windows`, `netstandard2.0`, or dual-targeted.
- Which application to move first, and whether the old one keeps running beside it.
- Whether to commit the files `doctor --fix` writes, since they change every developer's build.
- Every pin, rule override, exclusion, and frozen project in `offramp.yml`, each with a reason.
- Deleting dead code; fixing forward (`--accept-diff`) instead of fixing on .NET Framework first.
- Data that `BinaryFormatter` wrote to storage, and anything else whose meaning changes at run
  time.
- Renaming files, or references, whose letter case differs between the code and the disk. Offramp
  reports these and does not rewrite them: how an item is spelled names embedded resources and
  copied files, on Windows too.
- CI changes, and anything that commits, pushes, or publishes.

## Reporting progress

After each step, tell the user:

- **What ran:** the command and its exit code.
- **What changed:** files, with counts.
- **How it was verified:** build, tests, `git status`.
- **The numbers that moved:** before and after.
- **What needs their decision**, if anything.
- **The next step.**

Keep the guide's state current with `offramp guide --done STEP` or `--skip STEP`. Keep it short:
the diff is the record.

## When Offramp is wrong

It will be sometimes.

- **Work around it where the workaround stays visible.** Use `offramp.yml`:
  - a `kind` override for a misclassified project;
  - `deadCode.externalConsumers` for a library other repositories use, when Offramp does not
    already treat it as shipped (see [codebase-shapes.md](references/codebase-shapes.md));
  - `rules` with a reason, to change a rule's severity;
  - `paths.exclude` for projects Offramp must never modify (they are still analyzed).

  Every override shows up in each command's `effectiveConfig`. Do not edit results by hand.
- **Collect the evidence:**
  - `offramp --version`;
  - the command and its JSON envelope;
  - the files the finding points to;
  - why the finding is wrong.
- **Offer to report it upstream.** Don't file anything without the user's consent.

Gaps known at the time of writing, which you may meet:
- `scan` asks MSBuild to build native (C++) projects, so a solution-level dependency on one can stop
  the build. Leave its `Build.0` line out of the solution configuration for the scan.
- `csproj modernize` keeps an MSTest v1 reference, so a converted MSTest v1 test project has no
  test framework outside Windows. Move it to `MSTest.TestFramework`.
- The letter-case check (`OFR0117`) only fires on a case-sensitive file system, so run a scan on
  Linux before you promise a Linux build.
- `web scaffold` ports only simple controllers.
- `redirects sync --prune` can remove a framework assembly's unifying redirect, such as
  `System.Net.Http`.
