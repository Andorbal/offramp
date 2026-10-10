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
  output and the run command Offramp uses.

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

Coverage is low (1 of 51 production sites gets a probe), and most of that is MassTransit, not
Offramp: a message bus serializes `object` and internal envelope types, which no test can construct
and no rule can type. The report says where Offramp could do better (finding #6).

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
