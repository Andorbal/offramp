# Compiling .NET Framework code on macOS and Linux

You can compile, analyze, and get IntelliSense for `net48` projects on a Mac.
You cannot run the result, and a few build steps need Windows. This page
explains why, how to set it up, and what to do about the exceptions. Offramp's
`doctor` command checks all of it for you.

## Why it works

The C# compiler needs only *reference assemblies*: metadata-only copies of the
framework that contain every public type and signature and no method bodies.
Microsoft ships them for every .NET Framework version as the NuGet package
`Microsoft.NETFramework.ReferenceAssemblies`. Since the .NET Core 3.0 SDK, any
SDK-style project that targets a `net4x` framework references that package
automatically when the Windows reference assemblies are not installed
(the property behind it is `AutomaticallyUseReferenceAssemblyPackages`).

So for SDK-style projects:

```bash
dotnet build src/Foo/Foo.csproj          # net48 target compiles on macOS
```

Rider and VS Code with C# Dev Kit drive the same SDK build for IntelliSense,
and you switch the view between `net48` and `net10.0` in a dual-target
project with the target framework picker.

Legacy (non-SDK) csproj files need Windows MSBuild to evaluate. Convert them
first (`offramp csproj modernize`) or use the compiler-log route below.

## What does not work

| Thing | Why | What to do |
|---|---|---|
| Running `net48` output, including tests | needs the Framework runtime | run the modern target of dual-target tests locally; leave `net48` execution to Windows CI |
| `sgen` (`GenerateSerializationAssemblies=On`) | loads the built assembly under the Framework runtime to pre-generate `XmlSerializer` code | turn it off for non-Windows builds (below); on modern .NET use `Microsoft.XmlSerializer.Generator` or drop it |
| COM references, `EmbedInteropTypes` | type library importer is Windows-only | reference the interop assembly as a file, or exclude the project from Mac builds |
| Entity Framework 6 EDMX embedding (`EntityDeploy`) | build task ships with Visual Studio | use code-first mappings, or the compiler-log route |
| T4 templates at build time, Microsoft Fakes | Visual Studio-only targets | run on Windows, or check generated output in |
| SSDT `.sqlproj` | Windows-only targets | `MSBuild.Sdk.SqlProj` is the cross-platform replacement |
| Pre/post-build events calling Windows executables | obvious | guard them with a condition on `$(OS)` |
| WPF / WinForms on modern targets | need Windows targeting packs | set `EnableWindowsTargeting=true`; they then build on macOS |

## The compile-only conditional

Put this in the repository's root `Directory.Build.props` (`offramp doctor
--fix` writes it after showing you the diff):

```xml
<!-- Compile-only builds on macOS/Linux: skip steps that need Windows. -->
<PropertyGroup Condition="!$([MSBuild]::IsOSPlatform('Windows'))">
  <GenerateSerializationAssemblies>Off</GenerateSerializationAssemblies>
  <EnableWindowsTargeting>true</EnableWindowsTargeting>
  <OfframpCompileOnly>true</OfframpCompileOnly>
</PropertyGroup>
```

Then guard anything else that needs Windows with
`Condition="'$(OfframpCompileOnly)' != 'true'"`. Offramp's own verification
builds pass the same properties from `offramp.yml` (`verify.properties`), so
verification works even before you edit any props file.

## The compiler-log fallback

When a project cannot build on the Mac at all, capture the build where it
works and analyze it where you are. A *compiler log* (`.complog`, from the
`complog` tool, which is the `Basic.CompilerLog` project) packs every C#
compiler invocation from an MSBuild binary log together with its sources and
reference assemblies into one portable file.

On the Windows build agent:

```powershell
dotnet tool install -g complog
dotnet build Monolith.sln -bl:msbuild.binlog
complog create msbuild.binlog -o monolith.complog
```

On the Mac:

```bash
offramp scan --complog monolith.complog
```

Everything that reads the workspace model, including `move plan`'s trial
compilations, then works from that snapshot. Remember that a snapshot goes
stale as code changes; the native `dotnet build` route is the everyday path
and the compiler log is for the projects that need it. `offramp doctor` tells
you which projects those are.

## Checklist

1. `dotnet --list-sdks` shows an SDK that can target your `--target`.
2. `offramp doctor` is green, or lists exactly which projects need the
   compiler-log route and why.
3. `dotnet build` of your solution filter succeeds with the conditional above.
4. Your IDE shows no red squiggles in a `net48` project.
