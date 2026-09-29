# 0036. Build legacy (non-SDK) solutions outside Windows

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/compiling-on-macos.md`, `docs/spec/commands/workspace.md` (`scan`, `doctor`)

## Context

The compile-only block (ADR 0030) made SDK-style projects, including
`MSBuild.SDK.SystemWeb` ones, build on macOS and Linux. Legacy projects, which most
.NET Framework codebases still are, did not: on a fresh DotNetNuke 9.13 checkout (64
legacy projects of 71), `scan` left 6 projects unloaded and 51 partial, and
`doctor --fix` changed nothing that helped. A day of manual work showed what they need,
and which of it Offramp can supply:

1. Reference assemblies. The SDK adds `Microsoft.NETFramework.ReferenceAssemblies` to
   SDK-style projects only (MSB3644 otherwise), and the package wires the Visual Basic
   runtime for SDK-style projects only.
2. The Visual Studio web targets, which the ASP.NET section added to SDK-style projects
   only (MSB4019).
3. The `packages.config` packages. `dotnet restore` skips `packages.config` silently, so
   every `HintPath` into `packages/` dangles.
4. No `NuGet.exe` restore: solutions from the NuGet 2 era import `.nuget/NuGet.targets`,
   which runs `mono NuGet.exe` outside Windows (MSB3073).
5. Paths spelled in the letter case of the file on disk. Windows and macOS file systems
   ignore case; Linux does not (37 imports, a documentation file copy, and 99 source
   files in DotNetNuke).
6. No steps that only .NET Framework's MSBuild runs: `CodeTaskFactory` inline tasks
   (MSB4801, from `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` in nearly every
   Web Forms site), non-string `.resx` resources without preserialized resources
   (MSB3822/MSB3823), and `Exec` commands written for `cmd.exe`.

## Decision

- **The compile-only block has a third section** for legacy projects outside Windows:
  `RestoreProjectStyle=PackageReference`, and the reference assemblies and web targets
  packages as implicit, private references. A legacy project then restores those two
  packages like an SDK-style one; its `packages.config` is left to `scan` (next point).
  For a legacy Visual Basic project it also references `Microsoft.VisualBasic` and, in a
  target before `CoreCompile`, passes it to the compiler as the runtime with
  `DisableSdkPath`, as the SDK does. The section is marked by
  `<OfframpLegacyPackages>`, and `doctor --fix` adds it to files that have the first
  two sections.
- **`scan` restores `packages.config` packages** outside Windows before its build, into
  the folder `nuget restore` uses (`repositoryPath` from `nuget.config`, else `packages/`
  beside the solution) and in its layout (`<Id>.<Version>/` named from the nuspec, with
  the `.nupkg`). Each package comes from the NuGet global packages folder when it is
  there, else from the feeds `nuget.config` enables, through the NuGet client
  libraries. A folder that exists in any letter case is never touched. What was written
  is `OFR0106` (info), each package not found `OFR0105` (warning).
- **Every build Offramp runs** (scan, verification, `csproj modernize`) passes
  `RestorePackages=false` outside Windows unless `verify.properties` sets it. Only
  `.nuget/NuGet.targets` reads the property, and the project file sets it after
  `Directory.Build.props`, so the block cannot.
- **What Offramp cannot supply is detected and named** from the build's errors, each with
  the file to change: a path that differs from the file on disk in letter case only
  (`OFR0117`, from MSB4019, CS2001, MSB3030, or CS0006), an inline task factory only
  .NET Framework's MSBuild has (`OFR0118`), and non-string resources (`OFR0119`). A
  `cmd.exe` command in an `Exec` that failed (MSB3073) is a `build-event` step
  (`OFR0115`) whether it came from `PostBuildEvent` or a target. Paths in the evidence
  are repository-relative.
- **`doctor`'s reference assemblies check** warns (`OFR0017`) when the model has legacy
  projects and the compile-only block lacks the legacy section, instead of passing
  because the package is in the cache.

## Alternatives considered

- Setting `TargetFrameworkRootPath` and `VSToolsPath` to folders in the global packages
  folder: works (it is how DotNetNuke was first built here) but needs the packages
  downloaded by something else and a path that differs per machine.
- `RestoreProjectStyle=PackageReference` looked risky for solutions where a
  `netstandard2.0` project references a legacy project (NuGet could start enforcing
  NU1201 on it). Restoring DotNetNuke's `DotNetNuke.DependencyInjection` with the style
  set showed no NU1201, and the whole solution restores.
- A throwaway `PackageReference` project restored with `dotnet restore` to fetch the
  `packages.config` packages: another process, another `Directory.Build.props` in scope,
  and `PackageDownload` semantics to work around. The client libraries do it directly.
- Converting every project with `csproj modernize` first: the documented route, but it
  refuses web application projects, and a conversion should not be the price of a scan.
- Rewriting paths to their on-disk case, or redefining the CodeDom provider's targets in
  the block: the first edits the user's files, which a build preparation must not do;
  the second cannot work from `Directory.Build.props`, which MSBuild imports before the
  package's targets. The diagnostics name the fix instead, and
  `docs/compiling-on-macos.md` shows the `Directory.Build.targets` one.

## Consequences

`scan` writes `packages/` folders, which `nuget restore` would have written; the
`.gitignore` templates of such repositories exclude them. Restoring needs the feeds in
`nuget.config` or a warm global packages folder. On DotNetNuke 9.13, a fresh checkout
with `doctor --fix --apply` and `scan` loads all 71 projects, and once the case
mismatches are fixed and the `XCOPY` targets guarded, the build stops only at the inline
task (fixed as documented) and two projects with non-string resources. The case, task
factory, `Exec`, and resource findings remain the user's to fix, one diagnostic each.
