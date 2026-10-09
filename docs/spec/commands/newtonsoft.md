# Newtonsoft.Json upgrade: `newtonsoft scan`, `newtonsoft tests`, `newtonsoft upgrade`

Legacy codebases often reference Newtonsoft.Json 7 to 12 through `packages.config`
and a `HintPath`, next to projects (or packages) built against 13, and paper
over the difference with binding redirects. Redirects work at run time only: the
compiler does not read `app.config`, so moving code between such projects, or
consolidating their versions, fails with CS1705 or changes behavior nobody
tested. These commands make the upgrade to 13.x a measured step:

1. `newtonsoft scan` finds every place the code serializes, deserializes, or
   configures Newtonsoft.Json, traces each to a method a test can call, and lists
   the behavior and API changes between the version in use and the target.
2. `newtonsoft tests` generates characterization tests ("probes") into the test
   projects that already cover the production code, and records what the
   current version does as baseline files.
3. `newtonsoft upgrade` moves every project to the target version, restores,
   builds, reruns the probes, and reports each one whose output changed.

The commands are Newtonsoft-specific by design (ADR 0065); what they know about
releases lives in data files (`rules/newtonsoft-*.yml`). No step uses an LLM.
Diagnostics are `OFR3700–3799` (an audit sub-range).

## Shared definitions

- **Version in use.** Per project and target framework: the resolved
  `Newtonsoft.Json` package (assets file or `packages.config`), else the version
  in a `Reference`'s identity or `HintPath` folder (`packages/Newtonsoft.Json.<v>/`;
  the folder names the version, not the assembly version, which is `<major>.0.0.0`).
  A loose DLL with no package behind it is `OFR3723` (run `deps resolve-dlls`
  first). Several versions in one solution is `OFR3722` (warning) and each project
  is judged against its own.
- **Target.** `newtonsoft.target` (default `[13.0,14.0)`) or `--version`: the
  newest stable version in the range whose `lib/` supports every target framework
  of every project that references Newtonsoft.Json ("supports target" as in
  `deps.md`). None: `OFR3760`.
- **Site.** An invocation, bound with the semantic model, of an API listed in
  `rules/newtonsoft-apis.yml` (by documentation ID), with its *direction*:
  `serialize` (`JsonConvert.SerializeObject`, `JsonSerializer.Serialize`,
  `JToken.FromObject`, ...), `deserialize` (`DeserializeObject`, `Deserialize`,
  `PopulateObject`, `ToObject`, ...), or `dom` (`JToken.Parse`/`Load`,
  `SelectToken`, `Value<T>`, ...). Configuration is reported separately:
  `JsonSerializerSettings` and `JsonSerializer` creations,
  `JsonConvert.DefaultSettings` assignments, and declared subclasses of
  `JsonConverter`, `IContractResolver`/`DefaultContractResolver`,
  `NamingStrategy`, and `ISerializationBinder`/`SerializationBinder`.
