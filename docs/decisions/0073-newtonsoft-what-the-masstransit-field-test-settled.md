# 0073. Keep the newtonsoft commands to what they own, and say why a probe cannot run

- Status: accepted
- Date: 2026-10-10
- Spec section: `docs/spec/commands/newtonsoft.md`

## Context

The MassTransit 3.3.5 field test (`docs/field-tests/2026-10-masstransit-3.3.5.md`) ran the
`newtonsoft` commands on a real codebase for the first time. The specification left the following
questions open, or answered them in a way the codebase showed to be wrong:

- **Which redirects `newtonsoft upgrade` changes** (P1 #3). ADR 0072 runs `redirects sync` over the
  upgraded model so that the redirects third-party packages need are computed, not guessed. As
  written it also took every other edit `redirects sync` would make: on MassTransit, two stale
  log4net redirects.
- **What a test project with no probes gets** (P1 #4). The runtime was "once per test project",
  so three MassTransit test projects whose sites all went uncovered got two runtime files and two
  `Compile` items, and nothing that used them.
- **How far settings are followed** (P1 #2). The spec named what is `member` and what is `unknown`
  but not how far a reference is followed to get there. MassTransit caches its serializers in
  `static readonly Lazy<JsonSerializer>` fields set in a static constructor, and copies one into a
  readonly field in a constructor; both were `unknown`.

## Decision

- **The upgrade syncs the Newtonsoft.Json redirects only.** `redirects sync` still computes them
  from the deployed assemblies (ADR 0072 stands), but `RedirectsRequest.Assemblies` limits the
  plan to the `Newtonsoft.Json` identity. The upgrade changes only Newtonsoft.Json in the model,
  so any other redirect that is out of date was so before; it is `redirects sync`'s to fix, in a
  change of its own.
- **No probes, no files.** A test project whose probes were all left out gets nothing: no runtime
  and no `Inputs/` copies, and the files an earlier run generated there are deleted with their
  `Compile` items, as any file no longer produced is. The runtime stays while a file the user took
  over (OFR3742) is in the folder, since it may use it. Samples are copied only for probes that are
  written.
- **Follow a reference while its value is certain.** A `Lazy<T>`'s `.Value` is what its factory
  lambda returns, when the lazy itself has one known value. A readonly field (static or instance)
  has one value when it is assigned exactly once: in its initializer, or in the type's only
  constructor of its kind, and never passed by `ref`. Anything less certain (two constructors, a
  compound assignment, a parameter) stays `unknown`, now located where the value came from.

## Alternatives considered

- **Keep every edit, as before.** The dry run shows them, but they land in the upgrade's journal,
  and a behavior difference `--verify` finds afterwards could come from either change.
- **Keep the edits that differ between planning on the original model and on the upgraded one.**
  Exact in principle, but it plans twice for no difference in practice: Newtonsoft.Json has no
  dependencies on .NET Framework, so only its own redirects can differ.

- **Trace serializers passed into constructors to the callers that pass them.** It would cover
  MassTransit's five deserializers, but each has several callers with different serializers, so
  a site would need several settings; that is a model change for another time.

## Consequences

- The upgrade's diff and result list Newtonsoft.Json redirects only. A user who wants the rest runs
  `redirects sync`, which says what it changes.
