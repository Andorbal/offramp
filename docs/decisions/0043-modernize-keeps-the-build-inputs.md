# 0043. Keep what the legacy build compiled against when converting to SDK style

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/scaffold.md#csproj-modernize`

## Context

`csproj modernize` proves a conversion by building it and comparing what the compiler gets. Three
field tests found conversions that failed that proof, or did not build, for reasons that came from
the SDK's defaults rather than from the project:

- **Project references flow.** NHibernate 4.1.2's `TestDatabaseSetup` references `Test`, which
  references `DomainModel`. An SDK-style project compiles against the whole ProjectReference closure,
  a legacy one against its direct references, so the conversion added `NHibernate.DomainModel` and
  failed verification (`OFR4303`). The field test left two options open: treat such a reference as
  an allowed, transitive addition, or keep the reference set with `DisableTransitiveProjectReferences`.
- **NuGet 2 package restore.** Ten of SmartStoreNET 4.2.0's eleven conversions failed with MSB4019:
  the converter dropped `SolutionDir` and `RestorePackages` but kept
  `<Import Project="$(SolutionDir)\.nuget\NuGet.targets" />`, and a project built on its own has no
  `SolutionDir`.
- **Packages flow.** In Open Live Writer 0.6.3, `PostEditor` asks for Newtonsoft.Json 10.0.2 and
  references `BlogClient`, which asks for 13.0.1. With `packages.config` each project compiled
  against its own; with `PackageReference` the 13.0.1 flows into `PostEditor`, whose direct 10.0.2 is
  then a package downgrade, NU1605, which the SDK treats as an error. Five conversions failed.

## Decision

The converted project compiles against what the legacy one did, whatever the SDK would pass on:

- When a referenced project has project references of its own (or is not in the model, so nobody
  knows), the converted project sets `DisableTransitiveProjectReferences` to `true`. Its references
  stay its direct ones, and the compile set comparison stays strict: an added reference is still a
  difference.
- The NuGet 2 restore import (`.nuget\NuGet.targets`, any spelling of the path) goes with
  `RestorePackages`: `PackageReference` restore replaces it. `SolutionDir`'s definition, with its
  fallback for builds outside the solution, is kept whenever something the conversion keeps still
  uses `$(SolutionDir)` (a build event, another import), and dropped otherwise.
- When a package that flows in from a referenced project (converted in the same run, or already
  restoring the `PackageReference` way) is at a higher version than the project's own
  `packages.config` entry, the converted project asks for the higher version, and `OFR4307` says so.
  Packages marked as development dependencies, or `PrivateAssets="all"`, do not flow and do not count.

## Alternatives considered

- Allow an added reference when it is the output of a project in the closure, as packages that flow
  are allowed. The conversion would then compile against assemblies the legacy project never saw,
  which can change overload resolution and which extension methods are in scope, and verification
  would no longer be able to tell. Keeping the reference set is the conservative choice.
- Drop `SolutionDir` and every use of it. A build event's `$(SolutionDir)packages\...` would then
  point at the file system root; keeping the property keeps the command's meaning.
- Report the package downgrade and keep the lower version. The conversion would not build (NU1605 is
  an error by default), and forcing the lower version on the referenced project's assembly is the
  one choice NuGet refuses. Raising is what `deps consolidate` would propose.

## Consequences

Converted projects in a deeper reference chain carry `DisableTransitiveProjectReferences`; removing it
later is a deliberate step that `csproj modernize`'s verification can check. A project whose own
package version was raised compiles against the newer package, which verification builds; the
message names the project the version comes from.
