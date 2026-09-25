# 0006. Define doctor's result, check identifiers, and environment codes

- Status: accepted
- Date: 2026-09-25
- Spec section: `docs/spec/commands/workspace.md#doctor`

## Context

The spec lists what `doctor` checks and that it exits 1 when a check fails, but
not its JSON result, the diagnostic codes for environment problems (the code
table has none for a missing SDK or git), or how the missing workspace model
(`OFR0001`, an error elsewhere) is reported before the first `scan`.

## Decision

- Result: `checks` (ordered, each `{ id, title, status: pass|warn|fail|skip,
  message, remedy, codes }`), `environment` (installed SDKs, selected SDK,
  global.json, git, OS runtime identifier, target), and `summary` counts.
  Schema: `schemas/v1/doctor.json`.
- Stable check ids, in order: `dotnet-sdk`, `global-json`, `target`,
  `reference-assemblies`, `git`, `git-repository`, `config`, `workspace`. Later
  milestones append (`windows-only-build-steps`, `cpm`, `llm`), never renumber.
- New codes in the workspace/environment range: `OFR0010` no SDK, `OFR0011`
  global.json SDK missing, `OFR0012` SDK cannot target `--target`, `OFR0013`
  reference assemblies unresolvable, `OFR0014` no git, `OFR0015` not a
  repository, `OFR0016` no `offramp.yml` (info). Configuration: `OFR0053` invalid
  value, `OFR0054` YAML syntax, `OFR0055` config file missing, `OFR0056` invalid
  environment value; `OFR0030` init refused to overwrite; `OFR0020` several
  solutions; `OFR0099` internal error. A feed that cannot be queried reuses
  `OFR1006`.
- A check's status and its diagnostic severity agree (fail = error, warn =
  warning), so "exit 1 when a check failed" is the ordinary `--fail-on error`
  rule. Doctor reports a missing workspace model as `OFR0001` at warning
  severity: before the first scan it is expected, not broken.
- Reference assemblies are "resolvable" when `Microsoft.NETFramework.ReferenceAssemblies.net48`
  is in the global packages folder, the .NET Framework 4.8 targeting pack is
  installed (Windows), or an enabled feed from the repository's `nuget.config`
  lists it.

## Alternatives considered

- Per-check exit logic independent of diagnostics: rejected; `--fail-on` and
  `rules:` overrides would then not apply to doctor.

## Consequences

- A severity override in `offramp.yml` can make doctor pass a failing check; the
  diagnostic is marked `overridden` so that stays visible.
