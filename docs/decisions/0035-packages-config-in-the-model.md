# 0035. Record packages.config packages in the workspace model

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/02-workspace-model.md`, `docs/spec/commands/deps.md`

## Context

The model recorded only that a `packages.config` exists (`packagesConfig: true`), not
what it lists. Every package analysis read `PackageReference` items, so on a legacy
solution (DotNetNuke 9.13: 64 of 71 projects on `packages.config`) `deps audit` saw 23
packages out of 77, test projects referencing NUnit through `packages.config` were
classified as libraries, and `redirects sync` believed the binding redirects of those
packages were stale. The specification's answer, `csproj modernize` first, does not
reach ASP.NET web application projects, which it refuses to convert (OFR4304).

## Decision

Each project records the packages its `packages.config` lists in
`packagesConfigPackages` (`id`, `version` as written, `targetFramework`,
`developmentDependency`), sorted by id and version, and omitted when there is no
`packages.config`. The solution-wide `packages` index includes them, with versions
normalized the way NuGet does (`1.0.0.0` is `1.0.0`), so `deps audit`, the guide, and
`deps consolidate`'s `current` see every package in use. Kind detection counts a test
framework or Topshelf listed there. `deps consolidate` still writes `PackageReference`
projects only; a `packages.config` project on another version than the selected one is
reported with `OFR1204`.

## Alternatives considered

- `packageReferences` entries with a flag for their origin: every command that edits
  `PackageReference` versions (consolidate, codemods, resolve-dlls, modernize) would have
  had to learn to skip them, and one that did not would write a `PackageReference` into a
  `packages.config` project. A separate list makes reading them the opt-in.
- Recording them under `resolved` as if restored: `packages.config` lists direct and
  transitive packages alike with no dependency graph, and consumers of `resolved` expect
  one.
- Leaving them out and requiring `csproj modernize` first: blocks web projects forever.

## Consequences

The index can hold a package only `packages.config` projects use; consolidation lists it
and changes nothing, with `OFR1204`. `packages.config` lists transitive packages too, so
`deps audit` audits them as if direct, which is what a legacy project effectively has.
Models written before this change lack the list; `scan` fills it.
