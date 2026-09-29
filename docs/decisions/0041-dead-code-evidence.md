# 0041. Treat packed libraries, and libraries no application uses, as shipped

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/audit.md#audit-dead-code-ofr3400-3499`, `docs/spec/03-configuration.md`

## Context

ADR 0022 rates a public symbol `medium` when its project is packable (`IsPackable`) or listed
in `deadCode.externalConsumers`, and `high` otherwise. Legacy projects never set `IsPackable`,
so on the field tests every legacy library's public API looked like the solution's private
code:

- NHibernate 4.1 is packed by NAnt from `src/NHibernate/NHibernate.nuspec.template`, and no
  application in its solution references it. `audit dead-code` rated 250 of its public symbols
  `high` (SQL dialects chosen by name in configuration, `Configuration.AddXmlReader`, ...).
- Open Live Writer ships its plugin SDK, `OpenLiveWriter.Api`, through `OpenLiveWriter.SDK.nuspec`
  at the repository root, which packs the DLL from the build output.

The spec did not say what "packed" means for a project without `IsPackable`, nor where a
`.nuspec` may be.

## Decision

A project is **shipped** when the first of these holds, and that rule is the evidence:

1. it is listed in `deadCode.externalConsumers` (project name, assembly name, or path);
2. it is packable (`IsPackable=true` as evaluated);
3. a `.nuspec` anywhere in the repository packs its DLL: a `<file src>` whose file name is
   `<AssemblyName>.dll`, written without wildcards; or, for a library, a `.nuspec` or
   `.nuspec.template` sits in the project's folder (`bin`, `obj`, `packages`, `node_modules`,
   and dot folders are not searched);
4. it is a library that no application in the solution depends on, directly or through other
   projects. A library is a `library` project, or a `test` project whose output is a library (a
   production library that carries its tests is a `test` project by kind). An application is a
   `web`, `winforms`, `wpf`, `service`, or `console` project.

`audit dead-code` rates the public symbols of a shipped project `medium` with that evidence
("public in an assembly packed by src/NHibernate/NHibernate.nuspec.template: other repositories
may use it").

## Alternatives considered

- **Only the rules the report proposed, with "no non-test project references it" for rule 4.**
  NHibernate is referenced by `NHibernate.DomainModel`, a library its tests use, so the rule would
  not fire for the codebase that motivated it. "No application depends on it" is the question
  that matters: a library the solution's applications do not need is there for someone else.
- **A `.nuspec` beside any project.** A `.nuspec` beside a web application is usually OctoPack's
  deployment package, which nobody compiles against, so the folder rule is for libraries only;
  a `.nuspec` that packs a project's DLL by name ships it whatever its kind.
- **Honoring wildcards in `<file src>`.** `bin\**\*.dll` would ship every project whose output
  lands there; literal names are rare to get wrong.

## Consequences

- Repositories whose libraries are consumed elsewhere no longer get their public API called
  removable; the high-confidence total is what the solution itself can drop. On NHibernate 4.1
  that moves 250 public symbols from `high` to `medium` without configuration.
- A solution made only of libraries and tests has no public `high` candidates at all; its
  internal and private dead code is still `high`.
- `deadCode.externalConsumers` remains the way to name consumers Offramp cannot see, and is
  documented in `docs/spec/03-configuration.md`.
