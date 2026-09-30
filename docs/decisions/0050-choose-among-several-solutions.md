# 0050. Choose among several solutions by what they build

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/03-configuration.md#init`, `docs/spec/commands/workspace.md#scan`

## Context

With no `solution:` configured, `init` and `scan` chose the only solution, or the only one at the
repository root, and otherwise left `solution: null` with `OFR0020`. All three field tests of
2026-09 hit it, although the main solution was clear each time:

- SmartStoreNET: `src/SmartStoreNET.sln` (25 projects) contains every project of
  `src/SmartStoreNET.Minimal.sln` (10).
- Open Live Writer: `src/managed/writer.sln` (29 projects, the one `build.ps1` builds) and three
  utility solutions of 1 to 4 projects.
- NHibernate: `src/NHibernate.sln` (5 projects) and `src/NHibernate.Everything.sln` (6). Everything
  is not a superset (it lacks `NHibernate.TestDatabaseSetup` and adds the `HbmXsd` tool and
  `NHibernate.Example.Web`), and `Example.Web` is a folder-based Web Site project: .NET's MSBuild
  cannot build one (MSB4249), and the failure stops the whole solution build, so a scan of
  Everything loads nothing (field test P1 #13).

## Decision

Solution filters are never chosen. The only solution, or the only one at the root, is chosen as
before. Otherwise every solution is read, and:

1. Solutions that cannot be read are set aside, and so are solutions with a Web Site project when
   at least one solution has none.
2. If one solution is left, it is chosen.
3. Else a solution that contains every project of every other one left is chosen.
4. Else the solution with strictly the most projects is chosen.
5. Else (equal counts, or identical project sets) nothing is chosen, and `OFR0020` gives each
   solution's project count.

A choice made by rules 1 to 4 is reported as `OFR0023` (info) with the reason, in `init` and in
`scan`, so the user sees it and can override it with `--solution` or `solution:`. `doctor` uses the
same choice silently.

## Alternatives considered

- Keep `OFR0020` whenever there are several solutions. Every field test needed a manual step for
  a choice the repository makes obvious.
- Pick the superset first, Web Site projects or not. For NHibernate it would not pick Everything
  anyway (it is no superset), but it would pick the solution with the most projects, and the first
  scan would load nothing.
- Read the build scripts (`build.ps1`, NAnt, psake) for the solution they build. Not deterministic
  across the many script languages, and a script may build several.
- Prefer names (`*.Minimal.sln`, `*.Everything.sln`). Names are conventions of a few repositories;
  project sets are facts.

## Consequences

`init --defaults` and `scan` without configuration work on all three field-test repositories. A
repository whose main solution is smaller than a combined one still needs `--solution`; the info
diagnostic makes such a choice visible. Reading every solution costs milliseconds.
