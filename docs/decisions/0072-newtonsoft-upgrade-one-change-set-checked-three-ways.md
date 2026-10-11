# 0072. Upgrade Newtonsoft.Json as one change set, checked by restore, build, and probes

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-upgrade`

## Context

`newtonsoft upgrade` edits several kinds of files: SDK project files, central
version files, `packages.config`, legacy `Reference` items with their HintPaths,
and application configuration files. It then has to show that the result builds
and still behaves. Five questions were open:

- whether to reuse `deps consolidate`;
- how redirects learn about a package that is not restored yet;
- which redirects to touch;
- when to roll back;
- where the code lives, since it needs both the refactoring library and the
  probe recorder.

## Decision

- **Its own writers.** `deps consolidate` chooses versions by constraint and
  writes only PackageReference and central versions (packages.config gets OFR1204).
  The upgrade knows its target and must write every style. New writers do the
  legacy part:
  - `PackagesConfigEditor` changes only the bytes of the `version` value;
  - `ProjectFileEditor.RewriteReference` changes the identity's `Version=` and the
    HintPath's package and `lib/` folders. The `lib/` folder is read from the
    target package, not guessed.

  PackageReference and central versions are a metadata edit.
- **Redirects from `redirects sync`, over the model as the upgrade leaves it.**
  The target's package is put in the global packages folder (outside the
  repository) so the planner can read its assemblies. The planner then runs on a
  model with every Newtonsoft.Json reference at the target, and its edits join
  the same change set.
  - Running this exposed a gap in `redirects sync` itself. When only one version
    of an assembly is deployed, it left existing redirects alone, even one
    pointing at a version no longer deployed (`0.0.0.0-7.0.0.0 → 7.0.0.0` after
    the upgrade), which makes any assembly built against an old release fail to
    load.
  - `redirects sync` now moves such a redirect to the deployed version
    (OFR1502). That is a fix for every caller, not only this command.
- **One change set, one journal, checked three ways.** Apply, then:
  1. `dotnet restore` plus the packages.config restore;
  2. a build of what `verify.projects` selects;
  3. the probes against their baselines.

  A failed restore or build rolls everything back (OFR3766, OFR3761), because the
  repository would not build. Probes that differ do not roll back by default:
  the differences are what the user needs to see, on an upgrade they can still
  build and examine (`--on-diff rollback` exists for CI).
- **In `Offramp.Scaffolding`.** Comparing probes needs the probe recorder, which
  lives with the probe generator. Scaffolding already references Refactoring
  (journals, editors, redirects), so the orchestration sits there. The generic
  writers stay in Refactoring.

## Alternatives considered

- **Extend `deps consolidate` with a target version and packages.config writing.**
  It would serve one caller, and consolidate's version choice would fight a
  target the user named.
- **Rewrite only the Newtonsoft.Json redirect directly.** That misses redirects a
  third-party package built against an older Newtonsoft.Json needs, which
  `redirects sync` computes from the deployed assemblies.
- **Roll back on any probe difference.** That hides exactly the information the
  probes were recorded for, and makes the decision for the user.

## Consequences

- The dry run shows every file the upgrade will touch, including configuration
  files, as one diff.
- `redirects sync` users get the same protection against redirects left pointing
  at undeployed versions.
- The global packages folder gains the target release during a dry run. Nothing
  in the repository changes.
