# 0031. Treat a `deps.cpm.file` with a folder as repository-relative, and find the file projects use

- Status: accepted
- Date: 2026-09-28
- Spec section: `docs/spec/03-configuration.md`, `docs/spec/commands/deps.md`

## Context

The spec's example is `file: eng/Packages.props` with `scope: solution`, but
the writer always prefixed the solution's folder, so a solution in `src/` put
that file at `src/eng/Packages.props`, and an `init` answer such as
`apps/Legacy/Directory.Packages.props` for `apps/Legacy/Legacy.sln` became
`apps/Legacy/apps/Legacy/Directory.Packages.props`.

On projects already under central management, the writer looked only for a
file named `Directory.Packages.props` above each project. After `--cpm` had
converted to a named file (`<Solution>.Packages.props`, opted into with an
`<Import>` in each project), the next consolidation found the root
`Directory.Packages.props` instead, the unrelated file the name was chosen to
avoid, and wrote the versions there.

The OFR1301 check that picks the named file looked only at existing
`Directory.Packages.props` files, so creating a new one at the root of a
repository with unrelated projects reached all of them.

## Decision

- A `deps.cpm.file` containing a `/` (after normalizing `\`) is a
  repository-relative path and `scope` does not apply; a bare name goes in the
  solution's folder or the root, as before (`CpmConfig.PathFor`).
- A default-named file that does not exist yet is checked for the projects
  outside the solution below its folder; any of them is OFR1301 and the file is
  named `<Solution>.Packages.props` in the same folder.
- Opt-in is needed whenever the SDK would not find the file by itself: another
  name, or a folder that is not above every project.
- On existing central management, a project's file is the props file with
  `PackageVersion` items it imports (imports relative to
  `$(MSBuildThisFileDirectory)`, `$(MSBuildProjectDirectory)`, or the project's
  folder; others are skipped), else the recorded `DirectoryPackagesPropsPath`,
  else the nearest `Directory.Packages.props`.

## Alternatives considered

- Always relative to the solution's folder: the spec example would not mean
  what it says, and answers typed from the repository root would double up.
- Preferring the configured file on existing central management: a repository
  set up before Offramp may use another file, and writing versions where the
  projects do not look would change nothing, silently.

## Consequences

- A bare name keeps its old meaning, so existing configurations are unchanged.
- A conversion followed by another consolidation is covered by a test that
  applies the first change set and rescans.
