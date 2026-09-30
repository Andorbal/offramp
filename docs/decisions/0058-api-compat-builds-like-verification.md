# 0058. `audit api-compat` builds the way verification does, and the baseline gets what no revision holds

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/audit.md` (`audit api-compat`)

## Context

`audit api-compat` built each side with `dotnet build <project> -c Release -f <tfm>`. On NHibernate 4.1.2 outside
Windows (field test P1 #10), with `--baseline 4.1.1.GA`, both sides failed (`OFR3504`, exit 3):

- The working tree failed with MSB3073: Release runs NHibernate's Release-only `AfterBuild`, which calls
  `Tools/ilrepack/ilrepack.exe`. The build also left out `verify.properties`, so `RestorePackages=false` (which
  every other Offramp build passes outside Windows, ADR 0037) and the user's properties were missing.
- The baseline, a scratch work tree of the revision, failed with MSB3644 (no net40 reference assemblies): it had
  neither the compile-only block that `doctor --fix` wrote into the working tree's `Directory.Build.props`
  (untracked, or uncommitted) nor `src/SharedAssemblyInfo.cs`, which NAnt generates and git ignores.

The spec said "Release" and nothing about what the baseline's work tree holds.

## Decision

- Both sides build with `verify.configuration` and `verify.properties` (`-c` and the `-p:` arguments every
  Offramp build takes, `RestorePackages=false` off Windows included). The public API is the same in Debug and
  Release; a Release-only step (ILMerge, ILRepack, signing tools) is not part of it and often needs Windows.
- When the working tree's root `Directory.Build.props` has Offramp's compile-only block, the baseline's work tree
  gets the compile-only sections its own root `Directory.Build.props` lacks, inserted as `doctor --fix` would.
  Offramp owns those sections, and without them no legacy project builds off Windows.
- Git-ignored files that the working tree's compilation uses (the `Compile` items the model records for the
  project and every project it references, outside `bin` and `obj`) and that the baseline's work tree lacks are
  copied into it, and `OFR3505` (info) names them. No revision can hold a git-ignored file, so the baseline
  cannot build without it; untracked files that git does not ignore are new code and are not copied.

## Alternatives considered

- Passing the compile-only block as `-p:CustomBeforeMicrosoftCommonProps=<file>`: it reaches every project
  without touching the work tree, but the global property also reaches the working-tree side, differs from what
  `scan` and verification use, and would replace a `CustomBeforeMicrosoftCommonProps` the repository sets itself.
  The scratch work tree is Offramp's own, so writing into it is the smaller change.
- Copying every untracked file: a new source file in the working tree would then appear on both sides, hiding
  exactly the API difference the command is for.
- Keeping Release and adding `-p:PostBuildEvent=` or skipping targets: there is no general way to switch off a
  project's own `AfterBuild`, and verification already answers "which configuration": the one the user chose.

## Consequences

`audit api-compat --baseline` works on a legacy project outside Windows once `scan` does. A generated file that
differs between revisions (a version number) is taken from the working tree for both sides; `OFR3505` says which
files, so the user can judge. The comparison uses the verification configuration, so an API that only a Release
build exposes (an `#if !DEBUG` member) is compared as Debug sees it unless `verify.configuration` is `Release`.
