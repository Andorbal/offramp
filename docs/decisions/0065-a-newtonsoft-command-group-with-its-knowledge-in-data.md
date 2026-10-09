# 0065. Give the Newtonsoft.Json upgrade its own command group, with its knowledge in data

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md`

## Context

Old Newtonsoft.Json is the most common reason a legacy solution has several
versions of one package and the binding redirects that hide it. Redirects work at
run time only, so `move` fails with CS1705 when code that uses an older version
starts to call code built against a newer one, and `deps consolidate` can raise a
version without anyone knowing what the serializer will now do differently.
Moving to 13.x needs three things Offramp did not have: an inventory of the code
that serializes, tests that capture what it does today, and an upgrade that
handles `packages.config` and `HintPath` references (which `deps consolidate`
reports but does not write, OFR1204).

The question was whether to build this as a general "characterize, upgrade,
compare" engine with Newtonsoft.Json as its first package, or as commands for
this one package.

## Decision

A command group, `offramp newtonsoft scan|tests|upgrade`, specific to
Newtonsoft.Json. What it knows about the package lives in data files embedded
like the audit rule packs: `rules/newtonsoft-apis.yml` (the serialize,
deserialize, and DOM entry points by documentation ID), `rules/newtonsoft-breaking.yml`
(behavior changes by release, with the detector each uses), and
`rules/newtonsoft-hermetic.yml` (APIs that make a method unsuitable as a probe's
entry point). Severities are overridden through `rules:` as for audits. The
pieces that are not about Newtonsoft (the method-level call graph, the test
runner, the `packages.config` and `HintPath` writers) are written as ordinary
library code that a later command can reuse.

## Alternatives considered

- **A general engine with a pluggable catalog** (`offramp upgrade characterize
  --package ID`). Every interesting part is package-specific: which calls are
  sites, what a probe records, which behavior changes matter. A general engine
  would have one real client and an abstraction shaped by guesses about the
  next one. If a second package earns it, the reusable parts are already
  separate.
- **Extending `audit serialization`.** It answers "what will break on the new
  runtime", not "what changes between two versions of a package", and it does
  not write tests or edit references.

## Consequences

- Users get commands named for the job, and the guide can offer them only when a
  project references Newtonsoft.Json below 13.
- A second package with the same need would get its own group, reusing the call
  graph, test runner, and reference writers; at that point the shared shape is
  known instead of guessed.
- The rule files are the part most likely to need care over time; each rule
  carries the release that introduced its behavior and a test that proves it
  (ADR 0066).
