# 0030. Supply the ASP.NET web targets from a package outside Windows

- Status: accepted
- Date: 2026-09-28
- Spec section: `docs/compiling-on-macos.md`, `docs/spec/02-workspace-model.md`

## Context

`MSBuild.SDK.SystemWeb` (4.0.88) ends its `Sdk.targets` with an unconditional
import of `$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets`.
Only Visual Studio installs that file, so `dotnet build` of such a project stops
with MSB4019 on macOS and Linux (and under the .NET SDK on Windows). The failed
evaluation is still recorded in the binary log, with properties but no items or
imports, so the model got the project as a partial `library` and doctor said no
project needed Windows. The Windows-only step list did not cover web targets or
`MvcBuildViews`, which the SDK turns on in Release and which runs
`AspNetCompiler` (MSB4803 under .NET's MSBuild).

## Decision

- A new step, `OFR0116`, with two ids: `web-targets` (an import of the web
  targets from anywhere but the package below, or an MSB4019 error naming them,
  from the project's evaluation or, when there is none, from the not-loaded
  path) and `aspnet-compiler` (`MvcBuildViews=true`).
- The compile-only block gains a second section, found by its own marker, so a
  file with the first section from an earlier Offramp gains only the new one.
  Outside Windows it adds an implicit (`IsImplicitlyDefined="true"`),
  private reference to `MSBuild.Microsoft.VisualStudio.Web.targets` 14.0.0.3
  for SDK-style projects that define `VSToolsPath`; turns `MvcBuildViews` off;
  and points `AspNetTargetsPath` at a folder without the Web Deploy targets.
- Checked with the real toolchain: the `systemweb` fixture fails to build as is
  and builds (build, clean, `--no-incremental`, Release) with the block.

## Alternatives considered

- A stub `Microsoft.WebApplication.targets` written into the repository: a
  second file to own and commit, and a build that differs more from Windows.
- Conditioning the package on the targets being missing
  (`!Exists('$(VSToolsPath)/...')`): the package's own props set `VSToolsPath`,
  so the condition flips once restored. NuGet's restore evaluation skips package
  props and hides this, but an IDE restore does not; the condition would flap.
- Separate `Version`/`VersionOverride` items for central package management:
  an implicit reference may carry a version under CPM, and implicit references
  are already excluded from the model's packages.
- Keeping the Web Deploy targets: they add `CleanWebsitesPackage` to `Clean`,
  whose tasks need .NET Framework's MSBuild, so every rebuild failed.

## Consequences

- Every repository that already has the first section is offered the second by
  `doctor --fix` and `init`; it is inert without System.Web projects.
- Web Deploy publishing is unavailable outside Windows, as it was before.
- `web.config` is found case-sensitively on Linux (`AppConfig=web.config`);
  macOS's default file system hides this.
