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