- **Payload type.** For `serialize`, the static type of the value argument;
  when it is `object`, an interface, or a type parameter, the concrete types passed
  to the containing method's parameter by its callers (the same tracing as `audit
  serialization`, depth 3). For `deserialize`, the type argument, the `Type`
  argument when it is a `typeof`, or the target of an enclosing cast. Unknown is
  `OFR3720`.
- **Settings provenance.** `none` (no settings argument), `inline` (an object
  creation with an initializer whose values are constants, enum members, `typeof`,
  or creations of public types with accessible parameterless constructors),
  `member` (a static field or property, readable from the test project), or
  `unknown` (a local assigned in several statements, a parameter, an instance
  member, anything else). `unknown` and any `DefaultSettings` assignment are
  `OFR3721` (info): probes of that site use the settings they can reproduce, and
  the report says so.
- **Probe.** One generated test method that exercises one (site or entry point,
  payload type, sample) and writes its observable result to a file. A *type
  probe* calls `JsonConvert`/`JsonSerializer` itself with the site's reproduced
  settings; an *entry probe* calls the production method found by tracing and
  never mentions Newtonsoft.Json.

## `newtonsoft scan`

```
offramp newtonsoft scan [--project P ...] [--version V] [--format table|json|markdown] [--out PATH]
```

Read-only. For every C# project that references Newtonsoft.Json (or the given
ones), using its recorded compilation for the preferred target framework:

1. **References**: version in use, how it is referenced (`packageReference`,
   `centralPackage`, `packagesConfig`, `hintPath`, `looseDll`), and the
   binding redirects of every application that deploys it.
2. **Sites and configuration**, as defined above, each with file, line,
   containing member, direction, payload types, and settings provenance.
3. **Entry point** for each site (below), or the reason there is none.
4. **API changes** by trial compilation: the project's recorded compilation with
   the target's `Newtonsoft.Json.dll` (the `lib/` folder NuGet would pick)
   substituted for the current one. Errors in the substituted compilation that
   the original does not have are `OFR3701` (error, "API removed or changed"),
   one per member, with the compiler's first message and every location; new
   `CS0618`/`CS0612` warnings are `OFR3702` (warning, "API obsolete").
   `rules/newtonsoft-breaking.yml` adds the replacement to the message where it
   knows one (`JsonConvert.SerializeObjectAsync` → `Task.Run` around
   `SerializeObject`). The target package comes from the global packages folder
   or the feeds; neither available is `OFR3725` and the step is skipped.
5. **Behavior changes** from `rules/newtonsoft-breaking.yml`. Every rule names
   the release that changed the behavior (`since`) and fires only for projects
   whose version in use is below `since` while the target is at or above it.
   A rule with a detector reports the code that is affected; a rule without one
   reports once per project (`OFR3719`, info), so the list of applicable
   release-note items is complete either way. Each rule ships with a differential
   test that runs its example under the version before `since` and under the
   target and fails unless the outputs differ (ADR 0066): a rule that cannot be
   shown to change behavior is not shipped.

Candidate detectors, from the release notes of 8.0.1 through 13.0.4 (each is
kept only if its differential test reproduces the change):

| Code | Since | What changed | Detector |
|---|---|---|---|
| OFR3703 | 11.0.1 | camel casing of all-caps words | a camel-case resolver or naming strategy in a site's settings, and payload members whose camel-cased name differs between the versions; the finding lists each `old → new` |
| OFR3704 | 11.0.1 | `ISerializable` types without `[Serializable]` are serialized as objects | such a type among the payload types |
| OFR3705 | 11.0.1, 13.0.1 | enum dictionary keys honor `[EnumMember]` and the naming strategy | `IDictionary<TEnum, …>` payload members whose enum has `[EnumMember]`, or a naming strategy in the settings |
| OFR3706 | 11.0.1, 13.0.3 | decimals keep trailing zeros when read | `decimal` payload members at deserialize sites |
| OFR3707 | 13.0.1 | a mismatched `JToken` conversion throws `JsonSerializationException`, not `InvalidCastException` | a `catch (InvalidCastException)` whose `try` contains a `ToObject`/`Value<T>`/explicit `JToken` conversion |
| OFR3708 | 10.0.1 | `StringEnumConverter.AllowIntegerValues = false` also rejects integer strings and unnamed values | that assignment or constructor argument |
| OFR3709 | 13.0.1 | `MaxDepth` defaults to 64 | a payload type that contains itself (directly or through members or collections) at a site without an explicit `MaxDepth` |
| OFR3710 | 12.0.1 | `DateTime.Kind` is kept from ISO strings; `JTokenReader` honors `DateTimeZoneHandling` | `DateTime` payload members at deserialize sites, `JToken.ToObject` of them |
| OFR3711 | 11.0.2 | `[DataContract]` no longer forces a collection type to an object contract | a `[DataContract]` payload type implementing `IEnumerable` |
| OFR3712 | 12.0.1 | `TargetSite` is not written for exceptions without `[Serializable]` | an `Exception` payload type |
| OFR3713 | 12.0.3 | `IImmutableSet`/`IImmutableDictionary` deserialize to the hash variants | payload members of those interface types |

Severities are the defaults; `rules:` in `offramp.yml` overrides them as for
audits. Findings carry `since` and the release-note line.

Result (`schemas/v1/newtonsoft-scan.json`):

```jsonc
{
  "target": "13.0.4",
  "projects": [
    {
      "project": "src/Orders/Orders.csproj", "targetFramework": "net48",
      "newtonsoft": { "version": "7.0.1", "via": "packagesConfig", "hintPath": "packages/Newtonsoft.Json.7.0.1/lib/net45/Newtonsoft.Json.dll" },
      "redirects": [ { "app": "src/Orders.App/Orders.App.csproj", "file": "src/Orders.App/App.config", "oldVersion": "0.0.0.0-7.0.0.0", "newVersion": "7.0.0.0" } ],
      "sites": [
        {
          "id": "Orders.OrderCodec.Write#1", "file": "src/Orders/OrderCodec.cs", "line": 14,
          "member": "M:Orders.OrderCodec.Write(Orders.Order)", "api": "M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonSerializerSettings)",
          "direction": "serialize", "payloadTypes": [ "Orders.Order" ],
          "settings": { "kind": "member", "symbol": "F:Orders.OrderCodec.Settings", "file": "src/Orders/OrderCodec.cs", "line": 9 },
          "entryPoint": { "member": "M:Orders.OrderCodec.Write(Orders.Order)", "depth": 0 },
          "uncovered": null
        }
      ],
      "configuration": [ { "kind": "settings", "file": "src/Orders/OrderCodec.cs", "line": 9, "symbol": "F:Orders.OrderCodec.Settings" } ]
    }
  ],
  "findings": [ /* the audit finding shape, plus "since" */ ],
  "summary": { "projects": 3, "sites": 12, "withEntryPoint": 7, "findings": { "error": 1, "warning": 4, "info": 6 } }
}
```

Site ids are `<containing type>.<member>#<n>`, `n` counting sites in that member
in source order; they are stable while the member's sites keep their order, and
probe ids derive from them.

