# 0062. Give verification's scratch copy the files the scan's build read

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/scaffold.md#csproj-modernize`; extends ADR 0049 and ADR 0061

## Context

`csproj modernize` verifies a conversion by building it in a scratch copy: a `git worktree` of
`HEAD`, plus the model's inputs, the compile items, `HintPath` assemblies, and the converted
projects' folders taken from the working tree (ADR 0049, ADR 0061). After those fixes, all 28 Open
Live Writer 0.6.3 conversions and 1 of 11 SmartStoreNET 4.2.0 ones still failed verification
(`OFR4303`), for two reasons that were not the conversions':

- **The scratch copy lacked files the build reads that `HEAD` does not have.** 20 OLW conversions
  failed with MSB3030: `OpenLiveWriter.CoreServices`, a legacy project each of them references, copies
  `intl/markets/Master.xml`, which exists in the working tree only as an untracked link (the letter-case
  fix `OFR0117` prescribes), and embeds `Marketization/Markets.xml`, which a build-time generator writes
  and git ignores (`OFR0115`'s remedy leaves the generated file in place). SmartStoreNET's
  `SmartStore.Web.MVC.Tests` references the legacy site, whose `EnsureNuGetPackageBuildImports` target
  stops the build when the `packages/<Id>.<Version>/build/` files it imports are missing; `scan` restored
  them into the git-ignored `packages/`, and one of those packages runs a task from its own `tasks/`
  folder. The model's inputs leave `packages/` out on purpose (hashing
  it for staleness would cost too much), and nothing else in the list names a copy source or a resource
  of a project that is not converted.
- **Generated files under a custom intermediate path counted as sources.** 8 OLW conversions failed
  with "sources added: `../obj/Debug/<Project>/<Project>.AssemblyInfo.cs`": `writer.build.settings`
  puts every project's intermediate files in `src/managed/obj/<Configuration>/<Project>/`, and the
  comparison recognized the SDK's generated files only under the project's own `obj/`.

The spec said the scratch copy holds "the committed tree plus the model's inputs from the working
tree"; it did not say what else a build needs, or how a scratch copy stays bounded when the answer is
a restored packages folder.

## Decision

The scratch copy also takes every file inside the repository that the scan's build read, as its
binary log records it: the files each evaluation imported (the build files of restored packages
included), the files its items name (`Compile`, `EmbeddedResource`, `Content`, `None`, `Resource`,
`Page`, `ApplicationDefinition`, `AdditionalFiles`, `Analyzer`, `EntityDeploy`, `COMFileReference`, and a
`Reference`'s `HintPath`), the sources of every `Copy` task that ran, and the assemblies the tasks that
ran were loaded from, with the files beside them (a task assembly loads its dependencies from its
folder: SmartStoreNET's `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` runs `KillProcess` from its
`tasks/` folder). Never a file in a `bin` or `obj` folder (the build writes those), in `.git`, or in the state directory; from `packages/`, only the
files the build read, not the folder. A listed file is copied when `HEAD` lacks it or has it with other
content, and left as checked out otherwise; a symbolic link is copied as the file it points to.

When the scratch copy took files that `HEAD` does not have (untracked or ignored by git), `OFR4309`
(info) names them, so the user knows the verification holds for this working tree and what a clean
checkout would lack. Files of restored packages (`packages/<Id>.<Version>/` of a package a
`packages.config` lists) are counted, not named: a restore brings them back.

A source file in the folder the compiler writes the assembly to (its `/out:`, which is the build's
intermediate output path wherever the project puts it) is the build's own, like one under `obj/`,
and is left out of both compile sets, unless that folder is the project's folder or above it.

## Alternatives considered

- **Link `packages/`, or every folder the build read from, into the scratch copy.** Cheaper on disk,
  but a symbolic link needs a privilege or developer mode on Windows, and anything the scratch build
  writes through it (a package's build step, a restore into the folder) lands in the user's working
  tree, which verification must never touch. Copying the files read is bounded by what the build used.
- **Copy everything `git status` shows (modified and untracked files), plus the ignored files the build
  read.** It reproduces files no build reads (OLW's working tree had 388 untracked files, most of them
  Offramp probes with compiler logs), and still needs the log for the ignored ones, which were the ones
  missing.
- **Record the files the build read in the workspace model at scan time.** It changes the model's
  contract and its staleness (ADR 0049 left `packages/` out of the hashed inputs for their cost); the
  scan's binary log already holds the list, and reading it at verification costs seconds next to the
  builds.
- **Verify in the working tree itself.** The dry run would then write the user's project files, which
  only `--apply` may do.
- **Recognize the SDK's generated files by name** (`<Project>.AssemblyInfo.cs`,
  `.AssemblyAttributes.cs`, `.GlobalUsings.g.cs`). Other generated sources (XAML's `.g.cs`, a
  project's own generators writing to the intermediate path) would still count; the folder is what
  the build says.

## Consequences

Conversions whose legacy dependencies read untracked or ignored files, or restored packages' build
files, verify as the working tree builds, and `OFR4309` says which uncommitted files that relied on.
A verification run reads the scan's binary log once more (a model made from a compiler log alone has
none, and gets the previous list). The log does not record everything a build reads: a target skipped
as up to date logs no inputs (the scan rebuilds, so the common targets run, but a custom target whose
outputs git ignores can still be skipped), a task that did not run was not loaded, and a task's reads
that are not items (a `.resx` file's file references, an `Exec` command's files) are not in it; a
converted project's own folder is copied whole, as before. `deps consolidate --verify build` builds in
the same kind of scratch copy from the model's inputs and could take the same list; `verify` and
`move apply` build in the working tree and need none of this.
