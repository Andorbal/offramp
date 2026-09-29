# 0034. Let scan build with Visual Studio's MSBuild

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/workspace.md#scan`, `docs/spec/03-configuration.md`

## Context

`scan` builds the solution with `dotnet build`, which runs the .NET SDK's
MSBuild. Some .NET Framework projects only build with the .NET Framework
MSBuild that ships with Visual Studio and the Build Tools: sgen fails with
MSB3474, COM references with MSB4803, and the web application targets are not
there at all. On Windows, where that MSBuild exists, users had no way to have
`scan` use it short of building by hand and passing `--binlog`. The spec left
open how to choose the builder, where MSBuild.exe comes from, and whether the
build changes shape.

## Decision

- **Choice.** `scan.builder: dotnet | msbuild` in `offramp.yml` (default
  `dotnet`), overridden by `--msbuild` for one run. It lives under a new
  `scan:` section rather than `verify:`: `verify` keeps building with `dotnet`,
  and changing that is a separate decision.
- **Location.** `--msbuild-path` or `scan.msbuildPath` names MSBuild.exe, a
  folder holding it, or an installation folder, where `MSBuild/Current/Bin` and
  then `MSBuild/15.0/Bin` are tried. `--msbuild-path` implies `--msbuild`,
  because nobody names an MSBuild they do not want. Without a path, scan uses
  the Developer Command Prompt's installation (`VSINSTALLDIR`), else the newest
  release installation with the `Microsoft.Component.MSBuild` component that
  vswhere reports (Build Tools included), which is the query vswhere documents.
- **Same build.** `MSBuild.exe <solution> -restore -t:Rebuild -m -bl:<log>
  -p:Configuration=<verify.configuration>` is what `dotnet build
  --no-incremental -c <configuration>` runs, with the same logging switches and
  `verify.properties` last. It adds `-p:RestorePackagesConfig=true` so
  packages.config projects, which are most of the ones that need this builder,
  are restored as Visual Studio restores them; a `verify.properties` entry can
  still turn that off.
- **Failure.** An MSBuild.exe that cannot be found or started is `OFR0017`,
  an environment failure (exit 3) like a missing `dotnet` (`OFR0010`), with
  what was searched in the message. The flags are a usage error (exit 2) with
  `--binlog`, `--complog`, or `--no-build`, which do not build.

## Alternatives considered

- Searching `PATH` for MSBuild.exe: many machines have
  `C:\Windows\Microsoft.NET\Framework\v4.0.30319` on `PATH`, whose MSBuild 4
  builds nothing modern, and a Developer Command Prompt is already covered by
  `VSINSTALLDIR`. A deliberate choice is one `--msbuild-path` away.
- Loading MSBuild in process (`Microsoft.Build.Locator`): the .NET Framework
  MSBuild cannot load into a .NET process, which is the whole reason for this
  builder.
- `vswhere -prerelease`: would prefer a Preview installation over a release one
  on machines with both. A Preview-only machine names it with `--msbuild-path`.
- A `--builder dotnet|msbuild` option: more general, but the only alternative
  builder is MSBuild, and the environment variable `OFFRAMP_SCAN__BUILDER=dotnet`
  already undoes a configured `msbuild` for one run.
- Recording the builder in the scan result or the workspace model: the
  envelope's `effectiveConfig` already carries `scan.builder`, and the model
  should not change with the tool that produced the same evaluation.

## Consequences

- Windows users with sgen, COM, or web application projects get a complete
  model from `scan` alone, without the compile-only block.
- `effectiveConfig` gains `scan` in every envelope, and `init` writes the
  section, so the setting is visible.
- MSBuild.exe cannot be exercised on Linux or macOS CI: tests use a fake that
  checks the command line and returns a log captured by MSBuild on Windows.
  A change to the build's switches needs a manual check on Windows.
