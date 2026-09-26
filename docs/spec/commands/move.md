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
    { "file": "src/Foo/Util/Clock.cs", "to": "src/Foo.Core/Util/Clock.cs", "coMoveOf": null, "sha256": "..." }
  ],
  "projectEdits": [
    { "project": "src/Foo.Core/Foo.Core.csproj", "kind": "addPackageReference", "id": "System.Memory", "version": "4.5.5" },
    { "project": "src/Foo.Core/Foo.Core.csproj", "kind": "addProjectReference", "path": "src/Bar/Bar.csproj" },
    { "project": "src/Foo/Foo.csproj", "kind": "addInternalsVisibleTo", "assembly": "Foo.Core" }
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

## Diagnostics summary

| Code | Meaning |
|---|---|
| OFR2001 | move would create a project reference cycle (path included) |
| OFR2002 | destination equals source |
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
| OFR2201 | test code used by production code (or another project); not moved |
| OFR2202 | multiple candidate test projects |
| OFR2203 | no test project found; use `--to` or `--create` |
| OFR2204 | destination path collision |
| OFR2205 | project language not supported by `move tests` |
| OFR2206 | file linked from outside the project folder |
| OFR2210 | test-framework packages removable from source |
| OFR2301 | string reference to moved type found |