### Entry points

A test can exercise a site through the site's own member or through a member
that calls it. The finder walks a method-level call graph built from every
project's recorded compilation (calls, object creations, property accesses, and
calls inside lambdas and local functions, attributed to the enclosing member;
calls through an interface or base member count for every implementation or
override). Breadth first from the site's member, up to `newtonsoft.tests.entryDepth`
callers (default 4), it takes the first member that is:

1. **accessible** from the test project: public with public containing types, or
   internal with `InternalsVisibleTo` for the test assembly (`notAccessible`);
2. **constructible**: static, or declared on a concrete type with a constructor
   whose parameters can all be generated (`notConstructible`; interfaces and
   abstract parameter types cannot be in v1, there are no mocks);
3. **callable**: every parameter can be generated, or is an output the probe can
   read back (`Stream`, `TextWriter`, `StringBuilder`, `out`/`ref`) (`typeNotGeneratable`);
4. **hermetic**: nothing it calls, transitively up to 6 levels, is in
   `rules/newtonsoft-hermetic.yml` or `newtonsoft.hermetic.nonHermetic` (files,
   directories, network, databases, `DateTime.Now`/`UtcNow`, `Guid.NewGuid`,
   `Random`, `Environment`, `ConfigurationManager`, `Thread.Sleep`, console
   input) (`nonHermetic`, with the API and where it is called);
5. **observable**: returns a value or has an output parameter (`notObservable`).

Ties at the same depth go to the ordinal-first documentation ID. A site with no
such member gets the first reason that ruled out the nearest candidate, or
`depthExceeded`. Every site has exactly one of `entryPoint` and `uncovered`.

## `newtonsoft tests`

```
offramp newtonsoft tests [--project P ...] [--to TESTPROJECT] [--samples DIR]
                         [--record | --verify] [--apply]
```

Generates probes for the selected production projects (default: every project
`scan` finds sites in), records baselines, or checks them.

**Test project.** For each production project, the project in
`newtonsoft.tests.projects`, else `--to` (only with a single `--project`), else
the test project that references it, chosen as `move tests` chooses (ADR 0045):
several is `OFR3734` (error, candidates listed), none is `OFR3733` (error). Test
projects are not created. The framework is detected from the test project's
references: xunit (2 and 3), NUnit (3 and 4), MSTest (v1
`Microsoft.VisualStudio.QualityTools.UnitTestFramework` and v2); anything else
is `OFR3736` and the project is skipped.

