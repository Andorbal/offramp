# 0005. Pin xunit v3 3.2 and Verify 32 for the test stack

- Status: accepted
- Date: 2026-09-25
- Spec section: `CLAUDE.md` (toolchain, testing standards), `docs/spec/04-testing-and-fixtures.md`

## Context

CLAUDE.md names xunit and `Verify.Xunit`. In September 2026 the current
releases are xunit v3 4.x, which runs only on Microsoft.Testing.Platform (the
.NET 10 SDK's VSTest-mode `dotnet test` refuses it), and Verify 33, which added
a build-time sponsorship gate (`SponsorCheck`) that fails the build unless the
consuming project declares a sponsorship or exemption.

## Decision

- xunit v3 **3.2.x** with `xunit.runner.visualstudio` **3.1.x** and
  `Microsoft.NET.Test.Sdk`, so `dotnet test --filter "Category!=Corpus"`,
  `--logger trx`, and `--collect:"XPlat Code Coverage"` in CI work as documented.
- `Verify.XunitV3` **32.0.1**, the last release before the sponsorship gate
  (Verify.Xunit is the xunit v2 flavor; v3 is the maintained line).
- Snapshots live in each test project's `Snapshots/` directory. Tests scrub the
  repository root, versions, timestamps, durations, and hashes themselves
  (`Offramp.Fixtures.Scrub`), and Verify's own date and GUID scrubbing is off so
  nothing else changes silently. `eng/accept-snapshots.sh` accepts pending
  snapshots.
- Every diagnostic test carries `[ProducesDiagnostic("OFR####")]`; a meta-test
  fails when a catalogued code has none.

## Alternatives considered

- Moving to Microsoft.Testing.Platform now: viable, but it changes every test
  command in CI, CLAUDE.md, and the release workflow for no functional gain yet.
  Revisit when a dependency forces it.
- Declaring a Verify sponsorship exemption: that is a licensing statement for the
  maintainers to make, not an implementation choice. If they make it, upgrading
  is a one-line change in `Directory.Packages.props` plus the property.
- Hand-rolled snapshot files: rejected while a maintained library works.

## Consequences

- Upgrading past these versions is a deliberate change with its own PR.
