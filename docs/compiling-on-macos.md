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

Legacy (non-SDK) csproj files get neither the package nor the rest of what
Visual Studio installs. The compile-only block's legacy section and
`offramp scan` supply what they need; see
[Legacy (non-SDK) projects](#legacy-non-sdk-projects) below.

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
| ASP.NET (System.Web) web application targets, including every `MSBuild.SDK.SystemWeb` project | `$(VSToolsPath)/WebApplications/Microsoft.WebApplication.targets` ships with Visual Studio only; evaluation stops with MSB4019 | the compile-only block takes them from a package (below) |
| Precompiled MVC views (`MvcBuildViews=true`, on by default in Release for `MSBuild.SDK.SystemWeb`) | `AspNetCompiler` exists only in .NET Framework's MSBuild (MSB4803) | the compile-only block turns it off |
| `packages.config` packages | `dotnet restore` skips `packages.config`, and `NuGet.exe` needs Mono | `offramp scan` restores them into `packages/` (below) |
| Paths in another letter case than the file on disk (Linux only) | Windows and macOS file systems ignore case; Linux does not (MSB4019, CS2001, MSB3030) | rename the reference or the file; `scan` names each project (`OFR0117`) |
| `CodeTaskFactory` inline tasks, as in `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` | only .NET Framework's MSBuild has the factory (MSB4801) | redefine the targets that use it (below); `OFR0118` |
| Non-string `.resx` resources (images, icons) | .NET's MSBuild embeds them only preserialized (MSB3822, MSB3823) | `GenerateResourceUsePreserializedResources=true` and the `System.Resources.Extensions` package; `OFR0119` |

## The compile-only conditional

Put this in the repository's root `Directory.Build.props` (`offramp doctor
--fix` shows you the diff; `offramp doctor --fix --apply` writes it):

```xml
<!-- Compile-only builds on macOS/Linux: skip steps that need Windows (added by offramp doctor). -->
<PropertyGroup Condition="!$([MSBuild]::IsOSPlatform('Windows'))">
  <GenerateSerializationAssemblies>Off</GenerateSerializationAssemblies>
  <EnableWindowsTargeting>true</EnableWindowsTargeting>
  <OfframpCompileOnly>true</OfframpCompileOnly>
</PropertyGroup>
<!-- ASP.NET (System.Web) projects on macOS/Linux: the Visual Studio web targets come from a package; views are not precompiled and Web Deploy publishing is left out (added by offramp doctor). -->
<PropertyGroup Condition="!$([MSBuild]::IsOSPlatform('Windows'))">
  <MvcBuildViews>false</MvcBuildViews>
  <AspNetTargetsPath>$(MSBuildThisFileDirectory)</AspNetTargetsPath>
</PropertyGroup>
<ItemGroup Condition="!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' == 'true' And '$(VSToolsPath)' != ''">
  <PackageReference Include="MSBuild.Microsoft.VisualStudio.Web.targets" Version="14.0.0.3" IsImplicitlyDefined="true" PrivateAssets="all" />
</ItemGroup>
<!-- Legacy (non-SDK) projects on macOS/Linux: the .NET Framework reference assemblies and the web targets come from packages, as for SDK-style projects; offramp scan restores packages.config (added by offramp doctor). -->
<PropertyGroup Condition="!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' != 'true'">
  <RestoreProjectStyle>PackageReference</RestoreProjectStyle>
  <OfframpLegacyPackages>true</OfframpLegacyPackages>
</PropertyGroup>
<ItemGroup Condition="!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' != 'true'">
  <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" IsImplicitlyDefined="true" PrivateAssets="all" />
  <PackageReference Include="MSBuild.Microsoft.VisualStudio.Web.targets" Version="14.0.0.3" IsImplicitlyDefined="true" PrivateAssets="all" />
  <Reference Include="Microsoft.VisualBasic" Condition="'$(MSBuildProjectExtension)' == '.vbproj'" />
</ItemGroup>
<Target Name="OfframpLegacyVisualBasicRuntime" BeforeTargets="CoreCompile" Condition="!$([MSBuild]::IsOSPlatform('Windows')) And '$(UsingMicrosoftNETSdk)' != 'true' And '$(Language)' == 'VB' And '$(VBRuntime)' == ''">
  <PropertyGroup>
    <VBRuntime Condition="'%(ReferencePath.FileName)' == 'Microsoft.VisualBasic'">%(ReferencePath.Identity)</VBRuntime>
    <DisableSdkPath>true</DisableSdkPath>
  </PropertyGroup>
</Target>
```

Then guard anything else that needs Windows with
`Condition="'$(OfframpCompileOnly)' != 'true'"`. Offramp's own verification
builds pass the properties from `offramp.yml` (`verify.properties`), so
verification of the first section works even before you edit any props file;
the ASP.NET section needs the file, because it adds a package.

If your `Directory.Build.props` has sections from an earlier Offramp,
`offramp doctor --fix` adds only the ones it lacks.

### ASP.NET (System.Web) projects

`MSBuild.SDK.SystemWeb` ends its `Sdk.targets` with an unconditional
`<Import Project="$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets" />`,
and legacy web application projects import the same file. Only Visual Studio
installs it, so outside Windows the build stops before compiling anything:

```text
error MSB4019: The imported project ".../Microsoft/VisualStudio/v17.0/WebApplications/Microsoft.WebApplication.targets" was not found.
```

The block's ASP.NET section handles it, only outside Windows and only for
SDK-style projects that define `VSToolsPath`:

- The `MSBuild.Microsoft.VisualStudio.Web.targets` package carries copies of
  those targets, and its props point `VSToolsPath` at them. The reference is
  marked implicit, so it needs no `PackageVersion` under central package
  management and Offramp leaves it out of package analysis.
- `MvcBuildViews` is turned off: view precompilation runs `AspNetCompiler`,
  which .NET's MSBuild does not have.
- `AspNetTargetsPath` points at a folder without the Web Deploy targets, so
  they are not imported. Publishing needs Windows anyway, and those targets add
  a step to `Clean` (and so to rebuilds) that .NET's MSBuild cannot load.

Build with the .NET SDK (`dotnet build`, or the .NET SDK's MSBuild in Rider's
toolset settings). If the error names a path under
`/Library/Frameworks/Mono.framework/`, Mono's MSBuild did the build; Mono is
no longer maintained, and Offramp neither uses nor supports it.

`offramp scan` reports these projects as `OFR0116`, and `offramp doctor` lists
them under Windows-only build steps.

### Legacy (non-SDK) projects

Most .NET Framework codebases are legacy projects on `packages.config`
(DotNetNuke 9.13: 64 of 71). Outside Windows they lack four things, and
Offramp supplies each (`docs/decisions/0037-legacy-projects-outside-windows.md`):

- **Reference assemblies.** The legacy section restores legacy projects the
  `PackageReference` way (`RestoreProjectStyle`), so they take
  `Microsoft.NETFramework.ReferenceAssemblies` from a package as SDK-style
  projects do. Their `packages.config` stays as it is; `dotnet restore` just
  does not read it.
- **The web targets**, from the same package the ASP.NET section uses. Its
  props set `VSToolsPath` only when it is empty, which is the condition legacy
  web projects use for their own default.
- **The Visual Basic runtime.** The reference assemblies package wires it for
  SDK-style projects only; the legacy section references `Microsoft.VisualBasic`
  and passes it to the compiler the way the SDK does.
- **The `packages.config` packages.** `offramp scan` restores them into the
  solution's packages folder (`repositoryPath` from `nuget.config`, else
  `packages/` beside the solution) as `nuget restore` lays it out
  (`packages/<Id>.<Version>/`), from the NuGet global packages folder when it
  has them and otherwise from the feeds in `nuget.config`. It never overwrites a
  folder. It reports what it wrote (`OFR0106`) and what it could not find
  (`OFR0105`). Its builds, and Offramp's verification builds, pass
  `RestorePackages=false`, so a `.nuget/NuGet.targets` from the NuGet 2 era
  does not try to run `NuGet.exe` through Mono (MSB3073).

`offramp doctor` warns when the workspace has legacy projects and the file has
no legacy section (`OFR0018`).

What is left is the repository's own, and `scan` names each case with the file
to change:

- **Letter case** (`OFR0117`, Linux only): a reference spelled `Package.Targets`
  for `Package.targets` on disk. Offramp does not rename anything.
- **Inline tasks** (`OFR0118`): `Microsoft.CodeDom.Providers.DotNetCompilerPlatform`
  runs `CodeTaskFactory` tasks from its build targets. Redefine the two targets
  that call them as empty ones, in a file only compile-only builds import. In
  `Directory.Build.targets` (which MSBuild imports after the package's props):

  ```xml
  <Import Project="compile-only.targets" Condition="'$(OfframpCompileOnly)' == 'true'" />
  ```

  and in `compile-only.targets`:

  ```xml
  <Project>
    <Target Name="KillVBCSCompilerBeforeCopy" />
    <Target Name="KillVBCSCompilerBeforeClean" />
  </Project>
  ```

- **Build events and `Exec` commands written for cmd.exe** (`OFR0115`), such as
  `XCOPY` in a `PostBuild` target: add
  `Condition="'$(OfframpCompileOnly)' != 'true'"` to the target.
- **Non-string resources** (`OFR0119`).

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

When some projects build only with Visual Studio's MSBuild (sgen, COM
references), capture the log with `msbuild Monolith.sln -restore -t:Rebuild
-bl:msbuild.binlog` from a Developer Command Prompt instead, or run Offramp on
that machine with `offramp scan --msbuild`.

On the Mac, with both logs (restore first so the package graph is complete):

```bash
dotnet restore Monolith.sln
offramp scan --binlog msbuild.binlog --complog monolith.complog
```

The binary log gives the evaluation (packages, SDK, kinds, Windows-only steps)
and the compiler log the compilations; paths from the Windows machine are
mapped onto your checkout. `offramp scan --complog monolith.complog` alone
also works but gives a reduced model without packages and project metadata
(`OFR0103`).

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
