- Status: accepted
- Date: 2026-10-18
- Spec section: `docs/spec/commands/workspace.md#verify`; `docs/spec/03-configuration.md`

## Context

Some legacy projects require full MSBuild.exe, rather than the .NET SDK's MSBuild, to build. Scan can already use MSBuild.exe, while verification always used `dotnet build`, preventing verification and probe recording on such projects.

## Decision

Add `verify.builder` with `inherit` as its default. `inherit` follows `scan.builder`, while explicit `dotnet` or `msbuild` choices override it. MSBuild verification shares scan's locator and configured `scan.msbuildPath`. Its build writes the same binlog consumed by verification and restores packages.config projects when restore is enabled. Probe execution remains `dotnet test` and is not changed by the verification builder.

## Alternatives considered

- Always use MSBuild when available: rejected because this would silently change existing default behavior for repositories using dotnet verification.
- Add verification-specific locator and path settings or CLI flags: rejected because scan already owns a locator and path that are appropriate for this use.

## Consequences

Repositories using `scan.builder: msbuild` automatically use that same builder for verification unless `verify.builder` overrides it. On machines without a usable MSBuild installation, verification reports the existing `OFR0017` diagnostic. Test execution remains unchanged.