**What is generated** (under `newtonsoft.tests.folder`, default
`NewtonsoftCharacterization/`, in the test project's folder):

- `OfframpProbe.cs` and `OfframpObjectDump.cs`: the probe runtime, once per
  test project. C# 6, no dependencies beyond the BCL: it compiles in a legacy
  project at `LangVersion` 6 and needs no package.
- `<Production>SerializeProbes.cs`, `<Production>DeserializeProbes.cs`,
  `<Production>EntryProbes.cs`: one test class each, `[Category]`/`[Trait]`/
  `[TestCategory]` `NewtonsoftCharacterization` on every method.
- `Inputs/<probe>.json`: the `--samples` files a probe reads.
- `Baseline/<runtime>/<probe>.txt`: written by `--record`, never by generation.

Every generated file starts with an `<auto-generated>` header naming the command.
A file whose header was removed belongs to the user: it is not regenerated or
deleted (`OFR3742`, info). Regenerating rewrites changed files (edits), deletes
generated files that are no longer produced, and leaves the rest untouched, so a
second run with the same inputs changes nothing. Legacy (non-SDK) test projects
get `Compile` items for the `.cs` files; nothing else is added to any project
file. Before anything is written, the generated files are compiled into the test
project's recorded compilation: a probe that does not compile is dropped
(`OFR3735`, with the first error), never written.

**Type probes** reference Newtonsoft.Json directly, so they are generated only
when the test project's compilation binds the same Newtonsoft.Json assembly
identity as the production project's (`OFR3743` otherwise, and only entry probes
are generated for it). `upgrade` moves test projects with production ones, so the
identities stay equal.

