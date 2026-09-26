# 0011. Make the workspace model independent of the scanning machine

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/02-workspace-model.md`, `docs/spec/00-architecture.md`

## Context

The model must be identical for the same commit whether it is scanned on
Windows, macOS, or Linux (roadmap M1 acceptance). Several things MSBuild and
NuGet record depend on the machine, and the spec leaves a few classification
cases open.

## Decision

- **Framework class.** net4x only is `framework`; netstandard only is
  `standard`; modern .NET only is `modern`; net4x with anything else is `dual`.
  netstandard with modern .NET (no net4x) is `standard`: the project is already
  portable to every modern target. An unrecognized framework (for example
  `uap10.0`) counts as `framework`, the conservative reading.
- **Toolchain references are not the project's.** Assembly references the SDK
  or a package's build files add (`IsImplicitlyDefined`, `_SDKImplicitReference`,
  `NuGetPackageId` metadata, `Pack=false` without a hint path, `mscorlib`) are
  left out of `assemblyReferences`: they differ between the reference-assembly
  package and the Windows targeting pack, and NETStandard.Library adds a hundred
  facades. Likewise `resolved` leaves out packages reachable only from
  dependencies NuGet marks `autoReferenced` (NETStandard.Library,
  Microsoft.NETFramework.ReferenceAssemblies, which the SDK adds only where no
  targeting pack is installed).
- **A Reference whose Include is a file path** is a file reference named after
  the file, with that path as its hint path.
- **`defineConstants`** per target framework come from the compiler call when
  there is one (it includes the SDK's implicit symbols such as `NET48_OR_GREATER`),
  from evaluation otherwise.
- **New fields**: `language` (from the project file extension),
  `defineConstants`, `packagesConfig` (a `packages.config` beside the project),
  `inputs` (0009), and `source.complog` (0010). `partial: true` marks a project
  with a target framework that has no compiler call.

## Alternatives considered

- Recording every reference MSBuild resolved: exact, but not comparable
  across machines and noisy for every consumer.

## Consequences

- Consumers that need the full reference set rebuild the compilation from the
  compiler log, which has exactly what the compiler saw.
