# 0078. Scope Newtonsoft analysis to deployed releases and avoid needless version churn

- Status: accepted
- Date: 2026-10-15
- Spec section: `docs/spec/commands/newtonsoft.md`

## Context

A project-reference solution can have several applications resolving different Newtonsoft.Json releases while a shared library's own package reference is older than the resolution in most consuming applications. Scanning every library site's own declared release over-reports behavior changes and generates probes for code not running under an older deployed release. The upgrade also previously rewrote already-compatible patch releases and broad binding redirect ranges without changing the deployed version.

## Decision

Compute each non-test web, service, console, WinForms, and WPF application's deployed Newtonsoft.Json release from the workspace model, then attribute those releases to projects in its project-reference closure. Analysis and probe generation default to projects where an attributed deployed release is older than the target; report release sets and applications in scan output, summarize excluded projects with OFR3726, and allow `--all-projects` or `newtonsoft.scope: all`. Behavior rules use the older deployed labels while API trial compilation continues to use the project's recorded compilation. Upgrade remains broad over selected Newtonsoft.Json references but leaves stable same-major releases at or above its configured floor alone unless an explicit exact version is requested. Redirects with the already-deployed newVersion and a covering oldVersion range are left unchanged. Newtonsoft probe destination selection is separate from the shared `move tests` policy and requires the destination to resolve the deployment release, with a production assembly that hosts tests allowed as a final fallback.

## Alternatives considered

- Judge every project by its own PackageReference version: rejected because nearest-wins may cause that release never to run in most apps.
- Filter all upgrade reference edits to deployed scope: rejected because an old shared-library declaration should still be upgraded even when consumers currently resolve a newer release.
- Normalize all redirects and compatible patch releases to one exact version: rejected because it creates churn without changing the deployed assembly.
- Change `move tests`' shared selector: rejected because its behavior is unrelated and already has its own contract.

## Consequences

Scan results now expose deployment attribution and omit irrelevant sites by default. Users can request the historical all-project inventory explicitly. Tests are placed where the characterized release actually runs, or report the mismatch. Existing compatible patch references and sufficient redirects remain byte-for-byte unchanged; explicit `--version` remains exact.

## Amendment

When automatic test-project selection has several direct candidates resolving the deployed release, `newtonsoft tests` breaks ties before reporting OFR3734: first prefer a production project that hosts tests and itself resolves a deployed release below the target (`By=self`, OFR3749); then a candidate named `<Production>.Test` or `<Production>.Tests`; then the most downstream candidate that transitively references every other matching candidate; then a candidate of kind `test` over an application. Candidate names in any remaining OFR3734 are ordinally sorted. Type probes may also run when the test project's Newtonsoft.Json assembly identity equals the identity for any deployed production release below the target, even when that differs from the production project's own recorded compilation identity.

For `newtonsoft upgrade` without an explicit `--version`, cap the newest compatible release in the configured range at the lowest stable, directly-declared Newtonsoft.Json release (PackageReference, central package, `packages.config`, or `HintPath`) already in use at or above `newtonsoft.upgradeFloor` and in the selected target major. Such projects, and projects on higher releases, remain unchanged; this prevents the projects being upgraded from creating a downgrade below a release the solution still uses. If no existing direct release limits the newest target, retain the existing newest-release behavior. `OFR3724` explains the chosen cap. An explicit `--version` remains exact and bypasses this policy.

For `newtonsoft upgrade`, binding redirects are planned only for applications whose deployed Newtonsoft.Json release changes under the same deployment-scope and upgrade-floor rule; left-alone applications receive no redirect edits, including normalization. A changed application gets an existing redirect rewritten only when its newVersion differs from the target assembly version, or gets a redirect added if it had none. A correct newVersion does not warrant rewriting its oldVersion range. This avoids unrelated app.config churn and keeps automatic redirects untouched.
