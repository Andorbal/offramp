# 0067. Probes record to files Offramp compares, and say which Newtonsoft.Json ran

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-tests`

## Context

`newtonsoft tests` puts characterization tests into the user's existing test
projects. Those projects use xunit, NUnit, or MSTest (v1 in many legacy
solutions), are often legacy non-SDK projects at C# 7.3 or lower, and may use
`packages.config`. Each test has to capture what the current Newtonsoft.Json
does and compare it later. Three questions were open: where the expected output
lives, who decides pass or fail, and how to be sure that "the current version"
is the one that actually ran.

The last question came from a spike. A `net48;net10.0` library referencing
Newtonsoft.Json 7.0.1 and an xunit project testing it on `net10.0` showed:

- With `Microsoft.NET.Test.Sdk` 17.x, the test project resolved Newtonsoft.Json
  **13.0.1**. `Microsoft.TestPlatform.TestHost` depends on it for .NET Core
  targets, NuGet picked the higher version, and the "baseline" was 13's output
  with nothing to say so. Test SDK 18.x dropped that dependency. The .NET
  Framework group never had it.
- With 7.0.1 actually resolved, .NET 10 failed at the first serialization: 7.0.1's
  `lib/net45` assembly needs `System.Security.Permissions`, which modern .NET does
  not ship. Adding that package made 7.0.1 run (`7.0.0.0`) on .NET 10.

## Decision

- **Expected output is a file per probe**, `Baseline/<netfx|core>/<probe>.txt`,
  not a string in test source. Escaped JSON in C# 7.3 strings cannot be reviewed,
  and rewriting test source after each recording would make the tests Offramp's
  output instead of code the user can read. Baselines are keyed by runtime
  family because .NET Framework and .NET format some doubles differently.
- **Probes write results; Offramp compares them.** In both modes a probe writes
  its result to `OFFRAMP_NEWTONSOFT_RESULTS`, and Offramp compares the files with
  the baselines. The test framework's pass or fail, and its result format
  (TRX or anything else), is not the verdict. That makes three frameworks one
  code path, and keeps the generated test bodies free of assertion APIs. When a
  person runs a probe outside Offramp, it compares in-process and throws on a
  difference, so it still fails in their runner.
- **Every result records the Newtonsoft.Json assembly version that ran**, read by
  the probe runtime through reflection. Offramp refuses to record a baseline
  whose version is not the production project's version in use (OFR3745). The
  spike shows that what the project files say is not what runs.
- **.NET Framework test targets run only on Windows.** No Mono. A test project
  that also targets modern .NET runs that target elsewhere, recorded as `core`.
  Offramp's own fixtures follow the spike: Test SDK 18.x, and
  `System.Security.Permissions` on the modern target so 7.0.1 loads there and
  the full record and verify path is tested on every OS.

## Alternatives considered

- **Verify (the snapshot library) in the user's test project.** It is a new
  package and needs a modern C# compiler. It also brings its own Newtonsoft-free
  serializer, but adds a dependency to every test project of a legacy solution.
- **Parse TRX.** Every framework and adapter writes it slightly differently,
  legacy MSTest v1 adapters least predictably. It also only says whether a test
  passed, not what it saw.
- **Trust the project files for the version.** The spike disproves that.

## Consequences

- Recording on a solution whose test projects target only .NET Framework needs a
  Windows machine (or CI agent). OFR3739 says so and gives the command.
- A modern-.NET test project that loads an old Newtonsoft.Json may need
  `System.Security.Permissions`. Offramp reports the run failure (OFR3740) with
  the missing assembly's name instead of adding the package itself.
- The probe runtime is a few hundred lines of C# 6 that Offramp generates and
  owns. Its output format is versioned (`# offramp-newtonsoft-probe 1`) so a later
  change can re-record instead of misreading old baselines.
