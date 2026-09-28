# 0032. Explain each `init` question and check typed answers before writing them

- Status: accepted
- Date: 2026-09-28
- Spec section: `docs/spec/03-configuration.md#init`

## Context

The interview asked "How should Offramp verify changes?" with the bare choices
`build`, `command`, `none`; choosing `command` never asked for the command, so
the file said `mode: command` with nothing to run. "Central package management
file?" took any text, and the writer then read it relative to the solution's
folder. "Add a package version pin?" led to "Package id?" with no hint of what
a pin is or what an id looks like, and any text was accepted.

## Decision

- A dim line before each question says what the setting does and what most
  people answer. Verify choices carry a description; `command` asks for
  `verify.command`.
- The CPM question says that nothing is written until `deps consolidate --cpm`,
  warns that a `Directory.Packages.props` applies to every project below it,
  and suggests the path consolidation would use for the chosen solution. The
  answer is a path from the repository root (absolute paths inside it are
  accepted); a folder gets `Directory.Packages.props`; a bare name is written
  with `scope: repo`. A root `Directory.Packages.props` found by detection is
  written with `scope: repo` too, so a solution in a folder does not move it.
- Pins check the id with NuGet's package id rules, the version with
  `NuGetVersion` (kept as typed), and the project against the file system.
- The checks live in `Offramp.Workspace.Init.InitAnswers`, so they are tested
  without a terminal; one CLI test drives the Spectre interview with keys.
- `init`'s result gains `values.verifyCommand` and `values.cpmScope`.

## Alternatives considered

- Dropping pins from the interview: the spec lists them, and teams with a pin
  policy want it recorded at setup. The question now defaults to No and says so.
- Offering the scanned packages as a list: `init` usually runs before the
  first scan, so there is rarely a model to list from.

## Consequences

- `offramp.yml` comments for `verify`, `deps.pins`, and `deps.cpm` changed.
