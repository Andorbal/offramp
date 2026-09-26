# 0015. Test package commands against a recorded feed

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/04-testing-and-fixtures.md`, roadmap M2 acceptance

## Context

`deps audit`'s answers must be tested "against a recorded feed (tests use a
local file feed so they do not depend on nuget.org)". Real packages are large,
their newest versions change, and a folder feed cannot express listing state or
deprecation. The `versions` fixture must also build so `scan` can model it.

## Decision

- `eng/record-feed.cs` records chosen versions of real packages from nuget.org
  into `tests/fixtures/versions/feed.json`: file paths, dependency groups,
  listing state, deprecation, and for every assembly its identity (name,
  version, culture, public key), assembly references, and `SupportedOSPlatform`
  attributes; `.props`/`.targets` content verbatim. Seeds live beside it in
  `feed.seeds.json`, including hand-written `Contoso.*` packages for cases real
  packages do not cover.
- Tests materialize the recording (`Offramp.Fixtures.Feeds`) as byte-identical
  `.nupkg` files whose assemblies are metadata-only stubs with the recorded
  identity and references. The acceptance test runs the real NuGet.Protocol
  client against that folder feed; a recorded-feed double covers deprecation and
  listing, which folder feeds cannot express.
- The fixture itself restores real packages from nuget.org like the other
  fixtures, and the `Contoso.*` ones from the committed `synthetic-feed/`
  (package source mapping), which a test keeps equal to the recording.

## Alternatives considered

- Committing real nupkgs: megabytes of binaries, and license review for each.
- A local HTTP feed serving the recording: exercises more of NuGet.Protocol, but
  a server in every test run for one field (deprecation) the double covers.

## Consequences

- Re-recording is a deliberate step with network access; the recording's date
  is in the file. The answers the tests assert are the recorded feed's, which is
  the point: they never drift with nuget.org.
