# 0077. `IsTestProject` is evidence, not a verdict

- Status: accepted
- Date: 2026-10-16
- Spec section: `docs/spec/02-workspace-model.md` (project kind detection)
- Amends: ADR 0074

## Context

ADR 0074 made a test framework package or assembly demotable when a deployed application uses
its project, but kept `IsTestProject=true` unconditionally. In SDK projects this property is often
inferred from `Microsoft.NET.Test.Sdk`, even when a web, service, Windows Forms, or WPF assembly
contains production code and tests and is deployed as part of an application. Treating that inferred
property as a verdict excluded production code from entry-point search and other analyses.

A separate detection gap classified `OutputType=WinExe` without desktop evidence as unknown, even
though it is an executable output, and the workspace did not identify production projects carrying
test framework references.

## Decision

- The legacy test ProjectTypeGuid remains an explicit test declaration and takes precedence.
- `IsTestProject=true` is demotable evidence. Detect the kind without that property and without
  test-framework evidence first. If it yields `web`, `service`, `winforms`, or `wpf`, use that kind
  and state that `IsTestProject=true` was ignored in the evidence. Otherwise retain `test`.
- When an application uses such an `IsTestProject=true`-only project, directly or transitively,
  reclassify it without the property and clear `ProjectInfo.IsTestProject`. As in ADR 0074, a
  library or console reference does not establish deployment. A ProjectTypeGuid or an explicit
  `offramp.yml` test-kind override remains a test and produces `OFR0107` when an application uses it.
- `WinExe` is `console` absent stronger application evidence, and is recognized as executable for
  the existing service evidence.
- `hostsTests` identifies projects of a non-test kind with known test-framework package,
  `packages.config`, or test assembly evidence. It is false for test-kind projects.

## Consequences

Projects made production kinds by this rule are included by readers that exclude test projects.
`OFR0107` is narrower: it indicates an explicit ProjectTypeGuid or `offramp.yml` test declaration
used by a deployed application. The workspace JSON adds required boolean `hostsTests`; consumers
of schema v1 should read it when they need to distinguish production assemblies that also carry tests.
A changed kind or `isTestProject` value requires a fresh scan.
