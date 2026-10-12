# Field test: MassTransit 3.3.5

The `newtonsoft` commands (`scan`, `tests`, `upgrade`; `docs/spec/commands/newtonsoft.md`) were run
against a real codebase on Newtonsoft.Json 7.0.1 for the first time, to find what the two
`newtonsoft-*` fixtures do not show. This page records what happened, ranks what needs fixing, and
gives what it took to get there. The standard sweep ran too, for the corpus test, but was not examined
in depth: the other field tests cover those commands.

- **Codebase:** [MassTransit/MassTransit](https://github.com/MassTransit/MassTransit) at tag `v3.3.5`
  (lightweight), commit `16cd51721a19ce75e5db95663921caa1488257cc` (June 2016), the last release on
  Newtonsoft.Json 7.0.1 (3.4.1 moved to 9.0.1). A message bus library on .NET Framework 4.5.2.
  `src/MassTransit.sln` loads 36 projects and about 133,500 lines of C#. All 36 are legacy
  (non-SDK) and target `net452` only; 35 use `packages.config`. 13 are NUnit 2.6.4 test projects,
  21 libraries and 2 services. Newtonsoft.Json 7.0.1 is referenced through `packages.config` and a
  `HintPath` by 5 production projects (MassTransit, the RabbitMQ and Azure Service Bus transports,
  QuartzIntegration, Futures) and 4 test projects. The usage is about as varied as Newtonsoft.Json
  usage gets: five custom `JsonConverter`s, a custom contract resolver, public static
  `JsonSerializerSettings`, cached serializers in `Lazy<JsonSerializer>`, BSON (`BsonReader` and
  `BsonWriter`, obsolete in 13.x), `XmlNodeConverter`, and `JObject`/`JToken`. Six `app.config`
  files redirect Newtonsoft.Json to 6.0.0.0, a version the codebase has not used since it moved to 7.
- **Machine:** macOS 26.6 on an Apple M5 Pro (18 cores, 64 GB), the .NET 10.0.401 SDK, no Mono.
- **Offramp:** `feat/newtonsoft-upgrade` at `7dceb88` (the M18 branch, after its Windows fix),
  built locally and run as `dotnet src/Offramp.Cli/bin/Release/net10.0/offramp.dll`.
- **Method:** each command was run the way a new user would, with `--json`, in a fresh clone outside
  this repository. Results were checked against the MassTransit sources and root causes traced in
  Offramp's code. `upgrade --apply` was run, built, and undone with its journal. MassTransit's test
  projects target only .NET Framework, so recording baselines (which needs Windows, ADR 0067) was
  not possible on this machine; what recording would do on Windows was established from the build
  output and the run command Offramp uses. After the fixes, the whole loop (`doctor`, `scan`,
  `newtonsoft tests --record`, `newtonsoft upgrade --apply`) ran on a GitHub Actions `windows-latest`
  runner (Visual Studio 2022, the .NET 10.0.401 SDK); that run found P1 #9 and #10
  ([below](#on-windows-the-whole-loop)). The Windows runs were one-off workflows on throwaway branches.

## Summary

The codebase gets through `scan` with nothing but `doctor --fix --apply`: 36 projects, no partial
builds, no diagnostics, 23 seconds. That is a first for a field test, and it holds for `newtonsoft`
too. `newtonsoft scan` finds 65 sites in 9 projects and checks them against 13.0.4 in 72 seconds,
with the BSON findings (`OFR3702`) at exactly the four places the 13.x build warns about.
`newtonsoft upgrade` plans the right edits for every reference style the codebase uses, and
`--apply` restores, builds all 11 affected projects against 13.0.4, and passes in 20 seconds; the
journal undoes it byte for byte.

The gaps are in what the tests can see and in what the commands touch:

1. **NUnit 2 test projects record nothing, without saying why.** Detection and generation work for
   NUnit 2.6.4 (the spec says only 3 and 4 are supported; the code accepts any `NUnit`), and the
   generated probes compile. But MassTransit's test projects have no VSTest adapter (NUnit 2 users
   ran `nunit-console` from `NUnit.Runners`), so on Windows `dotnet test <assembly>
   --test-adapter-path <output>` would discover no tests, and every probe would be `missing`
   (`OFR3741`) with nothing to say that the adapter is the problem.
2. **The settings of MassTransit's main serializers are `unknown`.** `JsonMessageSerializer` and
   `BsonMessageSerializer` cache their serializer in a `static readonly Lazy<JsonSerializer>`;
   `EncryptedMessageSerializer` copies one into a readonly field in its constructor. Offramp follows
   neither, so 3 of the 4 message serializers are "settings cannot be recreated" (`OFR3721`).
3. **`newtonsoft upgrade` edits log4net redirects.** Its redirect step is all of `redirects sync`,
   so two stale log4net redirects ride along in the Newtonsoft.Json upgrade's change set.
4. **`newtonsoft tests --apply` edits test projects it writes no probes into.** Three of the four
   test projects get the probe runtime and two `Compile` items, and nothing that uses them.
5. **Probes for `XElement` do not compile.** The generator picks `XElement`'s copy constructor and
   writes `new XElement(null)`, which is ambiguous; three probes are dropped (`OFR3735`).

On Windows, after the fixes, the whole loop works on this codebase: the probes record on 7.0.1, the
upgrade builds on 13.0.4, and the probes catch a real behavior change in MassTransit's XML serializer
([below](#on-windows-the-whole-loop)). Getting there found three more P1s, in `doctor` and `scan`
for `dotnet build` on Windows (#9 to #11). The corpus's first run on Linux found one more, in
`scan`: a project the solution lists in the wrong letter case (#12).

Coverage is low (1 of 51 production sites gets a probe), and most of that is MassTransit, not
Offramp: a message bus serializes `object` and internal envelope types, which no test can construct
and no rule can type. The report says where Offramp could do better (finding #6).

## Status after the fixes

All nine P1s and two of the three P2s are fixed on the branch that carries this report, and all
are general. The numbers are from a fresh checkout after the fixes; the corpus test `masstransit`
(`tests/Offramp.Corpus.Tests/Codebases/MassTransitTests.cs`) pins them.

| Finding | Fix | On MassTransit now |
|---|---|---|
| P1 #1 NUnit 2 records nothing, silently | `79e3d6b` | `OFR3747` for `MassTransit.Tests`, naming `NUnitTestAdapter`, on every OS; with that package added (a `packages.config` entry and its props import), the 6 probes record on Windows |
| P1 #2 settings through `Lazy<T>` and fields set once | `bc364f1` | the JSON and BSON serializers are `member` (`SerializerSettings`), the encrypted one `member` (`BsonMessageSerializer.Serializer`); `unknown` settings 20 (was 23), the rest injected or converter parameters, now located at the assignment |
| P1 #3 the upgrade edits log4net redirects | `241990c` | the upgrade's configuration edits are the two Newtonsoft.Json redirects only |
| P1 #4 files in test projects without probes | `32af828` | 4 files, all in `MassTransit.Tests` (was 10 in 4 projects, and 3 project files edited) |
| P1 #5 `XElement` probes do not compile | `13a6e28` | no `OFR3735` (was 3); the `XElement` site is `payloadNotGeneratable`, which it is |
| P2 #6 unknown payloads read as missing samples | `ccd29c8` | "its payload type is not known (…; OFR3720)"; a type that cannot be written is named (`JsonMessageEnvelope`) |
| P2 #7 `OFR3739` twice | `79e3d6b` | once, with `--test-adapter-path` in its command |
| P1 #9 no targeting pack for `dotnet build` on Windows | `cd971ef` | the legacy section applies to every build by .NET's MSBuild; all 36 projects build on `windows-latest` |
| P1 #10 NuGet 2 restore under .NET's MSBuild on Windows | `cd971ef` | `RestorePackages` conditioned on `'$(MSBuildRuntimeType)' != 'Core'` |
| P1 #11 the packages.config download project in the model | `cb0cb38` | 36 projects on Windows too (was 37, one of them partial) |
| P1 #12 the solution's project in the wrong letter case is not named | `e69c94c` | on Linux the first scan names all three paths in the wrong letter case (`OFR0117`), the solution's among them; with the links it prescribes, all 36 projects build |

Still open:

- **P2 #8** (fixture fields set in `[SetUp]`): not fixed, as proposed.
- **Coverage.** 1 of 51 production sites gets probes. The rest are MassTransit's shape (payloads
  typed `object` or internal, serializers injected through constructors), and tracing a constructor
  parameter to the callers that pass it is the next step that would move it (ADR 0073 says why not now).
- A `RestorePackages` that an earlier Offramp conditioned on `'$(OS)' == 'Windows_NT'` is left as it
  is (ADR 0073); `dotnet build` on Windows then still fails on `NuGet.targets` until the condition
  is changed by hand.

## On Windows: the whole loop

After the fixes, a `windows-latest` runner ran what a Windows user would: `doctor --fix --apply`,
`init`, `scan`, the package `OFR3747` asks for (`NUnitTestAdapter` 2.3.0 in `MassTransit.Tests`'
`packages.config`, with its props import, as Visual Studio adds it), `newtonsoft tests --record
--apply`, and `newtonsoft upgrade --apply`.

| Step | Time | Result |
|---|---|---|
| `doctor --fix --apply --yes` | 2 s | the block, the 28 `RestorePackages` conditioned for .NET Framework's MSBuild |
| `scan` | 26 s | 36 projects, build succeeded, 0 partial |
| `newtonsoft tests --record --apply` | 51 s | 6 probes, each run twice: all 6 `recorded`, on Newtonsoft.Json 7.0.0.0 |
| `newtonsoft upgrade --apply --yes` | 79 s | 9 projects to 13.0.4, restored, all 36 projects built; probes: 3 `same`, 3 `noLongerThrows` (`OFR3762`, exit 1); the upgrade stays applied (`--on-diff keep`) |

**What the probes found.** MassTransit's XML serializer (`XmlMessageSerializer.XmlSerializer`, a
`JsonSerializer` with Newtonsoft.Json's `XmlNodeConverter`) writing an `XDocument` that has a
declaration and no root element: on 7.0.1 it throws `ArgumentOutOfRangeException` ("Index was out of
range"), on 13.0.4 it writes `{"?xml":{"@version":…,"@encoding":…,"@standalone":…}}`. That is a real
change between the two releases, at a site no rule in `rules/newtonsoft-breaking.yml` covers, found
by a probe generated for a site with no entry point. The three reading probes that read what it wrote
are `same`: verifying feeds them the 7.x baselines, which threw.

Before the fixes, the same run stopped at `scan`: P1 #9, P1 #10, and (with those two worked around)
P1 #11.

## What worked well

- **The build, out of the box.** `doctor --fix --apply` conditions the 28 NuGet 2 `RestorePackages`
  imports, supplies reference assemblies to the 36 legacy projects, and adds
  `Offramp.PackagesConfig.targets`; `scan` restores the 43 `packages.config` packages and builds
  everything. No workarounds were needed.
- **The obsolete-API findings are exact.** `OFR3702` names `BsonWriter` and `BsonReader` at
  `MessageSessionSagaRepository.cs:109,132`, `BsonMessageDeserializer.cs:57` and
  `BsonMessageSerializer.cs:83`, with `BsonDataWriter`/`BsonDataReader` from `Newtonsoft.Json.Bson`
  as the fix. Those are the four places the 13.0.4 build warns about (CS0618).
- **Sites that are not sites are left alone.** RabbitMqTransport references Newtonsoft.Json only for
  a `typeof(JToken)` comparison; it has 0 sites, which is right.
- **The behavior findings are specific.** `OFR3719` names the 8.0.1 JArray comment change for the
  three projects with DOM sites, and the camel-casing change for keys with spaces at the one site
  that camel-cases dictionary keys (`JsonSerialization_Specs.cs:155`).
- **Every reference style is edited exactly.** 9 `packages.config` entries, 9 `Reference`
  identities and `HintPath`s (`Newtonsoft.Json.13.0.4\lib\net45`, read from the package), separators
  and every other byte kept.
- **Stale redirects are moved.** The two 6.0.0.0 redirects in applications
  (`MassTransit.TestFramework`, `MassTransit.NHibernateIntegration.Tests`) become 13.0.0.0
  (`OFR1502`). The four in libraries' `app.config` files, which nothing reads at run time, are left
  alone.
- **Apply, build, undo.** `upgrade --apply` restored, built 11 projects against 13.0.4 (the output
  DLL is the 13.0.4 one), and said honestly that no baselines meant a compiler-only check
  (`OFR3767`). `move rollback --journal …` restored all 24 files.
- **The determinism checks pass.** `scan --no-build` matches `scan`.

## Findings, ranked

No P0: nothing gave a wrong or dangerous answer. Every P1 below would hit any codebase with the same
shape, and none is specific to MassTransit.

### P1 #1: NUnit 2 test projects (and any legacy test project without an adapter) record nothing, silently

**What happened.** `newtonsoft tests` chose `MassTransit.Tests` for `MassTransit` (by name), detected
`nunit`, and generated probes with `[TestFixture]`, `[Test]` and `[Category]`, which NUnit 2 has; the
trial compile passed. On Windows, recording runs `dotnet test MassTransit.Tests.dll
--test-adapter-path src/MassTransit.Tests/bin/Debug --filter …`. VSTest runs NUnit tests only through
an adapter, and the output folder has none: the project has `NUnit` and `NUnit.Runners` 2.6.4, the
console runner NUnit 2 projects used, and no `NUnitTestAdapter`. VSTest would discover no tests and
exit without error, and every probe would be `missing` (`OFR3741`), "wrote no result".

**Evidence.** `src/MassTransit.Tests/packages.config`; `src/MassTransit.Tests/bin/Debug` after the
build has no `*TestAdapter*.dll`. Neither has any of the other 12 projects Offramp classifies as tests. `TestFrameworks.Detect`
(`NewtonsoftTestsGenerator.cs`) matches the `NUnit` package at any version, so the spec's "NUnit (3
and 4)" understates what generation supports; it is running that is not supported, and nothing says so.

**Would another codebase hit this?** Yes: most legacy NUnit 2 and MSTest v1 projects ran under
Visual Studio's built-in runners or a console runner and never had a VSTest adapter package.
SmartStoreNET's five test projects are NUnit 2 too.

**Proposed fix.** After the build, before running a legacy test project, look for a VSTest adapter
in its output folder. If there is none, report it (a new code, with the package to add for its
framework: `NUnitTestAdapter` 2.x for NUnit 2, `NUnit3TestAdapter` for NUnit 3 and 4,
`xunit.runner.visualstudio` for xunit, `MSTest.TestAdapter` for MSTest) and do not run it, on every
OS, so a user finds out on macOS before going to Windows. `newtonsoft.tests.command` stays the way to
use another runner. Spec: say that NUnit 2 is supported.

### P1 #2: settings through `Lazy<JsonSerializer>` and constructor-assigned readonly fields are `unknown`

**What happened.** 23 of 65 sites have `unknown` settings. Three of them are the ones that matter
most in MassTransit:

| Site | The code | Settings Offramp could know |
|---|---|---|
| `JsonMessageSerializer.Serialize#1` | `_serializer.Value.Serialize(…)`; `static readonly Lazy<JsonSerializer> _serializer`, assigned in the static constructor: `new Lazy<JsonSerializer>(() => JsonSerializer.Create(SerializerSettings))` | `JsonMessageSerializer.SerializerSettings`, a public static field |
| `BsonMessageSerializer.Serialize#1` | the same pattern | `BsonMessageSerializer.SerializerSettings` |
| `EncryptedMessageSerializer…Serialize#1` | `readonly JsonSerializer _serializer`, assigned once in the only constructor: `_serializer = BsonMessageSerializer.Serializer` | `BsonMessageSerializer.Serializer`, a public static property |

**Root cause.** `NewtonsoftSites.Classify` follows static fields and properties (by their
initializer), `JsonSerializer.Create(…)`, and locals assigned once. It has no case for
`Lazy<T>.Value`, and `Initializer` reads a field's declaration only, not the one assignment a
readonly field can get in a constructor.

**The rest are right to be unknown:** 5 are serializers passed into a constructor (the JSON, BSON,
encrypted and XML deserializers, and `JsonConsumeContext`), 6 are calls inside converters on the
`serializer` parameter they are given (`InterfaceProxyConverter`, `MessageDataJsonConverter`,
`ListConverterCache`), and 9 are in `MassTransit.Tests` (finding #8).

**Proposed fix.** In `Classify`: `x.Value` where `x` is a `System.Lazy<T>` whose value is known
classifies the factory's returned expression; a readonly field (static or instance) assigned exactly
once, in its initializer or in the type's only constructor of that kind, classifies that value. A
value that depends on a constructor parameter still classifies as `unknown`.

### P1 #3: `newtonsoft upgrade` rewrites unrelated binding redirects

**What happened.** The plan changes `log4net` redirects in `src/MassTransit.Host/App.config`
(1.2.14.0 → 1.2.15.0) and `src/MassTransit.QuartzIntegration.Tests/App.config` (1.2.13.0 →
1.2.15.0). Both are stale, and `redirects sync` would fix them, but they have nothing to do with
Newtonsoft.Json: they enter the upgrade's change set and journal, and a behavior difference that
`--verify` finds afterwards can no longer be put on Newtonsoft.Json alone.

**Root cause.** `NewtonsoftUpgrader.RedirectsAsync` runs `RedirectPlanner.Plan` over every
application and every assembly and merges every edit.

**Proposed fix.** Restrict the redirect step to the `Newtonsoft.Json` identity (an assembly filter
on `RedirectsRequest`), and report only those redirects in the result.

### P1 #4: `newtonsoft tests --apply` edits test projects that get no probes

**What happened.** Four test projects were chosen as targets; only `MassTransit.Tests` gets probes.
The other three (`MassTransit.AzureServiceBusTransport.Tests`, `MassTransit.Futures.Tests`,
`MassTransit.QuartzIntegration.Tests`) still get `NewtonsoftCharacterization/OfframpProbe.cs`,
`OfframpObjectDump.cs`, and two `Compile` items in their project files.

**Root cause.** `NewtonsoftTestsGenerator.Render` always adds the runtime files, whatever the probes.

**Proposed fix.** A test project with no probes gets no files, and the generated files a previous run
left there are deleted, as for any file no longer produced.

### P1 #5: probes for `XElement` do not compile (`OFR3735`)

**What happened.** `XmlMessageDeserializer` serializes `document.Root`, an `XElement`. The three type
probes for it are dropped: "The call is ambiguous between … `XElement(XName)` and
`XElement(XElement)`".

**Root cause.** `Generatability.Constructor` takes the accessible constructor with the fewest
parameters, then by display string: `XElement(XElement)`, the copy constructor, sorts before the
others. Its argument recurses into `XElement` until the depth limit, which writes `null`, and
`new XElement(null)` matches two overloads. A copy constructor can never be given anything but
`null`, so even where the call is not ambiguous it produces a probe that throws.

**Proposed fix.** Never choose a constructor with a parameter of the type being created, and write a
`null` constructor argument as `default(T)`, so the overload is always the one chosen.

### P2 #6: the reason for a site without a payload type reads as if it were the sample's fault

37 of the 65 sites have no payload type (`payloadTypes: []`: MassTransit serializes `object`, and
`Deserialize(reader, type)` with a runtime `Type`). The reading ones are `noInput` with "no sample
payload for  and nothing writes it with the same settings" (the type name is blank), the writing ones
`payloadNotGeneratable`, "its payload type cannot be written as C#". Both point the user at samples
or at the type, when the reason is that no type is known. Proposed: say "the payload type is not
known (object, or a Type chosen at run time)".

### P2 #7: `OFR3739` reported once per recording pass

Recording runs every probe twice (`record-1`, `record-2`), and the skipped-project warning is
reported on each pass: two identical `OFR3739`s. Its suggested Windows command also leaves out the
`--test-adapter-path` the real run passes.

### P2 #8: settings in test fixture fields set in `[SetUp]` are `unknown`

`JsonSerialization_Specs` assigns `_serializer = JsonMessageSerializer.Serializer` in a `[SetUp]`
method; 9 sites in `MassTransit.Tests` are `unknown` because the field is not readonly and is
assigned in a method. These are test sites, which matter less (the probes go into the same project).
Not proposed for now.

## Found on Windows

### P1 #9: `dotnet build` of a legacy project on .NET Framework 4.5.2 fails on Windows (MSB3644)

**What happened.** On `windows-latest`, `scan` after `doctor --fix` failed in 9 projects: "The
reference assemblies for .NETFramework,Version=v4.5.2 were not found." Visual Studio 2022 installs
no targeting pack older than 4.6.2, and the compile-only block's legacy section, which supplies the
reference assemblies as a package, was conditioned on `!$([MSBuild]::IsOSPlatform('Windows'))`
(ADR 0064 assumed Windows has the packs).

**Would another codebase hit this?** Any legacy codebase on 4.6.1 or older, built with `dotnet build`
on a machine with only Visual Studio 2022 or the .NET SDK: most of them.

**Fix.** The legacy section's condition becomes `'$(MSBuildRuntimeType)' == 'Core'`: every build by
.NET's MSBuild, as on macOS; Visual Studio's own build is unchanged. `doctor --fix` widens a section
an earlier Offramp wrote, in place.

### P1 #10: NuGet 2's `RestorePackages` was kept for Windows, not for .NET Framework's MSBuild (MSB4801)

**What happened.** `doctor --fix` conditions the 28 `<RestorePackages>true</RestorePackages>` on
`'$(OS)' == 'Windows_NT'` (ADR 0063: "NuGet.exe, through Mono outside Windows"). On Windows,
`dotnet build` then runs `src/.nuget/NuGet.targets`, whose `DownloadNuGet` task is a
`CodeTaskFactory` task: MSB4801 and MSB4036, and the build stops.

**Fix.** Condition it on `'$(MSBuildRuntimeType)' != 'Core'`, like sgen and `AspNetCompiler`. Under
.NET's MSBuild, `Offramp.PackagesConfig.targets` restores `packages.config` instead.

### P1 #11: the packages.config download project is taken for a repository project

**What happened.** With P1 #9 and #10 worked around, `scan` on Windows built everything but reported
37 projects, one partial: `src/obj/offramp-packages-config/download.csproj`. On Windows, `scan`
leaves `packages.config` to the build, and with a cold NuGet cache `Offramp.PackagesConfig.targets`
writes and restores that project to download the missing packages. The build log has an evaluation of
it like any other project's, and nothing compiles in it.

**Would another codebase hit this?** Any `packages.config` solution scanned on Windows with packages
missing from the global packages folder, or built with `dotnet build -bl` and scanned with
`--no-build`.

**Fix.** `BinlogReader` skips it, as it skips `.metaproj` files.

## Found by the corpus on Linux

### P1 #12: a project the solution lists in the wrong letter case is not named

**What happened.** The corpus's first run in CI, on `ubuntu-latest`, loaded none of the 36 projects:
each was "not built: MSBuild stopped before building any project (MSB3202: The project file
"src/Containers/MassTransit.AutofacIntegration/MassTransit.AutofacIntegration.csproj" was not found.)".
`MassTransit.sln` (and two `ProjectReference`s) spell the folder `AutofacIntegration`; git has
`AutoFacIntegration`. macOS ignores the difference, which is why the field test and the corpus test,
both run on macOS, never saw it. `scan` named the two `Compile` items in the wrong letter case
(`OFR0117`) but not the solution's entry: the error that shows it, MSB3202, belongs to the solution,
not to the project it names, and was not among the errors read for letter case.

**Would another codebase hit this?** Any solution whose entry for a project differs in letter case
from the folder or file on disk, built on Linux.

**Fix.** MSB3202 is read for letter case like MSB4019 and CS2001, and is matched to the project it
names when the project has no evaluation of its own. The corpus test now applies the links `OFR0117`
prescribes on Linux, as SmartStoreNET's and Open Live Writer's do; `CaseLinks` links a folder in the
wrong case, not just the file, so the files beside the project still resolve.

## Command log

| # | Command | Time | Exit | Result |
|---|---|---|---|---|
| 1 | `doctor` | 1 s | 0 | 36 legacy projects without reference assemblies (`OFR0018`), 28 unconditioned `RestorePackages` (`OFR0019`), 35 `packages.config` projects (`OFR0026`) |
| 2 | `doctor --fix --apply --yes` | <1 s | 0 | `Directory.Build.props`, `Directory.Solution.targets`, `Offramp.PackagesConfig.targets`, 28 project files conditioned |
| 3 | `init` | <1 s | 0 | `offramp.yml` naming `src/MassTransit.sln` |
| 4 | `scan` | 23 s | 0 | 36 projects, 133,531 lines, build succeeded, 0 partial; restored 43 packages (`OFR0106`) |
| 5 | `newtonsoft scan` | 72 s | 0 | 9 projects, 65 sites, 8 findings: `OFR3702` ×4 (as 2 diagnostics), `OFR3719` ×4, `OFR3720` ×4, `OFR3721` ×2; target 13.0.4 |
| 6 | `newtonsoft tests` | 9 s | 0 | 4 targets (all `nunit`), 51 sites, 6 probes for 1 site; uncovered: `noInput` 31, `payloadNotGeneratable` 11, `depthExceeded` 3, `notAccessible` 3, `doesNotCompile` 1, `settingsNotReproducible` 1; `OFR3735` ×3, `OFR3743` ×2 |
| 7 | `newtonsoft upgrade` | 11 s | 0 | 9 projects, 5 config files (2 of them log4net only: P1 #3) |
| 8 | `newtonsoft tests --apply` | 10 s | 0 | 10 files in 4 test projects (6 of them in the 3 without probes: P1 #4) |
| 9 | `newtonsoft tests --record --apply` | 11 s | 0 | built; `OFR3739` ×2 for `MassTransit.Tests` (P2 #7) |
| 10 | `newtonsoft upgrade --apply --yes` | 20 s | 0 | applied, restored, 11 projects built; `OFR3767` |
| 11 | `move rollback --journal … --apply --yes` | <1 s | 0 | 24 files restored |
| 12 | `scan --no-build` | 1 s | 0 | model matches `scan` |
| 13 | `graph --format json`, `plan`, `report --format json` | 1 s each | 0 | |
| 14 | `deps audit` | 73 s | 1 | `OFR1001` ×8, `OFR1002` ×32, `OFR1003` ×3 |
| 15 | `deps resolve-dlls` | 1 s | 0 | |
| 16 | `redirects sync --prune` | <1 s | 0 | `OFR1502` ×4 |
| 17 | `audit api` | 33 s | 1 | `OFR3001` ×34, `OFR3011` ×34 |
| 18 | `audit behavior` | 8 s | 0 | 21 findings |
| 19 | `audit dead-code` | 7 s | 0 | `OFR3401` ×23 |
| 20 | `csproj modernize --all` | 43 s | 0 | `OFR4301` ×5, `OFR4306` ×30 |

## Appendix: what it took to get MassTransit through `scan` on macOS

1. `git clone https://github.com/MassTransit/MassTransit.git`, check out
   `16cd51721a19ce75e5db95663921caa1488257cc`.
2. `offramp doctor --fix --apply --yes`.
3. `offramp init`.
4. `offramp scan`.

Nothing else.