**Samples.** For each payload type, `newtonsoft.tests.samplesPerType` (default
3) instances written as C# object initializers: `typical` (every settable member
set), `empty` (nulls and empty collections where allowed), `edge` (values the
release notes name: decimals with trailing zeros such as `1.10m` and `0.0m`,
`DateTime` with `Utc` and `Unspecified` kinds, a `DateTimeOffset` with a
non-zero offset, an enum value with no name, enum-keyed dictionaries, `-0.0`,
`double.MaxValue`, strings with escapes and non-ASCII characters, `Uri` with an
unescaped original string). Values come from a seed derived from the type's full
name, so the same type gets the same samples everywhere. `DateTime` values are
never `Local` (they would depend on the machine's time zone). Members are set
through public setters or the constructor whose parameter names match them;
abstract member types take the solution's single concrete implementation, else
`null`; nesting stops at `newtonsoft.tests.objectDepth` (default 3). A probe is
generated once per (payload type, settings).

**Real payloads** (`--samples DIR` or `newtonsoft.samples`): files named
`<payload type full name>[.<label>].json` (`<type>[].json` for a collection
root) become inputs of the deserialize probes of that type and are copied into
`Inputs/`; a file that matches no payload type, or is not JSON, is `OFR3737`. A
deserialize-only site whose type has no sample and is not serialized anywhere is
`OFR3732` (warning): without a payload its probe would test a shape the code
never sees. The dry run lists every file to be copied: payloads can hold data
the repository should not.

**What a probe records.** Serialize probes record the JSON text. Deserialize
probes deserialize their input (a sample file, or the recorded output of the
serialize probe of the same type and settings) and record a dump of the
resulting object: public instance properties and fields in ordinal order;
decimals in invariant form (which keeps the scale), doubles and floats
round-trippable with negative zero shown, `DateTime` in round-trip form with
its `Kind`, `DateTimeOffset` in round-trip form, byte arrays as length and
base64, `Uri` as its original string, enums as name and value, `JToken` values
by their JSON text (found by type name, so the runtime needs no reference),
the runtime type where it differs from the declared one, reference cycles as a
back-reference, and nesting to a fixed depth. A probe that throws records the
exception's type and message instead. The current culture is invariant while a
probe runs. Every result file carries the Newtonsoft.Json assembly version that
actually ran (ADR 0067).

Baseline file:

```
# offramp-newtonsoft-probe 1
probe: Orders.OrderCodec.Write#1/Orders.Order/edge
newtonsoft: 7.0.0.0
outcome: returned
---
{"urlValue":"u","total":1.10,...}
```

UTF-8 without BOM, LF line endings (CRLF is normalized when read, for checkouts
with `autocrlf`).

**Recording** (`--record`, needs `--apply` to write baselines):

1. Build the test projects (`verify` machinery, `verify.mode: build`).
2. Run the serialize probes, then the deserialize and entry probes, twice
   (`newtonsoft.tests.runs`), with `OFFRAMP_NEWTONSOFT_MODE=record` and
   `OFFRAMP_NEWTONSOFT_RESULTS` pointing into `.offramp/newtonsoft/runs/<n>/`.
   Probes write only there; a deserialize probe whose input is a serialize
   probe's output reads it from the same run.
3. A probe whose two runs differ is `OFR3738` (warning, quarantined: no baseline
   is written, and the differing lines are reported). A result recorded under a
   Newtonsoft.Json version other than the production project's version in use
   is `OFR3745` (error, nothing written): the test project resolved another
   version, as SDK-style test projects on modern .NET do through
   `Microsoft.NET.Test.Sdk` 17.x (ADR 0067).
4. The results become `Baseline/<runtime>/<probe>.txt` through a change set
   (journaled, previewed, undone with `move rollback`). `<runtime>` is `netfx` or
   `core`: the two runtimes format some doubles differently, and each compares only
   with itself.

**Verifying** (`--verify`): build, run once with `OFFRAMP_NEWTONSOFT_MODE=verify`
(deserialize probes read their input from the baseline, so inputs stay those the
old version wrote), and compare each result with its baseline. Statuses: `same`,
`differs`, `nowThrows`, `noLongerThrows`, `messageOnly` (`OFR3763`, info: only an
exception message changed), `missing` (`OFR3741`, did not run), `noBaseline`
(`OFR3744`), `quarantined`. Any `differs`, `nowThrows`, or `noLongerThrows` is
`OFR3762` (warning, per probe, with a line diff) and exit 1.

**Running.** SDK-style test projects: `dotnet test <project> --no-build -f <tfm>
--filter FullyQualifiedName~.NewtonsoftCharacterization.`; legacy ones: `dotnet
test <output assembly> --test-adapter-path <the adapter's packages folder>`.
`newtonsoft.tests.command` replaces both (placeholders `{project}`,
`{assembly}`, `{framework}`, `{filter}`, `{results}`). The pass or fail of the
test run is not the verdict: the result files are. A run that fails to build or
start is `OFR3740` (error, output tail attached). .NET Framework test targets
run on Windows only: elsewhere they are skipped with `OFR3739` (warning), which
gives the command to run on Windows; a test project that also targets modern
.NET runs that target instead (ADR 0067). There is no Mono support.

Result (`schemas/v1/newtonsoft-tests.json`): `targets[]` (production project,
test project, framework, how it was chosen), `probes[]` (id, kind `type|entry`,
direction, site, payload type, sample, input `generated|sample|roundtrip`, file),
`uncovered[]` (site, reason), `files[]`, `preview`, `applied`, `journal`, `runs[]`
(runtime, framework, command line, exit code, per-probe status), and `coverage`
(sites, with a type probe, with an entry probe, uncovered by reason).

## `newtonsoft upgrade`

```
offramp newtonsoft upgrade [--version V] [--project P ...] [--on-diff keep|rollback] [--apply] [--yes]
```

Dry run by default: plans every edit and prints it as one diff.

1. **Plan** the target (above) and, for every project referencing
   Newtonsoft.Json (test projects included):
   - PackageReference and central package versions: as `deps consolidate
     --package Newtonsoft.Json` would write them, with the target as a pin.
   - `packages.config`: the `version` attribute of the `Newtonsoft.Json` entry,
     edited in place (every other byte kept). An `allowedVersions` that excludes
     the target is `OFR3764` and the project is not changed.
   - `Reference` items: the identity's `Version=` becomes the target's assembly
     version, and the `HintPath` folder `Newtonsoft.Json.<old>/lib/<tfm>` becomes
     `Newtonsoft.Json.<new>/lib/<nearest>`, where `<nearest>` is the folder of
     the target package that NuGet would pick for the project's framework (read
     from the package, not guessed). Separators and the relative prefix are kept.
   - Binding redirects: `redirects sync` over a model in which Newtonsoft.Json is
     at the target, with the target package in the global packages folder (put
     there first, outside the repository).
   - A package dependency whose range excludes the target is `OFR3765` (the
     chain is shown).
