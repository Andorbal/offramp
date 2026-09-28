# 0030. A project is not ready while it depends on something the model cannot see

- Status: accepted
- Date: 2026-09-28
- Spec section: `docs/spec/02-workspace-model.md`, `docs/spec/commands/workspace.md#plan`, `#doctor`, `docs/spec/commands/move.md#move-tests`

## Context

Dogfooding preparation turned up three places where Offramp gave a confident,
wrong answer on codebases of the kind it is for:

- `GraphBuilder` kept only project references whose target was in the model, so
  a reference to a C++/CLI `.vcxproj`, a `.sqlproj`, a project that failed to
  evaluate, or a project outside the scanned slice vanished. `plan --frontier`
  then called the dependent `ready`, `report` counted it as `next`, and the
  guide offered to port it.
- `move tests` recognized MSTest by the `Microsoft.VisualStudio.TestPlatform.TestFramework`
  assembly. Legacy MSTest projects reference `Microsoft.VisualStudio.QualityTools.UnitTestFramework`
  from the GAC, so the move found no tests, and `deps gac` called the reference
  `unknown`.
- Nothing checked the .NET Framework version. .NET Standard 2.0 libraries are
  consumed cleanly only from 4.7.2 on, and the guide's port step proposed
  `net45;net10.0` without a word.

The spec did not say what a reference outside the model means for readiness,
how a test framework referenced as an assembly is treated, or whether there is
a target framework floor.

## Decision

- **Unresolved references block; they are never dropped.** `scan` records every
  declared `ProjectReference` the model cannot follow on the referencing project
  (`unresolvedReferences: [{ path, reason }]`) and reports `OFR0105`. Readiness
  treats them like framework-only blockers: a framework-only project is `ready`
  only when it has none, on itself or on any framework-only project it depends
  on. `plan` lists them per entry, `report` keeps such an application out of
  `ready` and `next`, and the guide's port step does not offer the project.
  Two exceptions keep the rule honest: a reference kept only for build order
  (`ReferenceOutputAssembly="false"`) is not a dependency, and a reference on a
  project that is already portable blocks nothing, since that project evidently
  builds for the target with it. Waves still count framework-only blockers
  alone; a project blocked only by an unresolved reference keeps its wave and
  its readiness says why.
- **MSTest v1 is MSTest.** The QualityTools assembly marks a file as a test in
  the classifier, a project as `test` in kind detection, and maps to
  `MSTest.TestFramework` in `rules/framework-assemblies.yml`. A test project
  `move tests --create` writes gets the package instead of a copy of the
  assembly reference.
- **The floor is 4.7.2, and Offramp only points at it.** `scan` reports
  `OFR0106` for a .NET Framework target below `net472`; `doctor` gathers them
  under `framework-floor`; `plan` shows every project's targets. Raising a
  target is left to the developers (or to `csproj modernize --tfm`), because it
  touches `app.config` and deployment in ways a migration tool should not
  decide.

## Alternatives considered

- Adding unresolved references to `blockers` as paths. Rejected: blockers are
  projects with waves; an external path has none, and the two lists answer
  different questions ("port these first" versus "decide what to do about
  this").
- Making the referenced project a node of the graph. Rejected: it has no
  framework class, kind, or compile items, and every consumer of the graph
  would need a special case.
- Treating a reference outside the model as portable when the referenced file
  is a `.csproj`. Rejected: a project outside the slice is unknown, and
  guessing is what this decision removes.
- A guide step that raises the target automatically. Rejected for now: the
  right target and the config changes that go with it are a team decision.

## Consequences

- Repositories with C++/CLI or database projects in the middle of the graph now
  show fewer `ready` projects, and the reason. `graph` nodes flip to `blocked`
  the same way; the graph does not yet draw the external project.
- Models written before `unresolvedReferences` existed still load: the
  property's setter treats null as empty, since the source-generated
  deserializer does not run field initializers for absent properties.
- A `ReferenceOutputAssembly="false"` reference to an unsupported project still
  fails the build on macOS and Linux; that is `OFR0101`'s concern, not
  readiness.
- `plan`'s table gained a `Targets` column and its JSON `targetFrameworks` and
  `unresolvedReferences`; `doctor` gained a check after `cpm`.
