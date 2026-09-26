# Move commands: `move plan`, `move apply`, `move rollback`, `move tests`, `move extract`, `forwarders`

## The purity contract

A move never changes the bytes of a moved file. Not a namespace, not a using,
not a line ending, not a BOM. Git then records a 100% rename, and a pull
request containing only moves shows only renames. Anything that would require
an edit excludes the file from the plan with a diagnostic that says what edit
would be needed, so the user can do it in a separate change.

Project files may change (references added, explicit `Compile` items
adjusted). Those edits are left **unstaged**; renames are **staged** through
`git mv`. Nothing is committed. This supports the workflow "base PR = project
file changes, stacked PR = moves".

## `move plan`

Analyzes candidate files and writes a plan. No repository changes.

```
offramp move plan --from SRC.csproj --to DEST.csproj
                  [--files GLOB ...] [--files-from LIST] [--all]
                  [--co-move closure|none] [--namespace-mismatch allow|warn|block]
                  [--out plan.json]
```

Per file, using the source project's Compilation for each of its target
frameworks:

1. **Symbol collection.** Walk the syntax tree; for every identifier, member
   access, object creation, attribute, `using`, `nameof`, and type syntax,
   resolve the symbol with the semantic model; record the containing assembly
   and, for source symbols, the declaring file(s).