2. **Apply** (`--apply`): write the change set with a journal; restore
   (`dotnet restore`, and `packages.config` restore); a restore failure is
   `OFR3766` and rolls back.
3. **Build** the changed projects and their dependents (`verify`). A failure rolls
   back (`OFR3761`, error) and maps each compiler error to the `OFR3701` finding
   that predicted it, when one did.
4. **Compare**: `newtonsoft tests --verify` for every test project with
   baselines. Differences are `OFR3762` and exit 1; the upgrade stays applied
   (`--on-diff keep`, default, `newtonsoft.onDiff`) so the differences can be
   examined, or is rolled back (`rollback`). No baselines at all is `OFR3767`
   (warning): the upgrade was checked by the compiler only.

Result (`schemas/v1/newtonsoft-upgrade.json`): `from` (versions in use),
`to`, `projects[]` (project, from, how it was changed), `redirects` (the
`redirects sync` result), `verify`, `probes[]` (id, status, diff), `findings`
(the scan findings that applied), `applied`, `journal`, `rolledBack`, `preview`.

## Configuration

```yaml
newtonsoft:
  target: "[13.0,14.0)"        # --version overrides
  samples: null                # folder of real payloads; --samples overrides
  onDiff: keep                 # keep | rollback
  tests:
    folder: NewtonsoftCharacterization
    projects: {}               # production project -> test project
    samplesPerType: 3
    objectDepth: 3
    entryDepth: 4
    runs: 2
    command: null              # replaces the dotnet test invocation
    timeoutSeconds: 1800
  hermetic:
    nonHermetic: []            # documentation IDs (T:, M:, N:) treated as non-hermetic
```

## Diagnostics summary

| Code | Severity | Meaning |
|---|---|---|
| OFR3701 | error | API removed or changed in the target (trial compilation) |
| OFR3702 | warning | API obsolete in the target (trial compilation) |
| OFR3703–3713 | per rule | behavior change with a detector (table above) |
| OFR3719 | info | behavior change between the versions with no detector |
| OFR3720 | warning | payload type unknown |
| OFR3721 | info | settings cannot be reproduced, or `DefaultSettings` is assigned |
| OFR3722 | warning | Newtonsoft.Json at several versions in the solution |
| OFR3723 | warning | Newtonsoft.Json referenced as a loose DLL |
| OFR3724 | info | project already at the target |
| OFR3725 | warning | target package unavailable; API changes not computed |
| OFR3731 | warning | site has no probe (reason) |
| OFR3732 | warning | deserialize-only site without a sample payload |
| OFR3733 | error | no test project covers the production project |
| OFR3734 | error | several test projects cover the production project |
| OFR3735 | warning | generated probe does not compile; dropped |
| OFR3736 | warning | test framework not supported |
| OFR3737 | warning | sample file matches no payload type or is not JSON |
| OFR3738 | warning | probe output differs between runs; quarantined |
| OFR3739 | warning | .NET Framework tests cannot run on this OS |
| OFR3740 | error | test build or run failed |
| OFR3741 | warning | probe did not run |
| OFR3742 | info | generated file taken over by the user; left alone |
| OFR3743 | warning | test project binds another Newtonsoft.Json; type probes skipped |
| OFR3744 | warning | probe has no baseline |
| OFR3745 | error | probe ran under another Newtonsoft.Json version than production |
| OFR3760 | error | no target version supports every framework |
| OFR3761 | error | build failed after the upgrade; rolled back |
| OFR3762 | warning | probe output changed |
| OFR3763 | info | only an exception message changed |
| OFR3764 | warning | `allowedVersions` in packages.config excludes the target |
| OFR3765 | warning | a package dependency excludes the target |
| OFR3766 | error | restore failed after the upgrade; rolled back |
| OFR3767 | warning | no baselines; upgrade checked by the compiler only |

## Not in v1

TUnit and Visual Basic; Mono; reproducing `JsonConvert.DefaultSettings`; mocks
for interface-typed parameters; payloads serialized implicitly by Web API, MVC,
or SignalR formatters (reported through their settings only); code fixes for
removed APIs; `Newtonsoft.Json.Bson`; targets other than 13.x; any LLM step.
