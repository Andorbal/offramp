# Field test: DotNetNuke Platform 9.13.10

Offramp was run against a real, large .NET Framework codebase to find the gaps that the fixtures
do not show. This page records what happened, ranks what needs fixing, and gives the exact
workarounds that were needed, so each item can become an issue.

- **Codebase:** [dnnsoftware/Dnn.Platform](https://github.com/dnnsoftware/Dnn.Platform) at tag
  `v9.13.10`, the last 9.x release (.NET Framework 4.7.2, ASP.NET Web Forms, MVC and Web API).
  `DNN_Platform.sln` has 71 projects and about 533,000 lines of C#. 64 projects target .NET
  Framework only, 64 still use `packages.config`, and 64 are legacy (non-SDK) projects. 19 are web
  application projects, and one is a `.vbproj`.
- **Machine:** Ubuntu 24.04 in a container with 4 cores, the .NET 10.0.112 SDK from the Ubuntu
  archive, and no Mono. `dotnet-install.sh` could not be used behind the proxy.
- **Offramp:** `main` at `f118fe9` (v0.16.0), built locally and run as
  `dotnet src/Offramp.Cli/bin/Release/net10.0/offramp.dll`.
- **Method:** each command was run the way a new user would, following the README and
  `docs/compiling-on-macos.md`. Every result was checked against the DNN sources, and the root
  cause was traced in Offramp's code where it was not obvious.

## Summary

Out of the box, Offramp cannot analyze this codebase outside Windows. `scan`'s build fails within
20 seconds with 44 errors: 59 of 66 projects are partial and 6 are not loaded. `doctor --fix --apply`
does not help, because every part of the compile-only block that matters is limited to SDK-style
projects. Getting a clean build took about a dozen manual workarounds, all listed
[below](#appendix-what-it-took-to-build-dnn-on-linux). Most of them could be automated.

With those workarounds in place, `scan` succeeds: 71 projects, no diagnostics, 66 seconds. The
model is deterministic: two scans are byte-identical apart from `createdAt`. `graph`, `plan` and
`report` answer in under a second. Every command then runs to completion except one crash.
However, several commands give **wrong answers** on this codebase. The most serious are:

1. `audit api` blames the wrong API for about half of its target-compilation errors. It reports
   `System.Convert`, `System.Exception` and DNN's own types as "does not exist on the target".
2. `deps resolve-dlls` would rewrite 184 `packages.config` references to a **different package
   version** (Newtonsoft.Json 13.0.3 → 13.0.1, Castle.Core 5.1.1 → 5.0.0, LiteDB 5.0.19 → 5.0.20).
3. `redirects sync --prune` would delete live binding redirects (Newtonsoft.Json, BouncyCastle,
   SharpZipLib). It concludes they are unused because it ignores `packages.config`.
4. `audit dead-code` marks Web Forms code-behind classes as dead at high confidence, including
   `Default.aspx`'s page class.
5. `plan` and `report` count two netstandard2.0 projects that reference .NET Framework-only
   projects as done and portable.

The common thread is that `packages.config`, legacy csproj files, Web Forms markup, and Linux's
case-sensitive file system are barely covered by the fixtures. They are the norm in real .NET
Framework codebases.

## Status after the fixes

Nearly every finding was a general problem with `packages.config`, legacy projects, Web Forms,
or Linux, not something peculiar to DotNetNuke, so they were fixed on the branch that carries this
report. The numbers are from the same codebase after the fixes.

| Finding | Fix | On DotNetNuke now |
|---|---|---|
| P0 #1 `audit api` blames the wrong API | `dc4e8c7` | about 5,200 fewer false findings; 13 against `mscorlib` (was 1,254), 26 against DNN's own assemblies (was 4,085) |
| P0 #2 `deps resolve-dlls` wrong versions | `064ed1d` | 565 DLLs recognized as `packages.config` packages and left alone, 0 version changes, 6 blockers (was 172) |
| P0 #3 `redirects sync --prune` removes live redirects | `064ed1d`, this branch | live redirects kept (67 were called stale); a redirect down to an older version is `OFR1505`; an application with a partial model is skipped (`OFR1506`), since `--prune` on a fresh checkout's partial model removed live ones |
| P0 #4 `audit dead-code` and Web Forms | `dc4e8c7`, `1c03a8a` | markup, `AutoEventWireup` handlers, plugin discovery, and XML manifests count; removable lines 25,964 (was 40,163) |
| P0 #5 portable projects counted as done | `65de53f` | 3 projects `blocked`, each reference `OFR0121` |
| P1 #6 legacy projects outside Windows | `05fcc77` | `doctor --fix --apply` and `scan` supply the reference assemblies, web targets, and 64 `packages.config` packages. A fresh checkout still stops at DNN's own `XCOPY` targets and letter case (65 loaded, 6 not loaded, most partial), now each named; with those fixed, all 71 load and 4 are partial |
| P1 #7 `packages.config` invisible | `064ed1d` | `deps audit` sees 77 packages (was 23); 19 test projects (was 7) |
| P1 #8 letter case on Linux | `05fcc77` | named per project (`OFR0117`); fixing them stays the repository's job |
| P1 #9 .NET Framework-only MSBuild steps | `05fcc77` | `CodeTaskFactory` (`OFR0118`, with a documented fix that works), non-string resources (`OFR0119`), cmd.exe `Exec` (`OFR0115`) |
| P1 #10 `csproj modernize` defects | `3a2c01d` | attribute metadata and output folders kept; NuGet audit reported apart (`OFR4305`) |
| P1 #11 `move plan` on a large project | `4be7bff` | first error in the message; warning policy is `OFR2112`; inert `InternalsVisibleTo` items no longer planned |
| P1 #12 the corpus job cannot fail | this branch | `tests/Offramp.Corpus.Tests` runs this codebase |
| P2 polish | this branch | `doctor`'s global.json remedy and CPM noise, `OFR0101`'s reason, `OFR0130` counts per code, no ledger snapshot from a failed build, `OFR3012` for unaudited projects, one codemod notice per reason, relative paths in step evidence, `OFR1004`/`OFR1404` false positives, `audit dead-code` on unreadable files |

Since this report, `doctor --fix` also conditions the Windows-only settings the project files set
themselves (ADR 0063): the `PostBuild` targets' `XCOPY` commands and every `PostBuildEvent`, on
`'$(OS)' == 'Windows_NT'`, wherever the project defines them, including after the targets import,
which this report said only `-p:PostBuildEvent=` could reach.

Still open: the source-generator partials that make `move plan --all` plan nothing (P1 #11), the
build phase's missing heartbeat, `report` counting every web project as an application, and the
`move tests` naming and `Builder` heuristics (P2). The first two are general; the last two are
closer to DotNetNuke's layout.

## What worked well

- **Scan, once the build works:** deterministic, 66 seconds for the whole solution. It recognized
  web projects from their ProjectTypeGuids, and it detected the Windows-only build steps it knows
  about (`OFR0116`, `OFR0115`).
- **`plan`, `graph` and `report`:** each runs in under a second on 71 projects. The HTML graph
  stays readable at this size (zoom, search, focus) and shows HintPath-to-another-project edges,
  which is the right call for DNN. Neither HTML page loads anything from the network.
- **`deps resolve-dlls` OFR1401:** it correctly found 34 HintPath references into sibling
  projects' `bin/` folders (for example `..\Library\bin\DotNetNuke.dll`). That is a real DNN build
  smell.
- **`move plan` and `move apply`:** cycle detection (`OFR2001`), partial types moving together
  (`OFR2110`) and the co-move closure all behaved correctly. When a plan failed verification,
  `move apply` rolled back cleanly and the tree was identical afterwards.
- **`audit behavior`, `audit serialization`, `audit native`:** plausible and useful results.
  `HttpContext.Current` (628 uses), culture-sensitive string comparisons, `BinaryFormatter`,
  `JavaScriptSerializer`, and log4net's P/Invokes.
- **`codemod run --mod sqlclient`:** it warns that the package cannot be added to a
  `packages.config` project (OFR4505) and that Microsoft.Data.SqlClient encrypts connections by
  default (OFR4510). Both are exactly what a user needs to know.
- **`verify.properties`:** the properties flow through `scan`, `verify`, and `csproj modernize`'s
  verification, which is what made the workarounds possible.

## Findings, ranked

P0 means the command gives a wrong or dangerous answer. P1 means a gap that blocks real-world use.
P2 means polish.

### P0: wrong or dangerous results

#### 1. `audit api` reports the wrong missing API (TargetCompilationMatcher)

Roslyn reports a missing base type such as `UserControl` or `Page` at *every simple-name lookup*
inside the derived class, because name lookup walks the base-class chain.
`TargetCompilationMatcher.Missing` takes each error's location and resolves whatever symbol is
there in the recorded .NET Framework compilation. So `Convert.ToInt32(...)` inside a
`UserControl` subclass becomes "`System.Convert` (mscorlib) does not exist on the target", even
though the diagnostic says `'UserControl' could not be found`.

- A probe built on `TargetCompilationBuilder` checked every .NET Framework project. **11,661 of
  21,910** missing-name errors (53%) sit on a token that is not the missing name: `UserControl`
  ×7,888, `Page` ×1,945, `Control` ×939.
- In the report this shows up as 1,254 findings against `mscorlib` (System.EventArgs ×369,
  System.Exception ×202, System.Convert ×127) and 4,085 findings against DNN's own assemblies. For
  example: "`DotNetNuke.Entities.Modules.ModuleSettingsBase` (DotNetNuke) does not exist on the
  target".
- **Fix:** compare the name in the diagnostic with the identifier at the location. When they
  differ, attribute the finding to the named type (resolve it in the recorded compilation) and
  dedupe it per type and file, or drop it.
- **Regression fixture:** a class deriving from `System.Web.UI.UserControl` that calls
  `Convert.ToInt32` and throws `new Exception()`.

#### 2. `deps resolve-dlls` picks the wrong package version

HintPaths under `packages/<Id>.<Version>/` are matched to a package by assembly identity. Many
packages keep one AssemblyVersion across releases, so the lowest matching version wins. Of 397
package matches, 184 name a different version than the folder and `packages.config` do (12 more
differ only as `1.0.0.0` vs `1.0.0`):

| In use (packages.config) | Proposed PackageReference | Count |
|---|---|---|
| System.Buffers 4.5.1 | 4.5.0 | 41 |
| System.Threading.Tasks.Extensions 4.5.4 | 4.5.3 | 40 |
| Newtonsoft.Json 13.0.3 | 13.0.1 | 29 |
| Newtonsoft.Json.Bson 1.0.2 | 1.0.1 | 26 |
| System.Runtime.CompilerServices.Unsafe 4.5.3 | 4.5.2 | 22 |
| Castle.Core 5.1.1 | 5.0.0 | 18 |
| LiteDB 5.0.19 | 5.0.20 (an upgrade) | 1 |
| HtmlSanitizer 9.1.878-beta | 9.0.876 | 1 |

The `--apply` preview writes these versions, for example
`<PackageReference Include="Castle.Core" Version="5.0.0" />`.

**Fix:** when the HintPath is under the solution's packages folder, or the project's
`packages.config` lists the assembly's package, take the id and version from there.

#### 3. `redirects sync --prune` would remove live binding redirects

It raised 67 `OFR1504` warnings like "Redirects Newtonsoft.Json to 13.0.0.0, but no package in
the graph provides it; `--prune` removes it." Those packages are in use; they come from
`packages.config`, which the package graph does not read. Pruning would break the site at runtime
with `FileLoadException`. Web applications that carry binding redirects are nearly always
`packages.config` projects.

**Fix:** read `packages.config` into the graph (see [P1 #7](#7-packagesconfig-is-mostly-invisible)),
and until then refuse to prune for `packages.config` projects.

#### 4. `audit dead-code` treats Web Forms code-behind as dead

`.aspx`, `.ascx`, `.master`, `.ashx` and `.asmx` files are not read, so `Inherits="..."`
references are missed. 134 of the 571 classes that markup names are dead-code candidates, 21 of
them at **high** confidence:

- `DotNetNuke.Framework.DefaultPage`, behind `Default.aspx`, the page that serves every request;
- `DotNetNuke.Modules.Html.EditHtml` and `HtmlModule`;
- the DDRMenu views;
- `DNNConnect.CKEditorProvider.Browser.FileUploader`, an `.ashx` handler.

DNN's `.dnn` manifests (XML with another extension) also name controls and types.

**Fix:**
- Treat markup directives (`Inherits`, `CodeBehind`, `Class`, `<%@ Register %>`) as references.
- Treat `Global.asax` and `.dnn` manifests as string sources.

#### 5. Projects that only look portable are counted as done

- `DotNetNuke.DependencyInjection` (netstandard2.0) references `DotNetNuke.Instrumentation`
  (net472).
- `DotNetNuke.Maintenance` (netstandard2.0) references `DotNetNuke.Library` (net472).
- `DotNetNuke.ModulePipeline` (netstandard2.0;net472) references Library, Web.Mvc and Web.Razor
  unconditionally.

These build because the referenced legacy `packages.config` projects bypass NuGet's NU1201
compatibility check. `plan` puts all three in wave 0 with readiness `done` **while listing
framework-only blockers for them**. That contradicts the documented invariant that every project
comes after the framework-only projects it needs. `report` counts them as portable.

This is the "netstandard2.0 is not automatically portable" trap from `CLAUDE.md`.

**Fix:** a standard or dual project with framework-only dependencies in its closure should be
`blocked`, with a diagnostic naming the reference (a new code).

### P1: gaps that block real-world use

#### 6. Legacy csproj projects cannot be scanned outside Windows, and `doctor` does not say so

Each of these stopped the build on Linux. Offramp flagged only the first (as OFR0116), and its
`--fix` did not resolve it:

| Blocker | Error | Detected? | What worked |
|---|---|---|---|
| Legacy web projects import `$(VSToolsPath)/WebApplications/Microsoft.WebApplication.targets` | MSB4019 | yes (OFR0116), but `doctor --fix` puts the package only on SDK-style projects | global `VSToolsPath=<MSBuild.Microsoft.VisualStudio.Web.targets>/tools/VSToolsPath` |
| Reference assemblies for legacy projects (`AutomaticallyUseReferenceAssemblyPackages` is SDK-only) | MSB3644 | no; doctor's `reference-assemblies` check says "pass … the first net4x build downloads it" | global `TargetFrameworkRootPath` pointing at the `Microsoft.NETFramework.ReferenceAssemblies.netXX` packages |
| `dotnet restore` ignores `packages.config`, silently (`RestorePackagesConfig` does nothing in the .NET SDK) | CS0246 on every package type | no | fill `packages/<Id>.<Version>/` from the global packages folder |
| Solution-level `.nuget/NuGet.targets` (`RestorePackages=true`) runs `mono NuGet.exe` | MSB3073 | no | `-p:RestorePackages=false` |

Suggested scope for a "legacy csproj on macOS/Linux" milestone:

- **`doctor --fix`:** a legacy-project section that sets `TargetFrameworkRootPath` and
  `VSToolsPath`. Both packages would come from a restore Offramp runs itself.
- **`offramp restore`:** a restore for `packages.config` built on `NuGet.Protocol`. The folder name
  uses the version string from the nuspec (`Microsoft.Web.Infrastructure.1.0.0.0`), not the
  normalized one.
- **Detection:** `.nuget/NuGet.targets` should be reported as a Windows-only build step.
- **Tests:** the `legacy-csproj` fixture already fills `packages/` itself (ADR 0026), so the tests
  do by hand what the product does not.

#### 7. `packages.config` is mostly invisible

- **Model:** the packages in the model come from `PackageReference` only (23 packages from 7
  projects). The 64 `packages.config` projects show `packageReferences: []`.
- **`deps audit`:** it audits those 23 packages and none of DNN's real dependencies (WebFormsMvp,
  log4net, ASP.NET MVC, Web API and Web Pages, Microsoft.Extensions.* 2.1.1, LiteDB, and so on).
  Their HintPaths appear under `assemblyReferences` as `kind: file` with `mapping: null`, although
  every path is `packages/<Id>.<Version>/lib/...`.
- **`deps consolidate`:** it misses DNN's actual version skew (StyleCop.Analyzers 1.1.118 and
  1.2.0-beta.556; System.Runtime.CompilerServices.Unsafe 4.5.3 and 6.0.0; LiteDB 5.0.13 and
  5.0.19).
- **The documented answer does not reach everything:** that answer is `csproj modernize` first,
  but it does not convert web application projects (OFR4304). That leaves 19 of DNN's 71 projects
  without any package audit.
- **`audit api`:** `TargetCompilationBuilder.DirectPackages` returns nothing for these projects,
  so their net4x DLLs go into the net10.0 compilation unchanged. With Microsoft.Extensions.* also
  coming from the ASP.NET Core shared framework, this produces ambiguous calls (CS0121).
- **Test detection:** 11 NUnit projects are classified `library`, because detection uses only
  ProjectTypeGuids and `IsTestProject`. They are `DotNetNuke.Tests.Web`, `Tests.Web.Mvc`,
  `Tests.Urls`, `Tests.Data`, `Tests.Mail`, `Tests.AspNetCCP`, `Tests.Utilities` and all four
  `Dnn.PersonaBar.*.Tests`, 575 NUnit test and fixture attributes between them. A
  `packages.config` reference to NUnit, xunit or MSTest (or their adapters) should count.

**Fix:** read `packages.config` into the model as (id, version, targetFramework,
developmentDependency). Everything above then follows from the model.

#### 8. Case sensitivity on Linux

Windows and macOS file systems are case-insensitive by default, so none of this shows up there,
and the fixtures would not catch it on a Mac runner either:

- **`<Import>`:** 48 sites (`Microsoft.CSharp.Targets`, `Package.Targets`, `Module.Build`,
  `Skin.Build`, `SkinPackage.Targets`) fail with MSB4019.
- **`<Compile>`:** 99 items fail with CS2001. Most come from folders named `DTO` on disk but `Dto`
  in the project; the rest are single files such as `SMTPInfo.cs` vs `SmtpInfo.cs` and
  `XMLLayout.cs` vs `XmlLayout.cs`.
- **Output paths:** one project builds to `Bin/` while its packaging step copies from `bin/`, and
  25 projects generate `X.XML` documentation that post-build steps copy as `X.xml` (MSB3030).

This is cheap to detect statically before building: an import or item that exists only under a
case-insensitive match. It could also be recognized after the fact (MSB4019, CS2001 or MSB3030
where such a match exists). It deserves its own diagnostic and a `doctor` check, and it also
matters for `move` on case-insensitive checkouts.

#### 9. More MSBuild steps that need .NET Framework's MSBuild

None of these are detected or documented in `docs/compiling-on-macos.md`:

- **`CodeTaskFactory` (MSB4801):** comes from `Microsoft.CodeDom.Providers.DotNetCompilerPlatform`,
  which nearly every Web Forms site references. Its `KillVBCSCompilerBeforeClean` target runs on
  Clean, and `scan` always passes `--no-incremental`, which cleans.
- **Post-build `Exec` targets:** SDK-style projects use
  `<Target Name="PostBuild" AfterTargets="PostBuildEvent"><Exec Command="XCOPY …"/>`, and only
  the `PostBuildEvent` *property* is detected.
- **Late `PostBuildEvent`:** legacy projects that set `PostBuildEvent` after the targets import can
  only be neutralized with a global property (`-p:PostBuildEvent=`). OFR0115's remedy ("guard it
  with `$(OfframpCompileOnly)`") needs a project edit.
- **Non-string `.resx` resources (MSB3822/MSB3823):** these need
  `GenerateResourceUsePreserializedResources` plus `System.Resources.Extensions` under the .NET
  SDK's MSBuild.

#### 10. `csproj modernize` defects (dry run on all 64 legacy projects: 43 × OFR4303)

- **NuGet audit reported as a conversion failure.** 42 of the 43 failures are NU1902 (a known
  vulnerable package) promoted to errors by `TreatWarningsAsErrors`. Moving to `PackageReference`
  turns NuGet audit on; the conversion itself is fine. Report the vulnerabilities as their own
  finding (useful information) and do not fail verification on NU190x.
- **ProjectReference metadata is dropped.** A source-generator reference
  (`ReferenceOutputAssembly="false" OutputItemType="Analyzer"`) becomes a plain
  `ProjectReference`, so the generator is referenced as an assembly and no longer runs.
  Verification caught it as "references added"; the conversion should keep all metadata except
  `Project` and `Name`.
- **Output folder moves.** `<OutputPath>bin\</OutputPath>` is kept, but the SDK appends the TFM
  (verified: the output lands in `bin/net472/`). AfterBuild copies from `bin\` then fail, and
  sibling legacy projects that reference `..\X\bin\X.dll` break. The converter should set
  `AppendTargetFrameworkToOutputPath=false` when it keeps a legacy `OutputPath`.
- **Web projects are skipped entirely (OFR4304).** On a Web Forms codebase that is the largest
  group of projects.

#### 11. `move plan` and `move apply` on a large project

Plans were made from `DotNetNuke.Library` to `DotNetNuke.Abstractions` (netstandard2.0).

- **Warning policy mixed up with portability.** 287 of the 389 `OFR2103` "Does not compile in
  `<dest>`" exclusions are only CS1591 (missing XML doc comment): the destination builds with
  `TreatWarningsAsErrors` and documentation generation, and Library has `NoWarn 1591`. The human
  message gives no reason; the compiler errors are only in `data.details`.
  - Separate the destination's warning policy from portability (its own code or reason, with a
    remedy).
  - Show the first error in the message.
- **`--all` plans nothing.** It reports 0 moves and `OFR2104` ×98 ("Library does not compile
  without the moved files"). A source generator (`DnnDeprecatedGenerator`) emits partial halves of
  types into Library. The hand-written half moves and the generated half stays, which causes CS0436.
  - Partial types whose other part is generated should be excluded with a reason, and the plan
    retried.
  - One project-level failure should be reported once, not once per file.
- **The plan's trial compilation does not match the real build.** A 14-file plan passed trial
  compilation but failed verification with CS0122. It relied on an `<InternalsVisibleTo>` item,
  and Abstractions sets `GenerateAssemblyInfo=false` (it uses a shared `SolutionInfo.cs`), so the
  SDK ignores the item. Verification and rollback handled it correctly. The planner should know
  that the item is inert in such a project and exclude the files that need internals.

#### 12. The corpus job cannot fail

`.github/workflows/corpus.yml` runs `dotnet test --filter "Category=Corpus"`, and
`docs/spec/04-testing-and-fixtures.md` says it scans NHibernate 4.x and DotNetNuke 8.x. No test in
the repository has that category, so the weekly job matches nothing and stays green. Adding this
tag of DNN, with the assertions from this page, would have caught most of P0.

### P2: polish

- **`doctor` global.json remedy is wrong.** With `rollForward: latestMinor` and only SDK 10
  installed, doctor suggests `latestFeature`, which is *stricter*; following the advice changes
  nothing. Suggest `latestMajor`, or compute the least permissive setting that selects an
  installed SDK.
- **`doctor` global.json message is truncated.** It embeds a cut-off line of `dotnet` stderr:
  "…is not installed: The command could not be loaded, possibly because:".
- **`doctor` CPM check is noise here.** It raises 64 `OFR1303` warnings in a repository with no
  `Directory.Packages.props` and no plan to adopt central package management. `OFR1303` is a
  preflight for `deps consolidate --cpm`; `doctor` could show it only when central package
  management is configured or requested.
- **`OFR0101` gives a misleading reason.** It says "no evaluation for it in the build log" for 6
  projects that the solution configuration *does* build; MSBuild skipped them because a dependency
  failed. Name the failed dependency.
- **`OFR0115` evidence contains absolute paths** (`/home/user/...`); output paths should be
  repository-relative.
- **`OFR0130` would be clearer grouped by code** (MSB4019 ×12, MSB3073 ×7, …) than as the first
  20 raw errors.
- **The build phase has no heartbeat.** It is one progress event; on a 30-minute build the user
  sees nothing.
- **`report` trend includes failed scans.** Ledger snapshots from partial scans enter the trend,
  so the headline reads "framework LOC +144,198" when nothing changed. Skip them or flag them, or
  do not write a snapshot when the build failed.
- **`report` counts every web project as an application:** 20 applications for what is one site
  plus plugins.
- **`OFR1004` false positive.** "NUnit only works on Windows on net10.0 … references
  Microsoft.Win32.Registry." A reference to that assembly is not evidence of Windows-only code.
- **`OFR1404` false blockers.** It calls
  `packages/NUnit.4.2.2/lib/net462/nunit.framework.dll` "built for .NET Framework and nothing
  replaces it", but NUnit 4.2.2 ships `lib/net6.0`. The same happens for
  Microsoft.AspNet.WebApi.Client 6.0.0, Portable.BouncyCastle 1.9.0 and SharpZipLib 1.3.3, which
  all ship netstandard2.0. Decide at package level, not from the net4x DLL the HintPath picked.
- **`audit dead-code` crashes on one unreadable file.** A dangling symlink ended the command with
  `OFR0099`, exit 3, after 60 seconds of work: `DeadCodeAnalyzer` calls `File.ReadAllText` on
  every `.xml`, `.json`, `.config`, `.resx` and `.xaml` file under each project folder. It should
  skip unreadable files with a diagnostic. Its `/bin/` exclusion is also case-sensitive (DNN has
  a `Bin/`).
- **`move tests` default target is a naming convention.** "No project is named
  `DotNetNuke.Library.Tests`": it could suggest existing test projects that already reference the
  source project (`DotNetNuke.Tests.Core`).
- **`move tests` "Builder" heuristic is weak.** Classes named `*Builder` are "test support" by
  name, but `LocalizationExpressionBuilder` is an ASP.NET expression builder registered in
  web.config.
- **`codemod run --mod http-context` repeats itself:** 367 identical info diagnostics ("the
  project does not reference ASP.NET Core") on a `System.Web` project; one per project is enough.
- **`.vbproj` is silently skipped.** `DotNetNuke.WebUtility.vbproj` appears only in the audits'
  `skipped` list, with no diagnostic.

## Command log

Times are wall-clock times on the 4-core container, with the model built from the workarounds
below.

| Command | Time | Result |
|---|---|---|
| `doctor` | 2 s | fail: global.json (SDK 9.0.202 pinned); 64 × OFR1303 |
| `init --defaults` | <1 s | fine; picked `DNN_Platform.sln` out of 4 solutions |
| `scan` (as shipped) | 20 s | build failed: 44 errors, 59/66 partial, 6 not loaded |
| `doctor --fix --apply`, then `scan` | 14 s | still failing; the fix does not reach legacy projects |
| `scan` (with workarounds) | 66 s | 71 projects, 533,418 LOC, 0 diagnostics; deterministic |
| `graph --format json/html`, `plan`, `plan --frontier`, `report` | <1 s each | see P0 #5 and P2 |
| `deps audit` | 23 s | 23 packages only (P1 #7); OFR1004 false positive |
| `deps gac` | 48 s | plausible |
| `deps resolve-dlls` | 218 s | P0 #2, P2 (OFR1404) |
| `deps consolidate --all` | 23 s | `PackageReference` only |
| `redirects sync` | 1 s | P0 #3 |
| `csproj modernize --all` (dry run) | 183 s | P1 #10 |
| `audit api` | 133 s | 25,245 findings; P0 #1 |
| `audit behavior` / `serialization` / `native` | 82 / 56 / 49 s | 2,109 / 360 / 20 findings, plausible |
| `audit dead-code` | 62 s | first run crashed (P2); P0 #4 |
| `ifdef report` | 8 s | plausible |
| `seams --project DotNetNuke.Library` | 64 s | 136 seams, 303 partitions |
| `move tests --project DotNetNuke.Library` | 49 s | no test code, correctly |
| `move plan` (Common/Utilities, then `--all`) | 47 / 49 s | 14 moves / 0 moves (P1 #11) |
| `move apply` (14 files) | 39 s | verification failed (CS0122), rolled back cleanly |
| `verify --projects DotNetNuke.Instrumentation` | 12 s | passed |
| `codemod run --mod sqlclient` / `http-context` | 18 s each | good warnings / noisy |
| `config convert --project DotNetNuke.Website` | 3 s | plausible |
| `guide`, `ide check` | 1 s | fine |

Not exercised: `audit api-compat`, `service`, `web`, `remote`, `extract interface`, `forwarders`,
`slice`, `mcp serve`, and the VS Code extension.

## Appendix: what it took to build DNN on Linux

This is effectively the specification for a legacy-project `doctor --fix` and restore. The items
are in the order they were hit.

1. **SDK pin.** `global.json` pins `9.0.202` with `rollForward: latestMinor`; change it to
   `latestMajor`.
2. **`packages.config` restore.** Collect every (id, version) pair from the 64 `packages.config`
   files and download them with a throwaway SDK project of `PackageDownload` items. Then link
   `packages/<Id>.<Version>` to `~/.nuget/packages/<id>/<normalized version>`. Mind the
   `1.0.0.0` vs `1.0.0` folder names.
3. **Reference assemblies.** Download `Microsoft.NETFramework.ReferenceAssemblies.net40`, `.net45`,
   `.net472` and `.net48` (1.0.3), and link their `build/.NETFramework/vX` folders into one
   directory for `TargetFrameworkRootPath`.
4. **`offramp.yml`:**

   ```yaml
   verify:
     properties:
       RestorePackages: "false"          # legacy .nuget/NuGet.targets would run mono NuGet.exe
       VSToolsPath: "<nuget>/msbuild.microsoft.visualstudio.web.targets/14.0.0.3/tools/VSToolsPath"
       TargetFrameworkRootPath: "<dir with .NETFramework/v4.0, v4.5, v4.7.2, v4.8>"
       PostBuildEvent: ""                # legacy post-build events use cmd syntax (if not exist, xcopy)
       NuGetAudit: "false"               # only for csproj modernize's verification (P1 #10)
   ```

5. **`Directory.Build.targets`** (outside Windows only):
   - empty `KillVBCSCompilerBeforeClean`, `KillVBCSCompilerBeforeCopy` and `PostBuild` targets;
   - `GenerateResourceUsePreserializedResources=true`, plus a `System.Resources.Extensions`
     reference, for the two projects with non-string `.resx` resources.
6. **Case fixes.** Symlinks for `Package.Targets`, `Module.Build`, `Skin.Build`,
   `SkinPackage.Targets`, the `Dto` → `DTO` folders, four single files, and `bin` → `Bin`, plus
   `.xml` → `.XML` documentation-file links in 25 projects. Four csproj files were edited from
   `Microsoft.CSharp.Targets` to `Microsoft.CSharp.targets`.

The `audit api` root cause (P0 #1) was confirmed with a small console program that references
`Offramp.Analysis`, builds each project through `TargetCompilationBuilder`, and compares every
missing-name diagnostic's quoted name with the identifier at its location.
