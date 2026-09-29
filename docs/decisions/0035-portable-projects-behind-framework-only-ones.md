# 0035. Count a portable project that references a framework-only one as blocked

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/workspace.md` (`plan`), `docs/spec/commands/graph.md`

## Context

Readiness called every standard, modern, and dual project `done`, on the assumption that a
project can only reference what its targets can load. NuGet enforces that (NU1201) for
SDK-style and `PackageReference` projects, but skips a referenced legacy project, which
has no restore metadata to compare. DotNetNuke 9.13 has three such projects: two
`netstandard2.0` projects that reference `net472` legacy projects, and a
`netstandard2.0;net472` project that references three of them unconditionally. `plan`
put them in wave 0 as `done` while listing framework-only blockers for them, which broke
its own invariant that every project comes after the framework-only projects it needs.
The code builds and fails at run time on the portable target.

The graph has one set of edges per project, the union over its targets, so a dual
project that references a framework-only project for its `net4x` target alone looked the
same as one that references it for every target.

## Decision

- A project is `blocked` when a framework-only project is reachable from it through the
  references its portable targets use; with none, it is `ready` (framework-only) or
  `done` (standard, modern, dual). A blocked portable project takes its wave from its
  blockers like a framework-only one.
- The model records, for dual projects only, the project references of their standard
  and modern targets (`modernProjectReferences`). Readiness follows only those from a
  dual project, so a `net4x`-only reference blocks neither the dual project nor the
  projects that reference it. `HintPath` edges are not attributed to targets and always
  count. A model from a compiler log alone has no per-target references and counts
  every edge.
- `scan` reports each reference from a portable target to a framework-only project as
  `OFR0121` (warning), naming both projects and their targets.

## Alternatives considered

- Keeping `done` and adding a flag: `--frontier`, waves, `report`'s frontier, and the
  guide would all have had to learn the flag, and a check that forgot it would repeat
  the bug.
- Per-target edges in the graph for every project: a larger change to a contract many
  commands read, for a distinction that only dual projects have.
- Ignoring `HintPath` edges from dual projects, which may be conditioned on `net4x`:
  counting them can only make a project blocked that is not, which is the safe error.

## Consequences

`plan` can list standard and modern projects in waves after 0, and `counts.done` drops
by the number of portable projects that were never portable. `report`'s portable
percentage still counts lines by target framework, as its series always has; its
frontier and application numbers follow the new readiness.
