# 0063. Condition Windows-only settings where the project files set them

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/workspace.md#doctor`, `docs/compiling-on-macos.md`

## Context

`doctor --fix` wrote only the compile-only block into the root
`Directory.Build.props` (ADR 0012). MSBuild imports that file before the
project's own properties, so whatever a project file sets itself wins over the
block: `<GenerateSerializationAssemblies>On</GenerateSerializationAssemblies>`
still runs sgen outside Windows (MSB3474), and `<MvcBuildViews>true</MvcBuildViews>`
still runs `AspNetCompiler` (MSB4803). Build events and `Exec` commands written
for cmd.exe are not in the block's reach at all. `Directory.Build.targets` is
no remedy: it is imported after `Microsoft.Common.CurrentVersion.targets` has
already copied `GenerateSerializationAssemblies` into the property sgen reads,
and a legacy project defines its `PostBuildEvent` after that import.

Offramp's own builds got past these with `offramp.yml`'s `verify.properties`
(`-p:` global properties, which do beat the project) and, outside Windows,
`-p:RestorePackages=false`. That made `scan` succeed where a plain `dotnet
build` of the same checkout failed. The corpus harnesses leaned on it
(SmartStoreNET's and Open Live Writer's `PostBuildEvent: ""`), and `doctor`
reported "No project needs Windows to build" because the model's evaluations
had the override applied.

The goal is a repository that anyone can build on macOS or Linux with a plain
`dotnet build`, without Offramp, and a build there that tells a developer what
a build on Windows will do.

## Decision

`doctor --fix` also conditions each Windows-only setting in the MSBuild files
that set it: the solution's C#, Visual Basic, and F# project files, every
`Directory.Build.props`/`.targets` above them, the repository files they import
by a literal or `$(MSBuildThisFileDirectory)`/`$(MSBuildProjectDirectory)` path
(not those in a `packages` folder), and a settings file a scan named in
`OFR0122`. The rules:

| Setting | Condition added | Why that one |
|---|---|---|
| `GenerateSerializationAssemblies` other than `Off` | `'$(MSBuildRuntimeType)' != 'Core'` | sgen needs .NET Framework's MSBuild, which only Visual Studio and Build Tools have; `dotnet build` fails on it on Windows too |
| `MvcBuildViews` = `true` | `'$(MSBuildRuntimeType)' != 'Core'` | the same, for `AspNetCompiler` |
| any non-empty `PreBuildEvent`/`PostBuildEvent` | `'$(OS)' == 'Windows_NT'` | Visual Studio runs build events as batch files; elsewhere MSBuild hands them to `/bin/sh` |
| `Exec` in a target, whose command reads as cmd.exe's (`.exe`, `.bat`, `.cmd`, `xcopy`, `copy`, `del`, `%VAR%`) | `'$(OS)' == 'Windows_NT'` | the same; an `Exec` that runs `dotnet`, `npm`, or `git` is left alone |
| `RestorePackages` = `true` | `'$(OS)' == 'Windows_NT'` | a NuGet 2 `.nuget/NuGet.targets` runs `NuGet.exe`, through Mono elsewhere |
| `MSBuildExtensionsPath`, `...32`, `...64` | `'$(OS)' == 'Windows_NT'` | pointing it anywhere hides `Microsoft.Common.props`, and `Directory.Build.props` with it (`OFR0122`) |

A setting whose own condition, or an ancestor's (`PropertyGroup`, `Target`,
`When`), already mentions `$(OS)`, `IsOSPlatform`, `$(MSBuildRuntimeType)`, or
`$(OfframpCompileOnly)` is the author's choice and stays as it is. An existing
condition is kept inside the guard (`'$(OS)' == 'Windows_NT' And (old)`). The
edit is a text insertion into the start tag: every other byte, the encoding,
byte order mark, and line endings stay; a file that is not valid UTF-8 or
UTF-16 (a legacy code page) is never rewritten. A second run finds every
setting conditioned and changes nothing. As before, `--fix` shows diffs and
writes only with `--apply`.

A new doctor check, `plain-build` ("Builds without Offramp"), after
`windows-only-build-steps`, reads the files, not the model, and warns when a
plain `dotnet build` would do less than Offramp's build: a setting without its
condition (`OFR0019`, one per setting, with file and line), and each thing only
Offramp's builds supply (`OFR0026`): `verify.properties`, `packages.config`
restore, a Web Site project left out of the solution. When it passes, it lists
the conditioned settings a build outside Windows skips.

## Alternatives considered

- Moving the block's switches into `Directory.Build.targets`: too late for
  sgen (the common targets have read the property by then) and for a legacy
  project's trailing `PostBuildEvent`.
- Global properties in a checked-in `Directory.Build.rsp`: `dotnet build` reads
  it, but it cannot be conditioned per OS, so Windows builds would lose the
  steps too.
- `'$(OS)' == 'Windows_NT'` for sgen and `MvcBuildViews` as well, as the
  conditions for the rest: Windows would still fail under `dotnet build`, and
  the point is that `dotnet build` does the same on every OS. Visual Studio and
  MSBuild.exe report `Full`, older MSBuild versions report nothing; both keep
  the steps.
- Guarding only build events that look like cmd.exe commands, as `Exec` is
  guarded: build events are batch files by definition, and a cmd.exe line
  without a marker (`if exist ...`) would still fail on `/bin/sh`.
- Letting `verify.properties` carry it (the old state): only Offramp's builds
  get global properties.

## Consequences

- After `doctor --fix --apply`, a plain `dotnet build` outside Windows does
  what Offramp's scan build does, and the corpus harnesses no longer pass
  `PostBuildEvent=""`. On the `windows-only-settings` fixture, a plain `dotnet
  build` fails with only the block (MSB3474) and succeeds after the fix, in
  Debug and Release.
- A build outside Windows is the same compile, not the same output: it skips
  sgen's `*.XmlSerializers.dll`, precompiled views, and what build events copy
  or merge (NHibernate's Release build runs ILRepack from one). `doctor` lists
  each skipped setting, so a developer knows what only a Windows build proves.
- Some differences remain that no condition can remove, and `plain-build`
  names them: `packages.config` projects need their packages folder filled
  (`offramp scan`, or `nuget restore` on Windows; ADR 0064 has `dotnet restore`
  fill it), and a Web Site project stops `dotnet build` of the whole solution. Steps that
  compile needs (COM references, EDMX, T4 at build time) stay in
  `windows-only-build-steps`.
- `csproj modernize` keeps the conditions: it copies property conditions and
  build events' own conditions into the converted project.
- `audit api-compat --baseline` gives the baseline's work tree the conditions
  along with the block, as ADR 0058 gives it the block: the revision predates
  them, and the working tree's build has them.
