# 0080. Use deployed identities and bounded probe result filenames

- Status: accepted
- Date: 2026-10-15
- Spec section: `docs/spec/commands/newtonsoft.md`

## Context

A production project can host its own tests while its declared Newtonsoft.Json release is older than the assembly actually resolved by the test host. When there is no deployed release below the target, comparing probe results with the declaration rejects a valid recording. Separately, probe IDs are derived from source identities and can become long enough that result and baseline paths exceed Windows path limits. A missing result also needs to distinguish path trouble from test selection or discovery problems.

## Decision

When the test host is the production project and deployment scope has no release below the target, compare against that project's resolved Newtonsoft.Json release. Deployment scope uses a resolved package release when available, before falling back to its declared reference. Keep short probe IDs byte-for-byte stable; truncate longer, filename-safe IDs to 100 characters and append a 12-character lowercase SHA-256 digest. Use the same filename transform for results and baselines while retaining the probe's full generated identity wherever it is represented in a record. OFR3741 names the generated class and method, reports first-probe result/baseline path lengths, and advises checking the configured filter, adapter discovery, and early test-host exit when paths are not near the platform limit.

## Alternatives considered

- Compare a self-hosted test against the declared old package regardless of the loaded assembly: rejected because this can reject probes that actually ran under the project's resolved release.
- Shorten all probe IDs: rejected because it would needlessly change established baselines.
- Rely on Windows long-path support: rejected because test hosts and third-party adapters may not support it consistently.
- Report only aggregate missing counts: rejected because it does not tell a user which generated method to select or inspect.

## Consequences

Existing short probe IDs and baseline paths remain unchanged. Long IDs keep a deterministic, collision-resistant suffix and use consistent filenames across record, verify, input, cleanup, and baseline generation. Missing-result warnings identify the generated tests and give actionable path and test-host diagnostics.
