# Building anywhere

**The goal:** anyone who clones the repository builds the whole solution with a plain `dotnet
build`, on macOS, Linux, or Windows, without Offramp. On every OS it compiles the same code that
Visual Studio compiles.

Why it matters:
- Offramp's analyses read the compiler calls of this build, so a failed build means partial
  answers.
- Developers who are not on Windows can only work on the migration once this is true.

Background, and every XML snippet mentioned below, is in Offramp's
[`docs/compiling-on-macos.md`](https://github.com/Andorbal/offramp/blob/main/docs/compiling-on-macos.md).

## The loop

```bash
offramp doctor --json --out doctor.json
offramp init --defaults --solution src/App.sln
offramp doctor --fix --json --out fix.json      # dry run: read the diffs with the user
offramp doctor --fix --apply
offramp scan --json --out scan.json
```

After the first `scan`:
1. Read `result.buildSucceeded`, `result.notLoaded`, `result.partial`, and the diagnostics.
2. Fix the first cause.
3. `scan` again.

Useful `jq` (see [commands.md](commands.md) for more):

```bash
jq -r '.result.checks[] | select(.status != "pass") | "\(.id) \(.status): \(.message)"' doctor.json
jq -r '.diagnostics | group_by(.code)[] | "\(.[0].code) x\(length): \(.[0].message)"' scan.json
jq -r '.result.notLoaded[] | "\(.project): \(.reason)"' scan.json
jq -r '.projects[] | select(.partial) | .id' .offramp/workspace.json
```

One scan names every problem it can see; Open Live Writer once took eleven scans to find them one
build at a time. Still, expect a few rounds: after `doctor --fix`, NHibernate needed one more fix
and SmartStoreNET needed its letter-case paths renamed.

## What `doctor --fix` does for you

`doctor --fix` handles these. Like every write, it is a dry run until `--apply`. Visual Studio's
own build on Windows stays as it was.

| Problem outside Windows | What the fix does | Diagnostic |
|---|---|---|
| Legacy projects find no .NET Framework reference assemblies (MSB3644) | the block's legacy section supplies them from the `Microsoft.NETFramework.ReferenceAssemblies` package | `OFR0018`, `OFR0013` |
| ASP.NET web application targets missing (MSB4019) | the block takes them from a package | `OFR0116` |
| sgen, or `MvcBuildViews` (MSB3474, MSB4803) | the block turns them off; where a project file sets them, `doctor` adds `'$(MSBuildRuntimeType)' != 'Core'` | `OFR0110`, `OFR0116`, `OFR0019` |
| Build events and cmd.exe `Exec` commands run through `/bin/sh` | conditioned on `'$(OS)' == 'Windows_NT'` | `OFR0115`, `OFR0019` |
| NuGet 2's `RestorePackages` runs `NuGet.exe` under Mono | conditioned on Windows | `OFR0019` |
| `MSBuildExtensionsPath` overridden, so `Directory.Build.props` is never imported | conditioned on Windows | `OFR0122` |
| `Microsoft.Bcl.Build` binding-redirect task (MSB4062) | the block sets `SkipEnsureBindingRedirects` | `OFR0124` |
| `packages.config` is ignored by `dotnet restore` | `Offramp.PackagesConfig.targets`, imported from the block and `Directory.Solution.targets`, restores into `packages/` | `OFR0026`, `OFR0105` |
| `dotnet build` on Windows lacks the same tasks | the block has a section for it; Visual Studio's build is unchanged | |

`doctor`'s "Builds without Offramp" check (`plain-build`) reads the files, not the model:
- `OFR0019` names each setting still without a condition;
- `OFR0026` names what only Offramp's builds supply: `verify.properties`, `packages.config`
  restore, Web Site projects.

When the check passes, it lists what a build outside Windows skips. Give that list to the user;
it is what only the Windows build still proves. The field tests found:
- `*.XmlSerializers.dll` from sgen;
- precompiled views;
- whatever the build events copy;
- NHibernate's Release build, which merges assemblies with ILRepack in a post-build step.

**Prefer fixes in files over `verify.properties`.** `offramp.yml`'s `verify.properties` are
global `-p:` properties, and only Offramp's builds get them. They make `scan` succeed where a
colleague's `dotnet build` fails. Use them for a quick look at the codebase, then replace them with
a fix in the files.

## What stays the repository's job

Offramp names these and gives the fix; it does not edit the code for them. For each one, show the
user the finding and the fix, and ask before you change anything.

| Diagnostic | What you see | What to do | Seen in |
|---|---|---|---|
| `OFR0117` letter case | MSB4019, CS2001, MSB3030, MSB3554 on Linux only | rename the reference or the file, as the team decides (see below) | SmartStoreNET: 14 paths, among them `.nuget/nuget.targets` for `NuGet.targets`, imported by 19 projects. Open Live Writer: 62, including files a `.resx` references. DotNetNuke: `Microsoft.CSharp.Targets`, `bin` for `Bin` |
| `OFR0123` source file missing | CS2001 for a git-ignored file | the repository's build script generates it: run that step once, as its README or `CONTRIBUTING.md` says. Don't commit a generated file without asking | NHibernate: NAnt writes `src/SharedAssemblyInfo.cs` |
| `OFR0115` on a generator | the guarded `Exec` wrote nothing, so the compile misses its output | generate the output once (a generator often runs on .NET unchanged), or check it in | Open Live Writer: `MarketXmlGenerator.exe` writes `Markets.xml` |
| `OFR0119` non-string resources | MSB3822, MSB3823 | `GenerateResourceUsePreserializedResources` and `System.Resources.Extensions`, for compile-only builds, as the docs show. Set for every build, it becomes a run-time dependency of the .NET Framework application, and that is the user's call | Open Live Writer: 13 projects |
| `OFR0118` inline task | MSB4801 (`CodeTaskFactory`) | in your own targets, use `RoslynCodeTaskFactory`; for a package's targets, redefine the calling targets as empty ones for compile-only builds | DotNetNuke: `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` |
| `OFR0125` MSTest v1 | CS0246 for `Microsoft.VisualStudio.TestTools` | move to `MSTest.TestFramework` (same namespace), or reference its 1.4.0 DLLs for compile-only builds | Open Live Writer: 2 test projects |
| `OFR0126` Web Site project | MSB4249; the whole solution stops | `scan` leaves it out and builds `.offramp/scan.slnf`. A plain build of the whole solution still stops: convert the site to a web application project, or build a solution filter (`offramp slice`) | NHibernate's `Everything.sln` |
| `OFR0101` not loaded, or `OFR0024` | the reason says why: a restore failure, a failed dependency, a native project | fix the cause the reason names. For a native (C++) project that the solution builds first, leave its `Build.0` line out of the configuration you scan | Open Live Writer: the Ribbon `.vcxproj` |
| `OFR0111`–`OFR0114` COM, EDMX, T4 or Fakes, SSDT | Windows-only tasks | the documented fix each names, or the compiler-log route below | |
| `OFR0020` several solutions | `init` and `scan` stop | pass `--solution`: the one the build script and `CONTRIBUTING.md` use | NHibernate: 3, SmartStoreNET: 2 |
| `OFR0131` timeout | the build ran out of time | raise `verify.timeoutSeconds`, scan a solution filter (`offramp slice`), or scan a log built elsewhere | |

### Letter case

Linux file systems are case-sensitive. Windows and macOS file systems, by default, are not.

- **Which problems show up where:** a build on Windows or a Mac never shows these problems. A
  Linux build (CI, a container, a colleague) fails on them.
- **Detection:** `OFR0117` checks only whether a path exists as spelled. So it fires only when
  Offramp runs on a case-sensitive file system. Run a scan on Linux, for example in a container,
  before you promise the team a Linux build.
- **Don't rewrite it automatically.** Offramp reports these and does not rewrite them, and neither
  should you without the user's decision:
  - for most items (`Import`, `Compile`, `HintPath`, `ProjectReference`), matching the spelling
    on disk is harmless;
  - for an `EmbeddedResource`, the spelling is part of the resource name, so
    `GetManifestResourceStream("...logo.png")` stops finding `Logo.png` at run time, on Windows
    too;
  - for a copied file, the spelling becomes the output file name.
- **Renaming the file instead:** a change that only alters case is easy to get wrong on Windows
  and Mac checkouts.
- **A local workaround:** a symbolic link in the spelling the build asks for gets a Linux machine
  past it. Never commit one without asking; the corpus tests use these links, the repository
  should not.

## Environment surprises from the field tests

- **`global.json`**: a pinned SDK with `rollForward: latestMinor` rejects a newer major SDK
  (`OFR0011`). Offramp itself runs on the newest runtime installed.
- **NuGet feeds**: `https://nuget.org/api/v2/` redirects downloads to `globalcdn.nuget.org`. Some
  proxies block that host while `api.nuget.org` works. Pointing `nuget.config` at
  `https://api.nuget.org/v3/index.json` fixes it, but that is the user's file and their call.
- **No Mono needed**: everything above runs on the .NET SDK alone.

## When a project cannot build here at all

Capture the build where it works, and analyze it here. On a Windows machine or CI agent:

```powershell
dotnet tool install -g complog
dotnet build App.sln -bl:msbuild.binlog        # or msbuild ... -restore -t:Rebuild from a Developer Command Prompt
complog create msbuild.binlog -o app.complog
```

Then, here:

```bash
dotnet restore App.sln
offramp scan --binlog msbuild.binlog --complog app.complog
```

This is a snapshot, so it goes stale as the code changes. Use it for the projects that need it,
not as the everyday path. On Windows, `offramp scan --msbuild` uses Visual Studio's MSBuild
directly.

## Proving it

The loop ends with a build nobody has helped along. Do not clean the user's working tree for this;
make a copy.

```bash
# Before the user has committed the changes: copy tracked and new files, not ignored ones.
mkdir ../plain-build
git ls-files -z --cached --others --exclude-standard | rsync -a --from0 --files-from=- ./ ../plain-build/
# After they are committed on a branch:
git worktree add ../plain-build <branch>

cd ../plain-build && dotnet build src/App.sln -c Release
```

Build with Debug and Release: `MSBuild.SDK.SystemWeb` turns precompiled views on in Release only,
and NHibernate's ILRepack step runs only in Release. Report:
- the result;
- the time;
- anything that still fails, and on which OS.