2. **Partition** referenced symbols:
   - **Declared in the same project.** If declared in files also being moved,
     fine. Otherwise, with `--co-move closure` the declaring files are added to
     the candidate set (transitively) and reported as co-moves; with `none` the
     file is excluded (`OFR2101 needs co-move`). Files that the *rest of the
     source project* still needs after moving are allowed to move only if
     `SRC` will reference `DEST` (checked for cycles: `OFR2001 would create
     cycle`, `OFR2002 self reference`).
   - **Declared in another project in the solution.** `DEST` must reference it
     or be able to: the other project's framework class must be compatible with
     every `DEST` target (`standard` and `dual` can be referenced from
     anything modern; `framework` can be referenced only by `framework`/`dual`
     targets' net48 side, which excludes it for a `standard`/`modern` DEST).
     Compatible and not yet referenced → `addProjectReference` unless it creates
     a cycle (`OFR2001`, file excluded, cycle path reported).
   - **From a package.** Find which package supplies the assembly (assets file
     of `SRC`). `DEST` must reference it or be able to (`supports(target)` for
     every `DEST` tfm) → `addPackageReference` with the version `SRC` uses
     (subject to pins/CPM). Not supporting → `OFR2102 package unavailable for
     destination`.
   - **From the framework.** Decided by trial compilation, below.
3. **Trial compilation.** For each `DEST` target framework: take `DEST`'s
   Compilation, add the moved trees (and co-moves), add metadata references for
   references that would be added, and read diagnostics for the added trees
   only. Any error → excluded with the first three errors (`OFR2103 does not
   compile in destination`). Then take `SRC`'s Compilation, remove the trees,
   and read diagnostics for the remaining trees: new errors mean the source
   still needs the code → resolved by `SRC → DEST` reference if acyclic, else
   `OFR2104 source still depends on moved code`.
4. **Platform analyzers.** When a `DEST` target is non-Windows modern, run
   CA1416 (platform compatibility) over the added trees; warnings become
   `OFR2105` (warning; the file still moves unless `--fail-on warning`).
5. **File-pair rules.** `partial` types across files are co-moved (`OFR2110`
   info). `.resx`/`.Designer.cs`/`DependentUpon` groups move together. Files
   listed explicitly in `SRC` (non-globbed) get `Compile Remove`/`Include`
   edits; files under `DEST`'s glob need no csproj edit; files that `DEST`'s
   csproj explicitly excludes are reported (`OFR2111`).
6. **Namespace check.** Namespace vs. `DEST.RootNamespace` mismatch is
   allowed by default (namespaces are not bound to projects). `warn` emits
   `OFR2120`; `block` excludes.
7. **Internals.** Moved code using `internal` members of `SRC` needs
   `InternalsVisibleTo(DEST)` in `SRC` (`addInternalsVisibleTo`, an edit to
   `SRC`'s csproj or `AssemblyInfo`, never to a moved file) or is excluded when
   `SRC` cannot be edited (`frozen`).

Plan file:

```jsonc
{
  "$schema": "https://offramp.dev/schemas/v1/move-plan.json",
  "from": "src/Foo/Foo.csproj", "to": "src/Foo.Core/Foo.Core.csproj", "target": "net10.0",
  "workspaceHash": "sha256:...",
  "moves": [
    { "file": "src/Foo/Util/Clock.cs", "to": "src/Foo.Core/Util/Clock.cs", "coMoveOf": null, "sha256": "...", "needs": [] }
  ],
  "projectEdits": [
    { "project": "src/Foo.Core/Foo.Core.csproj", "kind": "addPackageReference", "value": "System.Memory", "version": "4.5.5" },
    { "project": "src/Foo.Core/Foo.Core.csproj", "kind": "addProjectReference", "value": "src/Bar/Bar.csproj", "version": null },
    { "project": "src/Foo.Core/Foo.Core.csproj", "kind": "addInternalsVisibleTo", "value": "Foo", "version": null }
  ],
  "excluded": [
    { "file": "src/Foo/Legacy/WebHelper.cs", "code": "OFR2103", "message": "...", "details": ["error CS0234: System.Web ..."] }
  ],
  "cycles": [ { "file": "...", "path": ["src/Foo.Core/...", "src/Baz/...", "src/Foo/..."] } ],
  "verify": "end"
}
```

The plan is deterministic and reviewable; agents can edit it (remove entries)
before applying. `--all` plans every file in `SRC`, which is how a project is
hollowed out into a destination in one overnight run.

### Details (M5, `docs/decisions/0019-move-plan-and-apply.md`)

- **Selecting files.** Exactly one of `--files`, `--files-from`, and `--all`.
  `--files` takes paths or globs relative to the current directory, matched
  against `SRC`'s compiled files and the `.resx` files in its folder.
  `--files-from` reads one path per line (`#` starts a comment). A named file
  that is not `SRC`'s is `OFR2004` (exit 2), as is a glob matching nothing.
  `--co-move` and `--namespace-mismatch` default to `move.coMove` and
  `move.namespaceMismatch`. A `frozen` source or destination is `OFR2003`.
- **Plan file shape.** Each move carries `needs`: the other planned files that
  must move no later than it (those it uses, or, when `DEST` already depends
  on `SRC`, those that use it, plus its resource pair). Project edits have one
  shape, `{ "project", "kind", "value", "version" }`: `value` is a project
  path, package id, or assembly name; `version` a package version.
  `keepResourceName` (value: the moved `.resx`, version: its manifest name)
  keeps a moved resource's manifest name with an `EmbeddedResource Update ...
  LogicalName` in `DEST`. Designer metadata (`DependentUpon`, `Generator`)
  follows moved files as `Update` items. Schema: `schemas/v1/move-plan.json`.
- **Trial compilation** runs for every `DEST` target framework against its
  recorded compilation. The source check references the nearest `DEST` target
  and adds `InternalsVisibleTo(SRC)` to `DEST` when remaining source code uses
  moved internals.
- **Dependents.** A project referencing `SRC` that uses moved types must still
  see them. SDK-style dependents see them through `SRC`'s new reference to
  `DEST`. Other dependents, and every dependent when `DEST` already depends on
  `SRC`, get their own `addProjectReference`. A dependent that would close a
  cycle keeps the files it uses (`OFR2001`); one with no compatible `DEST`
  target keeps them too (`OFR2104`).
- **Internals go the way the reference goes.** When `SRC` will reference
  `DEST`, moved code cannot use `SRC` at all, so its needs co-move. Code that
  stays in `SRC` and uses moved internals gets `addInternalsVisibleTo` on
  `DEST` (value: `SRC`'s assembly). When `DEST` already depends on `SRC`, moved
  code that uses `SRC`'s internals gets it on `SRC` (value: `DEST`'s assembly),
  as item 7 says.
- **Platform analyzers.** CA1416 comes from `DEST`'s own recorded analyzers,
  with its recorded MSBuild properties (`build_property.*`) applied to every
  tree, including the moved ones.
- **Output.** The result (`schemas/v1/move-plan-result.json`) is `{ plan,
  output, preview }`, where `preview` is the project-file diff plus the
  renames. `--out PATH` writes the plan file, not the envelope.

## `move apply`

```
offramp move apply --plan plan.json [--verify none|per-project|batch:N|end] [--on-failure rollback|keep] [--resume]
```

1. Check `workspaceHash` matches the current model (`OFR0002` blocks unless
   `--force`).
2. Check every file's `sha256` still matches (`OFR2150 file changed since
   plan`, skipped).
3. Open a journal `.offramp/journal/<yyyyMMdd-HHmmss>-<command>.json`; write
   each step before performing it (rename, edit with the original content, and
   the SHA-256 the file has after the step).
4. Perform project edits first, then renames (`git mv`, or `File.Move` outside
   a repo, creating directories as needed).
5. Verify per the policy. On failure with `rollback`, undo from the journal
   (reverse renames with `git mv`, restore csproj bytes) and exit 1 with
   `OFR2050`. With `keep`, leave the tree and exit 4.
6. On success, print the two path sets: staged renames and unstaged project
   edits, and the suggested commit split.

`--resume` continues an interrupted journal. `move rollback --journal PATH`
undoes a completed run.

### Details (M5, `docs/decisions/0019-move-plan-and-apply.md`)

- **Policies** (`--verify`, default: the plan's `verify`, from `move.verify`).
  Verification builds `SRC`, `DEST`, and their direct dependents with
  `offramp verify`'s machinery (`verify.mode`).
  - `none`: no verification.
  - `end`: one verification after every step.
  - `per-project`: after every step, one verification per project, in
    dependency order, stopping at the first failure.
  - `batch:N`: moves are applied in batches of about N files, verifying after
    each. A batch never separates files that need each other (strongly
    connected under `needs`), and a file's needs are never in a later batch.
    Project edits go with the first batch.
- **Failure.** `--on-failure` defaults to `verify.onFailure`.
  - `rollback` undoes the whole run from the journal, not just the failing
    batch, and exits 1 with `OFR2050`.
  - `keep` stops at the failing batch and exits 4. The journal stays
    `applying`, so `--resume` continues the run once the cause is fixed.
- **Stale plans and files.** A different `workspaceHash` is `OFR0002` and exits
  3 unless `--force`. A planned file that changed, disappeared, or whose
  destination exists is skipped (`OFR2150`), and so is every planned file that
  needs it. Anything skipped makes the exit code 4.
- **Journal.** Each create or edit step also records the bytes it writes
  (`after`), and the journal records the plan (`plan`), so another process can
  finish it.
- **Resume.** `--resume` takes the newest `applying` journal of this plan
  (`.offramp/journal/*-move-apply*.json`) and performs its pending steps. A
  step that was performed before the interruption but not recorded is
  recognized by its result and only marked done. A file that is neither as the
  step expects nor as it leaves it stops the run (`OFR2152`), as does having
  no journal to resume (exit 2). Then it verifies once (`batch:N` becomes
  `end`).
- **Result.** `schemas/v1/move-apply.json`: `plan`, `applied`, `journal`,
  `moved`, `skipped`, `edited`, `rolledBack`, `resumed`, and `verifications`
  (one `verify.json` result per run). A plan file that is missing or not a plan
  is `OFR2005` (exit 2).

## `move tests`

Finds test code in a production project and moves it, purely, to the test
project.

```
offramp move tests --project SRC.csproj [--to TESTS.csproj] [--create]
                   [--include-helpers high|medium|low|none] [--prune-packages]
                   [--apply] [--verify none|end]
```

Detection (semantic, C# only; another language is `OFR2205`):
- **Test file**: any type with an attribute whose containing assembly is a
  known test framework (`xunit.core`, `xunit.v3.core`, `nunit.framework`,
  `Microsoft.VisualStudio.TestPlatform.TestFramework`, `TUnit.Core`), or that
  derives from a known test base type. Confidence `certain`.
- **Helper file**: iterate to a fixpoint over which files use which (every
  simple name bound with the semantic model, in the source project and in the
  compilations of every project that depends on it). A candidate is a non-test
  file with test-support evidence: it uses a test framework, assertion, or
  mocking library (`xunit.assert`, Moq, NSubstitute, FakeItEasy, AutoFixture,
  Bogus, FluentAssertions, Shouldly), a declared type's name contains `Builder`,
  `Fake`, `Stub`, `Mock`, `Fixture`, `TestData`, or `Harness`, or it lives under
  a `Tests`, `Test`, `Testing`, `TestSupport`, `TestData`, `TestHelpers`,
  `Fakes`, or `Mocks` folder. A candidate is a helper when every user of what
  it declares is a test or another helper (or the destination project) and a
  test reaches it; confidence `high`. A file used only by tests but with no
  evidence is the code under test and stays
  (`docs/decisions/0018-move-tests.md`).
- Used by nothing at all: `medium` with evidence (listed for review as
  `candidates`), `low` without (unused code, not listed). A helper whose type
  name appears in a string literal (`Type.GetType("...")`) is never above
  `medium`.
- Files used by production code, or by a project other than the destination,
  are never moved (`OFR2201`, with the users listed).
- `--include-helpers` (default `move.tests.helperMinConfidence`, `high`) moves
  helpers at or above that confidence; `none` moves tests only.

Target selection: `--to`, else a project whose name equals `SRC` name +
`move.tests.targetSuffix` anywhere in the solution (`OFR2202` if several),
else with `--create` a new project next to `SRC` (`src/Bar` gets
`src/Bar.Tests/Bar.Tests.csproj`) named `<Name>.Tests`, else `OFR2203`. The
destination may not be the source (`OFR2002`). A created project is SDK-style,
targets `SRC`'s target frameworks, copies its `LangVersion`, `Nullable`,
`ImplicitUsings`, and .NET Framework `Reference` items, references `SRC`, and
gets the detected framework's packages from `rules/test-projects.yml` (the
framework package at `SRC`'s version); it is added to the model's solution
(and, for a solution filter, to the filter and the solution it filters).

Path mapping: relative path under `SRC` preserved, with a `Tests` directory
segment removed when `stripTestsSegment` (so `Foo/Service/Tests/X.cs` →
`Foo.Tests/Service/X.cs`). Collisions (the path exists, or two files map to
it) → `OFR2204`, file skipped. A file linked from outside `SRC`'s folder has no
destination (`OFR2206`).

Then the `move plan` machinery:
- **Trial compilation** in the destination, for `SRC`'s preferred target
  framework (the first `net4x`, else the first): the destination's recorded
  compilation (or, for a new project, `SRC`'s .NET Framework references), with
  `SRC` minus the moved files (and an `InternalsVisibleTo` for the destination)
  in place of its old reference, plus the package and project references the
  moved files need that the destination lacks. A file with errors stays
  (`OFR2103`, warning, first three errors); repeat until the rest compiles.
  .NET Framework references are never added to an existing destination, so a
  file needing one stays.
- **Source check**: `SRC` minus the moved files must compile with no new
  errors; otherwise nothing moves (`OFR2104`), since `SRC` cannot reference its
  test project.
- **Project edits**: the destination gets `ProjectReference` to `SRC` if
  missing, `PackageReference`s for needed packages (the direct package of
  `SRC` that supplies each assembly, at `SRC`'s version; versionless under
  central package management), `ProjectReference`s for needed projects, and
  `Compile Include` items when not SDK-style. `SRC` loses explicit
  `Compile Include` items for moved files and gets `InternalsVisibleTo` for
  the destination (an item in SDK-style projects, else a line in
  `Properties/AssemblyInfo.cs`) when moved code uses its internals; a
  strong-named `SRC` or a legacy one without `AssemblyInfo.cs` keeps such
  files (`OFR2103`).
- **Pruning**: when nothing left in `SRC` uses a test framework, its
  test-framework `PackageReference`s are reported (`OFR2210`) and removed with
  `--prune-packages`.

Without `--apply` the result carries `preview`: a unified diff of the project
files and the list of renames; nothing is written. With `--apply` the change
set is applied as in `move apply` (journal, edits first, then `git mv`), then
verified once (the source, the destination, and the source's direct
dependents) unless `--verify none` (default `move.verify`). A failed
verification rolls back (`OFR2050`) unless `verify.onFailure: keep` (exit 4).

Result (`schemas/v1/move-tests.json`): `project`, `to`, `created`,
`framework`, `moves: [{ file, to, kind: test|helper, confidence, reasons }]`,
`skipped: [{ file, code, message, details }]`, `candidates`,
`projectEdits: [{ project, kind, value, version }]`, `prunable`, `applied`,
`journal`, `rolledBack`, `preview`, `verify`.

## `move rollback`

```
offramp move rollback --journal PATH
```

Undoes an applied change set from its journal
(`.offramp/journal/<yyyyMMdd-HHmmss>-<command>.json`, `schemas/v1/journal.json`):
renames reversed with `git mv`, edited files restored byte for byte, created
files deleted, and directories the move created removed when empty. Each
journal step records the SHA-256 its file had after the step; when any file
changed since, nothing is undone (`OFR2151`, with the files listed). Result
(`schemas/v1/move-rollback.json`): `journal`, `command`, `undone`, `changed`.

## `move extract`

Bulk form: move a set of types into a **new** project.

```
offramp move extract --from SRC.csproj --types T1,T2,... | --files GLOB --new NAME [--tfm net48;net10.0|netstandard2.0] [--dir PATH] [--apply]
```

Creates the project from a template (SDK-style, same `LangVersion`, `Nullable`,
analyzers, and package versions as `SRC`), adds a `ProjectReference` from `SRC`
unless that would be circular, then runs `move plan` + `move apply` into it.

## `forwarders`

After types moved between assemblies, keep binary consumers working.

```
offramp forwarders --from SRC.csproj --to DEST.csproj [--since GIT_REF] [--apply]
```

- Determines types that existed in `SRC`'s public surface (from the compilation
  at `--since`, or from `SRC`'s last built assembly) and now live in `DEST`.
- Generates `TypeForwarders.cs` in `SRC` with `[assembly: TypeForwardedTo(typeof(...))]`
  for each, and ensures `SRC` references `DEST` (`OFR2001` if cyclic; then it
  reports that forwarding is impossible without a third assembly).
- Also scans the solution for string-based references to the old
  assembly-qualified names (`Type.GetType("Ns.T, Src")`, config files, XAML,
  serialized data patterns) and reports them (`OFR2301`), since forwarders do
  not fix strings that name a type *and* assembly in data files.

### Details (M5, `docs/decisions/0019-move-plan-and-apply.md`)

- **Former surface.** Without `--since`, the former public surface is read from
  the source's compilation in the last scan's compiler log. `move apply`'s
  verification rebuilds `bin/`, so "the last built assembly" would already
  lack the moved types; the scan's log still has them. With `--since REF`, it
  is read from the source folder's C# files at that commit (`git ls-tree`,
  `git show`). A ref that is not a commit is `OFR2302` (exit 2).
- **What is forwarded.** Public top-level types (classes, structs, interfaces,
  enums, records, delegates), found by syntax, that C# files under the
  destination's folder declare now and no file under the source's folder
  still declares. Nested types follow their containing type.
- **Output.**
  - `TypeForwarders.cs` in the source's folder holds one
    `[assembly: global::System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::Ns.T))]`
    per type (generic arity as `<,>`), sorted, with the project file's line
    endings. Forwarders an existing file already declares are kept.
  - Non-SDK projects also get an `addCompile` edit.
  - When the source does not reference the destination yet, an
    `addProjectReference` is added. When the destination depends on the source,
    nothing is written and `OFR2001` (a warning) says forwarding needs a third
    assembly.
- **Strings.** String literals in C# files (by syntax, so comments do not
  count) and lines of `.config`, `.json`, `.resx`, `.settings`, `.xaml`,
  `.xml`, and `.yml`/`.yaml` files are searched. A match is a forwarded
  type's metadata name, then `,`, then the source assembly name.
- **Dry run by default.** `--apply` writes through a journal, which `move
  rollback` undoes. Result: `schemas/v1/forwarders.json`.

## Diagnostics summary

| Code | Meaning |
|---|---|
| OFR2001 | move would create a project reference cycle (path included) |
| OFR2002 | destination equals source |
| OFR2003 | source or destination is frozen |
| OFR2004 | file is not in the source project |
| OFR2005 | move plan file missing or invalid |
| OFR2010 | move crosses solution slice boundary |
| OFR2050 | verification failed; rolled back |
| OFR2101 | needs co-move (files listed) |
| OFR2102 | required package has no version supporting destination targets |
| OFR2103 | does not compile in destination (first errors) |
| OFR2104 | source still depends on moved code and cannot reference destination |
| OFR2105 | Windows-only API used (CA1416) |
| OFR2110 | partial type co-moved |
| OFR2111 | destination excludes the file's path |
| OFR2120 | namespace differs from destination root namespace |
| OFR2150 | file changed since plan |
| OFR2151 | file changed since the move; rollback stopped |
| OFR2152 | interrupted move cannot be resumed |
| OFR2201 | test code used by production code (or another project); not moved |
| OFR2202 | multiple candidate test projects |
| OFR2203 | no test project found; use `--to` or `--create` |
| OFR2204 | destination path collision |
| OFR2205 | project language not supported by `move tests` |
| OFR2206 | file linked from outside the project folder |
| OFR2210 | test-framework packages removable from source |
| OFR2301 | string reference to moved type found |
| OFR2302 | `--since` revision not found |
