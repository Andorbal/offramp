# 0066. Prove each Newtonsoft.Json change against both versions instead of transcribing release notes

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-scan`

## Context

`newtonsoft scan` warns about code whose behavior changes between the version in
use and the target. The obvious source is the release notes, but their lines are
short and easy to misread. While designing this, a test written from the 11.0.1
note "changed camel casing logic for all caps words" expected `URLValue` to camel-case
differently under 7.0.1 and 13.0.1; both produce `urlValue`. A warning built on a
misread note is noise at best, and at worst teaches users to ignore the rule pack.

Removed and obsolete APIs have a better source than notes: the compiler.

## Decision

**API changes come from a trial compilation**: the project's recorded
compilation with the target's `Newtonsoft.Json.dll` substituted. New errors are
OFR3701, new obsolete warnings OFR3702. The rule file only adds advice about the
replacement. Nothing is listed by hand that the compiler can find.

**Behavior rules ship only with a differential test.** Each rule in
`rules/newtonsoft-breaking.yml` names its `since` release and an example (a type,
settings, and the operation). A test loads Newtonsoft.Json at the last release
before `since` and at the target into separate `AssemblyLoadContext`s, runs the
example under each, and fails unless the outputs differ in the way the rule
describes. The versions come from the NuGet global packages folder, restored by
the test project as `PackageDownload`s. A rule whose example does not reproduce
is not shipped. Its code stays reserved, and the release-note line is reported
through the catch-all OFR3719 so the list of applicable notes stays complete.

## Alternatives considered

- **Transcribe the notes and trust them.** Fast to write, and it was wrong at
  least once before any code existed.
- **Detect API changes by listing removed members.** That duplicates the
  compiler and misses overrides and members reached through a derived type.
  It also goes stale if a later release changes the list.

## Consequences

- Every behavior warning comes with evidence that can be rerun when the target
  moves (13.0.5, or 14.x later).
- The test suite downloads a few extra Newtonsoft.Json versions. They are small,
  and the corpus and fixture tests already need nuget.org.
- Loading two versions of one assembly needs a collectible load context per
  version. This works on modern .NET only, which is where Offramp's tests run.
