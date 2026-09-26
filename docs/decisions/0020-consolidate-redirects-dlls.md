# 0020. Consolidation decides, restore verifies; redirects and loose DLLs from what is on disk

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/deps.md#deps-consolidate`, `#deps-resolve-dlls`, `#redirects-sync`

## Context

The spec gives `deps consolidate` an algorithm, but not these points:
- whether the chosen versions' own dependencies count
- what a family member without the family version does
- where versions go when the repository is not on central package management
- how projects opt into a non-default central file
- whether restore warnings that exist before the proposal block it

`DirectoryPackagesPropsPath` "in each project" does not work, because the SDK
imports the central file before the project body. `deps resolve-dlls` asks to
search feeds for a package "whose assets contain that assembly name", but
NuGet feeds cannot be searched that way. `redirects sync` does not say:
- which projects are applications
- how the deployed assemblies are found
- what happens to redirects for assemblies the graph does not have

## Decision

- **Consolidation decides; NuGet resolves.**
  - The planner collects lower and upper bounds from four sources:
    - direct references
    - every resolved package's dependency ranges in every project's graph
    - the chosen versions' own nuspec dependencies, iterated to a fixed
      point
    - pins
  - It picks the lowest (or newest) listed version that satisfies them and
    supports every target framework of the referencing projects.
  - It does not resolve a graph. `dotnet restore` of the proposal in a
    scratch worktree is the verification. It runs before and after writing
    the proposal, and only problems the proposal adds block it
    (`OFR1211`), so a repository with an old NU1608 can still consolidate.
- **Pins remove a project from the other constraints.** A project pin keeps
  that project's version (`OFR1203`, `VersionOverride` under central
  management). Its own graph's ranges are checked against the pin
  (`OFR1210`), not against the consolidated version. A global pin that breaks
  any range is `OFR1210`, with the chain of the broken range, and the package
  is left alone.
- **Families align to the highest member** when every member has that version
  published. A member without it keeps its own version (`OFR1220`), because a
  family is a preference, not a constraint NuGet enforces.
- **Where versions go:**
  - Already central: the central file.
  - Not central and no `--cpm`: in place.
  - `--cpm`: a full conversion. Every direct package gets a `PackageVersion`,
    because central management requires one for every reference. Packages
    outside the selection keep their versions (the highest as
    `PackageVersion`, `VersionOverride` where projects differ), so `--cpm
    --package X` changes no other version.
- **A non-default central file** is chosen when OFR1301 fires and the file
  name is the default: `<Solution>.Packages.props`. The per-project opt-in is
  `ManagePackageVersionsCentrally` plus an explicit `<Import>` of the file.
  `--opt-in-via PATH` puts both properties in a shared props file that is
  imported early enough. Restore verification proves either works (the
  `cpm-shadowing` test).
- **Loose DLLs.**
  - The candidate package is the one whose id is the assembly name. Its
    versions are inspected for an assembly of that name, the same public key
    token, and a version at least the referenced one, for every target
    framework of the project.
  - An exact assembly version wins over the lowest package version above it.
  - A .NET Framework DLL with no replacement is reported once, as the blocker
    (`OFR1404`), not also as unmatched.
- **Redirects.**
  - Applications are console, service, web, test, winforms, and wpf projects
    with a `net4x` target.
  - The deployed assemblies are those in each resolved package's nearest
    `lib/` folder in the global packages folder, which restore populated.
  - A signed assembly needs a redirect when any deployed assembly references
    another version of it.
  - Redirects for assemblies the graph deploys at one version are left alone:
    harmless, and removing them is not what anyone asked.
  - Redirects for assemblies no package provides are stale (`OFR1504`) and
    are removed only with `--prune`: they may be for framework assemblies.
  - The configuration file is edited as text, entry by entry, so every other
    byte stays.
- **A project without an app.config** is skipped rather than given one. The
  SDK generates redirects for executables, and creating configuration files is
  a larger edit than syncing them.
- **Diffs print removals before additions**, as git does.
- **The three commands live in `Offramp.Refactoring.Dependencies`**, not
  `Offramp.NuGet`. They edit project and configuration files through change
  sets, and the layering (`LayeringTests`) puts Refactoring above NuGet: NuGet
  keeps feeds, inspection, and target support.

## Alternatives considered

- Resolving the whole graph ourselves: CLAUDE.md forbids a NuGet resolver, and
  restore is the authority anyway.
- Failing on any NU1608 after the proposal: a repository with a pre-existing
  NU1608 could never consolidate.
- Searching every package in the global packages folder for a DLL's name: the
  answer would depend on what else the machine has restored.

## Consequences

- A consolidation that restore rejects is reported with NuGet's own words and
  is not applied, so the planner does not have to be perfect.
- `redirects sync` needs a restore to have run (a scan does one). Without the
  packages on disk there is no graph and nothing is added.
