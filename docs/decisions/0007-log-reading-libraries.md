# 0007. Read logs with MSBuild.StructuredLogger and pin Roslyn 5.9

- Status: accepted
- Date: 2026-09-26
- Spec section: `CLAUDE.md#toolchain`, `docs/spec/02-workspace-model.md#ingest`

## Context

`CLAUDE.md` names `Microsoft.Build.Logging.StructuredLogger` for binary logs
and `Basic.CompilerLog.Util` for compiler logs, and pins no Roslyn version.
`Microsoft.Build.Logging.StructuredLogger` is the old package id (1.1.x, no
longer updated); the reader now ships as `MSBuild.StructuredLogger`, and
`Basic.CompilerLog.Util` 0.9.63 depends on `MSBuild.StructuredLogger` 2.3.246.
Referencing both ids puts two copies of the same namespace in one process.

`Basic.CompilerLog.Util` accepts Roslyn 4.8 or newer. With 4.8, compilations
rebuilt from a .NET 10 build report spurious errors against the net10.0
reference assemblies, and a C# and Visual Basic Roslyn of different versions
fail to load together (`TypeLoadException`).

## Decision

Use `MSBuild.StructuredLogger` (the package `Basic.CompilerLog.Util` already
brings) and pin every `Microsoft.CodeAnalysis.*` package, C# and Visual Basic
alike, to one version (5.9.0) in `Directory.Packages.props`. The Roslyn version
moves forward deliberately, with the SDK in `global.json`, never below the
compiler that produced the logs Offramp reads.

## Alternatives considered

- `Microsoft.Build.Logging.StructuredLogger` 1.1.x: unmaintained and
  conflicts with the reader `Basic.CompilerLog.Util` loads.
- Floating Roslyn at `Basic.CompilerLog.Util`'s minimum: produced wrong
  diagnostics on the fixtures.

## Consequences

- A compiler log from a newer SDK than Offramp's Roslyn may contain language
  features Offramp cannot bind; bumping Roslyn is part of supporting a new SDK.
- `CLAUDE.md#toolchain` names the new package id.
