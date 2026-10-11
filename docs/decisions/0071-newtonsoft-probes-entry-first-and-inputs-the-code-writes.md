# 0071. Probe through entry points first, and feed reading probes what the code writes

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-tests`

## Context

`newtonsoft tests` turns each site into characterization probes inside an
existing test project. The spec left four choices open:

- what a probe calls;
- what a reading probe is given to read;
- how the probe code reaches the production types;
- how much of the generated code is allowed to fail.

## Decision

- **Entry probes first, type probes for the rest.** A site with an entry point
  is probed through it, so the probe runs the application's own settings, payload
  assembly, and post-processing. Only a site without one gets a probe that calls
  Newtonsoft.Json with the site's settings written out. That needs the test
  project to compile against the production project's Newtonsoft.Json, which
  entry probes never do.
- **Reading probes read what the code writes.** A deserialize site is given:
  - every real payload named after its type;
  - what a writing probe of the same payload type and the same settings records,
    because that JSON is what the application produces. A different settings
    object would give a shape the reader never sees.
  - Failing both, *input probes* write a generated sample with the reading
    site's own settings.
  
  Pairing goes by settings identity (the same static member, the same inline
  creation, or none), not by comparing settings values.
- **Everything is `global::`-qualified C# 6.** Generated code sits in a test
  project with unknown usings, nullable settings, and language version. It names
  every type in full and disables the obsolete and nullable warnings, so
  warnings-as-errors projects build it.
- **The compiler decides what is written.** The files are compiled into every
  recorded compilation of the test project. A probe with an error is dropped,
  together with the probes that read it, and the loop repeats until the rest
  compiles. A probe whose code is wrong costs one probe, not the run, and nothing
  that fails to compile reaches the repository.

DOM sites (`JObject.Parse`) are uncovered in v1. They read JSON of no known type,
so no sample can be matched to them by name.

## Alternatives considered

- **Type probes everywhere.** These are simpler and need no call graph, but they
  test Newtonsoft.Json with settings Offramp reconstructed, not the application,
  and they need the test project to reference Newtonsoft.Json.
- **Generated JSON as reading input.** It is easy to produce, but the shape comes
  from Offramp's guess, not from the code, so a reading change that only shows on
  real shapes would be missed.
- **Generate even when it does not compile, and let the user fix it.** The first
  build of the test project would fail, on a branch that only meant to add tests.

## Consequences

- A solution's coverage depends on how hermetic its serialization code is. The
  result's `coverage` and `uncovered` say where it falls short and why.
- Test projects that never reference Newtonsoft.Json still get entry probes,
  which is most sites in practice.
- On the `newtonsoft-sdk` fixture, probes recorded on 7.0.1 and verified on
  13.0.4 differ exactly where `scan` predicted. These were enum dictionary keys,
  the camel-cased `LEGACY CODE`, and the `ISerializable` `Money` type, while the
  project `scan` found unaffected stayed the same. This was a manual run that
  `upgrade`'s tests will automate.
