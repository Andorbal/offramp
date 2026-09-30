# 0064. Make dotnet build the same on Windows, and restore packages.config with dotnet restore

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/workspace.md#doctor`, `docs/compiling-on-macos.md`

## Context

ADR 0063 made a plain `dotnet build` outside Windows do what Offramp's build
does, and left two gaps on the way to "anyone can build the whole solution on a
Mac, and it builds the same on Windows":

- The compile-only block applied outside Windows only. `dotnet build` on
  Windows runs the same .NET MSBuild, which has no `SGen`, `AspNetCompiler`, or
  `Microsoft.Bcl.Build` task, and whose `VSToolsPath` points into the SDK,
  where Visual Studio's web targets are not. So `MSBuild.SDK.SystemWeb`, which
  turns `MvcBuildViews` on in Release from its own targets, built with `dotnet
  build -c Release` on a Mac and failed with it on Windows (MSB4803), as did
  its web targets import (MSB4019).
- `dotnet restore` does not read `packages.config`. On a fresh clone the
  packages folder was empty until someone ran `offramp scan` (or `nuget
  restore` on Windows), so a plain build of a legacy solution failed for anyone
  without Offramp. Most legacy codebases are on `packages.config` (DotNetNuke
  64 of 71 projects, SmartStoreNET 24 of 25).

## Decision

**The block gets a section for `dotnet build` on Windows**, conditioned on
`IsOSPlatform('Windows') And '$(MSBuildRuntimeType)' == 'Core'`: sgen off,
`MvcBuildViews` false, `AspNetTargetsPath` to the repository, and
`SkipEnsureBindingRedirects`, as outside Windows, and the web targets package
for SDK-style projects with a `VSToolsPath`. Visual Studio and MSBuild.exe
(`Full`) are unaffected. Legacy projects keep their Windows behavior (their
restore stays `packages.config`'s), so the legacy section is not extended.

**`packages.config` is restored by `dotnet restore` itself.** When the
solution has `packages.config` projects, `doctor --fix` adds, at the root:

- `Offramp.PackagesConfig.targets` (embedded in Offramp, rewritten when it
  changes, a CRLF checkout counting as current). A target after `Restore`, in
  a project or in a solution's metaproject, collects the restored projects
  (NuGet's `_RestoreGraphEntry` items of type `ProjectSpec`), and an inline task
  (`RoslynCodeTaskFactory`, which .NET's MSBuild has on every OS) works out each
  project's packages folder as `nuget restore` does: `repositoryPath` from the
  nearest `nuget.config` above the solution, else `packages/` beside it; for a
  project restored alone, the folder its `HintPath`s and imports name. Packages
  the folder lacks and the global packages folder lacks go into a generated
  SDK-style project under the solution's `obj/offramp-packages-config/`, one
  `PackageDownload` per package with every version the projects list, which
  the target restores with the `MSBuild` task: NuGet itself downloads them,
  with the solution's feeds and credentials. The task then copies each package
  from the global packages folder into `<Id>.<Version>/`, named from its nuspec,
  with the `.nupkg` renamed and without the global folder's bookkeeping,
  through a temporary folder moved into place. A package already there, in any
  letter case, is left alone.
- An import of it in the block's new last section (for projects, so `dotnet
  build Project.csproj` restores the legacy projects it references) and in
  `Directory.Solution.targets` (created, or the import added before its
  `</Project>`), which MSBuild imports into a solution's metaproject. Both are
  conditioned on `'$(MSBuildRuntimeType)' == 'Core'`.

The plain-build check counts `packages.config` as handled when the targets
file is current and both imports are in place.

## Alternatives considered

- `PackageDownload` items in the legacy projects themselves: NuGet attaches a
  download to a target framework alias, and a legacy project has none, so the
  downloads are dropped (with an alias, the restore fails).
- `PackageReference` with `ExcludeAssets="all"` for each `packages.config`
  entry: NuGet resolves dependencies, so the version conflicts and downgrades
  `packages.config` tolerates become NU1605/NU1107 errors, and packages without
  a compatible `lib/` fail NU1202.
- Downloading from the feeds in our own code: loses credential providers and
  every other NuGet setting.
- `RestorePackagesConfig=true`: only MSBuild.exe's NuGet supports it; `dotnet
  restore` reports nothing to do.
- Laying packages out during the build instead of the restore: MSBuild
  evaluates a project's imports before any target runs, so the first build
  would miss the packages' `build/` targets and the second would not.
- A separate `.cs` file for the task: a root-level `.cs` file is compiled into
  any project at the root.

## Consequences

- A fresh clone builds with `git clone` and `dotnet build` on macOS, Linux,
  and Windows, with or without Offramp, once the three files are committed. On
  the mvc5 fixture a plain build fails without them (CS0246) and succeeds with
  them; legacy-shared's two versions of Newtonsoft.Json restore from an empty
  NuGet cache.
- The same fix found a latent bug in the test helper that fills `packages/`
  for fixture scans: it restored one version of a package listed twice, which
  failed on a CI runner with an empty cache.
- The repository carries a targets file with inline C#. It is Offramp's, marked
  so, and small; `doctor` rewrites it rather than merging edits.
- `offramp scan` still restores `packages.config` itself first (it reports
  `OFR0105`/`OFR0106`), so its build finds the folder filled; the targets file
  is exercised by plain builds and by the fixture tests.
