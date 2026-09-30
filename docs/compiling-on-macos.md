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
| `sgen` (`GenerateSerializationAssemblies=On`) | loads the built assembly under the Framework runtime to pre-generate `XmlSerializer` code; .NET's MSBuild has no `SGen` task, on Windows either (MSB3474) | the compile-only block turns it off, and `doctor --fix` conditions it where a project sets it ([below](#settings-in-project-files)); on modern .NET use `Microsoft.XmlSerializer.Generator` or drop it |
| COM references, `EmbedInteropTypes` | type library importer is Windows-only | reference the interop assembly as a file, or exclude the project from Mac builds |
| Entity Framework 6 EDMX embedding (`EntityDeploy`) | build task ships with Visual Studio | use code-first mappings, or the compiler-log route |
| T4 templates at build time, Microsoft Fakes | Visual Studio-only targets | run on Windows, or check generated output in |
| SSDT `.sqlproj` | Windows-only targets | `MSBuild.Sdk.SqlProj` is the cross-platform replacement |
| Pre/post-build events, and `Exec` commands written for cmd.exe | Visual Studio runs build events as batch files; elsewhere MSBuild hands them to `/bin/sh` | `doctor --fix` conditions them on `'$(OS)' == 'Windows_NT'` ([below](#settings-in-project-files)) |
| WPF / WinForms on modern targets | need Windows targeting packs | set `EnableWindowsTargeting=true`; they then build on macOS |
| ASP.NET (System.Web) web application targets, including every `MSBuild.SDK.SystemWeb` project | `$(VSToolsPath)/WebApplications/Microsoft.WebApplication.targets` ships with Visual Studio only; evaluation stops with MSB4019 | the compile-only block takes them from a package (below) |
| Precompiled MVC views (`MvcBuildViews=true`, on by default in Release for `MSBuild.SDK.SystemWeb`) | `AspNetCompiler` exists only in .NET Framework's MSBuild (MSB4803) | the compile-only block turns the default off, and `doctor --fix` conditions it where a project sets it |
| `packages.config` packages | `dotnet restore` skips `packages.config`, and `NuGet.exe` needs Mono | `offramp scan` restores them into `packages/` (below) |
| Paths in another letter case than the file on disk (Linux only) | Windows and macOS file systems ignore case; Linux does not (MSB4019, CS2001, MSB3030, MSB3554) | rename the reference or the file; `scan` names every one in each project at once (`OFR0117`) |
| `CodeTaskFactory` inline tasks, as in `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` | only .NET Framework's MSBuild has the factory (MSB4801) | redefine the targets that use it (below); `OFR0118` |
| `Microsoft.Bcl.Build`'s binding redirects (`EnsureBindingRedirects`) | the task is built against .NET Framework's MSBuild 4.0 (MSB4062) | the compile-only block sets `SkipEnsureBindingRedirects=true`; `OFR0124` |
| MSTest v1 (`Microsoft.VisualStudio.QualityTools.UnitTestFramework`) | the assembly ships with Visual Studio only (CS0246, CS0234) | reference `MSTest.TestFramework` ([below](#mstest-v1)); `OFR0125` |
| ASP.NET Web Site projects (a folder in the solution, no project file) | `AspNetCompiler` exists only in .NET Framework's MSBuild; the whole solution stops (MSB4249) | `scan` builds the solution without them; `OFR0126` |
| Non-string `.resx` resources (images, icons) | .NET's MSBuild embeds them only preserialized (MSB3822, MSB3823) | `GenerateResourceUsePreserializedResources=true` and `System.Resources.Extensions`, as a DLL reference in legacy projects ([below](#non-string-resources)); `OFR0119` |

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
<!-- Microsoft.Bcl.Build on macOS/Linux: its binding-redirect task needs .NET Framework's MSBuild, and compile-only builds need no redirects (added by offramp doctor). -->
<PropertyGroup Condition="!$([MSBuild]::IsOSPlatform('Windows'))">
  <SkipEnsureBindingRedirects>true</SkipEnsureBindingRedirects>
</PropertyGroup>
```

MSBuild imports `Directory.Build.props` before the project's own properties,
so a setting the project file makes itself wins over the block; `doctor --fix`
conditions those where they are ([Settings in project
files](#settings-in-project-files)). Guard anything else that needs Windows with
`Condition="'$(OS)' == 'Windows_NT'"`. Offramp's own builds also pass the
properties from `offramp.yml` (`verify.properties`) on the command line, but a
plain `dotnet build` does not get them, so keep that list empty once the files
carry the conditions; `doctor` warns while it is not (`OFR0026`).

If your `Directory.Build.props` has sections from an earlier Offramp,
`offramp doctor --fix` adds only the ones it lacks.

MSBuild imports `Directory.Build.props` from `Microsoft.Common.props`, so the
block reaches only projects that import that file. Outside Windows, `scan`
checks each legacy project's evaluation after the build, and names each one the
block did not reach (`OFR0122`), with the cause and the file to change:

- A shared `.props` or `.settings` file sets `MSBuildExtensionsPath` (Open Live
  Writer's `writer.build.settings` does, "to prevent accidental pickup of
  local-machine scripts"), so `Microsoft.Common.props` is never imported.
  Condition that line on `'$(OS)' == 'Windows_NT'`.
- `ImportDirectoryBuildProps` is `false`.
- A nearer `Directory.Build.props` does not import the root one. Add
  `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`
  to it.

Such a project fails with MSB3644 (no reference assemblies) otherwise.
Importing the block later, from `Directory.Build.targets`, does not help: the
restore then fails, because `MSBuildProjectExtensionsPath` is empty.

The last section turns off `Microsoft.Bcl.Build`'s `EnsureBindingRedirects`
task, with the package's own switch. The package came with `Microsoft.Net.Http`,
`Microsoft.Bcl`, and `Microsoft.Bcl.Async` in .NET Framework 4.0 and 4.5
codebases, and its task is built against .NET Framework's MSBuild, so .NET's
MSBuild cannot load it (MSB4062). Compile-only builds need no binding
redirects; on Windows the task still writes the ones the application needs.
`scan` reports a project that imports the package's targets without the switch
as `OFR0124`.

## Settings in project files

The compile-only block cannot switch off what a project file sets itself:
MSBuild reads `Directory.Build.props` first, and the project's own
`<GenerateSerializationAssemblies>On</GenerateSerializationAssemblies>` or
`<MvcBuildViews>true</MvcBuildViews>` wins. `Directory.Build.targets` comes too
late (the common targets read the sgen setting before it, and a legacy project
defines its `PostBuildEvent` after them). So `offramp doctor --fix` puts a
condition on each such setting where it is written: in the project files, the
`Directory.Build.props`/`.targets` files above them, and the repository files
they import (`docs/decisions/0063-condition-windows-only-settings-in-project-files.md`):

```xml
<GenerateSerializationAssemblies Condition="'$(MSBuildRuntimeType)' != 'Core'">On</GenerateSerializationAssemblies>
<MvcBuildViews Condition="'$(MSBuildRuntimeType)' != 'Core'">true</MvcBuildViews>
<PostBuildEvent Condition="'$(OS)' == 'Windows_NT'">xcopy /y "$(TargetDir)*.dll" "$(SolutionDir)deploy\"</PostBuildEvent>
<Exec Condition="'$(OS)' == 'Windows_NT'" Command="copy /y &quot;$(TargetPath)&quot; ..\drop\" />
<RestorePackages Condition="'$(OS)' == 'Windows_NT'">true</RestorePackages>
<MSBuildExtensionsPath Condition="'$(OS)' == 'Windows_NT'">$(MSBuildToolsPath)\MsBuildExtensions</MSBuildExtensionsPath>
```

sgen and `AspNetCompiler` are kept for .NET Framework's MSBuild (Visual Studio,
Build Tools), which is what they need: `dotnet build` cannot run them on
Windows either. Build events, cmd.exe commands (`.exe`, `.bat`, `xcopy`,
`copy`, `del`, `%VAR%`), NuGet 2's `RestorePackages`, and an
`MSBuildExtensionsPath` override are kept for Windows. An `Exec` that runs
`dotnet`, `npm`, or `git` is left alone, as is anything already conditioned on
`$(OS)`, `IsOSPlatform`, `$(MSBuildRuntimeType)`, or `$(OfframpCompileOnly)`.
An existing condition is kept inside the new one. Visual Studio's build on
Windows is unchanged.

The result is a repository that builds with a plain `dotnet build` outside
Windows, for anyone, with or without Offramp. It is the same compile as on
Windows, not the same output: the skipped steps do not run, so there is no
`*.XmlSerializers.dll`, no precompiled views, and nothing a build event copies
or merges. `doctor`'s **Builds without Offramp** check lists every skipped
setting by file and line, so you know what only a Windows build proves, and
warns about what still separates a plain build from Offramp's:

- a Windows-only setting without its condition (`OFR0019`): run
  `offramp doctor --fix --apply`;
- `verify.properties` in `offramp.yml` (`OFR0026`): Offramp's builds pass them,
  a plain build does not;
- `packages.config` projects (`OFR0026`): `dotnet restore` does not restore
  them, so a fresh clone needs its packages folder filled first (`offramp
  scan` does it, as does `nuget restore` on Windows), until `offramp csproj
  modernize` moves them to `PackageReference`;
- an ASP.NET Web Site project (`OFR0026`, `OFR0126`): it stops `dotnet build`
  of the whole solution, so `scan` builds a filter without it.

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
  for `Package.targets` on disk. `scan` checks every `Import`, `Compile`, and
  `EmbeddedResource` path of each project, the `None` and `Content` items it
  copies to the output, and the files that `.resx` files reference
  (`ResXFileRef`, MSB3554), after the build and
  whatever it got to, so one scan names them all; the diagnostic's
  `data.paths` lists them. Offramp does not rename anything.
- **Missing source files** (`OFR0123`): a `Compile` item whose file does not
  exist in any letter case, and that no target of the build writes. When git ignores the path, the repository's own
  build script (NAnt, psake, Cake, FAKE, GitVersion) generates it, typically a
  shared `SharedAssemblyInfo.cs`: run that step once, then scan again.
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
  `XCOPY` in a `PostBuild` target: `offramp doctor --fix --apply` conditions
  them on `'$(OS)' == 'Windows_NT'` ([Settings in project
  files](#settings-in-project-files)); condition one it does not recognize the
  same way. When the
  command runs a program the solution itself builds (`$(OutDir)Tool.exe`), it
  is a build-time generator, and `scan` names its target and the files it
  writes (its `Outputs`). Guarding that target leaves those files missing, and
  the build fails later instead (CS1566 for a missing resource), so write them
  once: a generator written in C# often runs on .NET unchanged. Open Live
  Writer's `MarketXmlGenerator.cs` does.
- **MSTest v1** (`OFR0125`): a reference to
  `Microsoft.VisualStudio.QualityTools.UnitTestFramework` without a `HintPath`,
  which only Visual Studio installs; see [MSTest v1](#mstest-v1).
- **ASP.NET Web Site projects** (`OFR0126`): a folder the solution lists
  without a project file (type `{E24C65DC-7377-472B-9ABA-BC803B73C61A}`), which
  the solution build precompiles with `AspNetCompiler`. `dotnet build` stops the
  whole solution on it (MSB4249), before any project builds, so `scan` builds a
  solution filter of the other projects (`.offramp/scan.slnf`) instead. The
  site stays out of the model, with or without `--msbuild`: it has no project
  file. To migrate it, convert it to a web application project first.
- **Non-string resources** (`OFR0119`). `scan` reads each project's `.resx`
  files and names the ones with images, icons, type-converted values, or
  serialized objects (strings and byte arrays embed as they are). .NET's MSBuild
  embeds those only as preserialized resources, which need
  `GenerateResourceUsePreserializedResources=true` and a reference to
  `System.Resources.Extensions`; see [Non-string resources](#non-string-resources).

### Non-string resources

An SDK-style project takes the property and a `PackageReference` to
`System.Resources.Extensions`. A legacy project restored the `PackageReference`
way (the legacy section) gets no compile references from packages under the
.NET SDK, so the package is restored but not referenced, and the build still
fails with MSB3822. Reference the DLL from the package folder instead. For
compile-only builds, in `Directory.Build.props` after the block:

```xml
<!-- Non-string .resx resources in legacy projects on macOS/Linux (offramp scan: OFR0119). -->
<PropertyGroup Condition="'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true'">
  <GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>
</PropertyGroup>
<ItemGroup Condition="'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true' And $([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion.TrimStart('v')), '4.6.1'))">
  <PackageReference Include="System.Resources.Extensions" Version="6.0.0" IsImplicitlyDefined="true" PrivateAssets="all" />
</ItemGroup>
<Target Name="AddSystemResourcesExtensions" BeforeTargets="ResolveAssemblyReferences" Condition="'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true' And $([MSBuild]::VersionGreaterThanOrEquals($(TargetFrameworkVersion.TrimStart('v')), '4.6.1'))">
  <ItemGroup>
    <Reference Include="$(NuGetPackageRoot)system.resources.extensions/6.0.0/lib/net461/System.Resources.Extensions.dll" />
  </ItemGroup>
</Target>
```

The reference is added in a target, not as an item of the project, so the
workspace model does not record a reference the project does not have, and
`deps resolve-dlls` leaves it alone. Choose the version by target framework:

| Target framework | `System.Resources.Extensions` | DLL in the package |
|---|---|---|
| .NET Framework 4.6.1 | 6.0.0 (8.0.0 has no `net461` build) | `lib/net461/` |
| .NET Framework 4.6.2 and later | 8.0.0, or 6.0.0 as above | `lib/net462/` (8.0.0) |
| .NET Framework 4.6 and earlier | none: no version supports them | build these projects on Windows, or use the compiler-log route |

On Windows, .NET Framework's MSBuild embeds these resources without either
change. If you set the property for every build instead, the .NET Framework
application needs `System.Resources.Extensions` at run time.

### MSTest v1

MSTest v2's `MSTest.TestFramework` package has the same namespace
(`Microsoft.VisualStudio.TestTools.UnitTesting`), so moving a test project to it
is the real fix, and a step on the way to modern .NET. Until then, a legacy test
project compiles outside Windows against the package's DLLs. As for resources,
the package is restored the `PackageReference` way and referenced from a target,
here for the test projects `scan` named (`MyApp.Tests` below):

```xml
<!-- MSTest v1 test projects on macOS/Linux (offramp scan: OFR0125). -->
<ItemGroup Condition="'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true' And '$(MSBuildProjectName)' == 'MyApp.Tests'">
  <PackageReference Include="MSTest.TestFramework" Version="1.4.0" IsImplicitlyDefined="true" PrivateAssets="all" />
</ItemGroup>
<Target Name="AddMSTestFramework" BeforeTargets="ResolveAssemblyReferences" Condition="'$(OfframpCompileOnly)' == 'true' And '$(UsingMicrosoftNETSdk)' != 'true' And '$(MSBuildProjectName)' == 'MyApp.Tests'">
  <ItemGroup>
    <Reference Include="$(NuGetPackageRoot)mstest.testframework/1.4.0/lib/net45/Microsoft.VisualStudio.TestPlatform.TestFramework.dll" />
    <Reference Include="$(NuGetPackageRoot)mstest.testframework/1.4.0/lib/net45/Microsoft.VisualStudio.TestPlatform.TestFramework.Extensions.dll" />
  </ItemGroup>
</Target>
```

The v1 reference stays unresolved (a warning, MSB3245), and the tests compile
against the replacement. Coded UI tests (`Microsoft.VisualStudio.QualityTools.CodedUITestFramework`)
have no replacement.

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
3. `offramp doctor`'s **Builds without Offramp** check passes, and a plain
   `dotnet build` of your solution succeeds with the conditional above and the
   conditions `doctor --fix` added; the check lists what that build skips.
4. Your IDE shows no red squiggles in a `net48` project.
