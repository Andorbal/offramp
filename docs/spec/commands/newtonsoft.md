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
5. **Behavior changes** from `rules/newtonsoft-breaking.yml`. A rule describes
   one behavior as ordered version **segments**, each with a label: `[, 11.0.1)
   isSerializable`, `[11.0.1, ) objectContract`. Several behaviors changed and
   later changed back (decimals lost their trailing zeros in 10.x only), so a
   rule fires when the label of the version in use differs from the label of the
   target, not when a release lies between them. A rule with a detector reports
   the code that is affected; a rule without one (the behavior depends on data,
   not code) reports once per project (`OFR3719`, info). Every rule carries an
   example, a C# method returning a string, and a differential test runs it
   under every Newtonsoft.Json release from 7.0.1 to the newest 13.x: outputs
   must be equal exactly where the labels are equal (ADR 0066, ADR 0068). A
   release-note line that no example reproduces is not a rule.

Rules (the segments were measured, not read from the notes; ADR 0068 has the
matrix):

| Code | Segments | What changed | Detector |
|---|---|---|---|
| OFR3703 | `[, 8.0.3)`, `[8.0.3, 11.0.1)`, `[11.0.1, )` | camel casing of names with spaces: `"A B"` became `"a B"` (8.0.3), `"URL VALUE"` became `"url VALUE"` (11.0.1) | a camel-case resolver or naming strategy in a site's settings and a payload member whose JSON name (its `[JsonProperty]` name or its own) camel-cases differently; the finding lists each `old → new`. String-keyed dictionary members are reported as data-dependent (`OFR3719`) |
| OFR3704 | `[, 11.0.1)`, `[11.0.1, )` | an `ISerializable` type without `[Serializable]` is written and read through its public members instead of `GetObjectData` and the serialization constructor | such a type among the payload types |
| OFR3705 | `[, 11.0.1)`, `[11.0.1, )` | enum dictionary keys use `[EnumMember]` values (writing; reading them stopped throwing) | an `IDictionary<TEnum, …>` payload member whose enum has `[EnumMember]` |
| OFR3706 | `[, 10.0.1)`, `[10.0.1, 11.0.1)`, `[11.0.1, 13.0.2)`, `[13.0.2, 13.0.3)`, `[13.0.3, )` | decimals read from JSON keep their scale (`1.10`), except in 10.x (`1.1`) and for zero in 13.0.2 (`0`) | a `decimal` payload member at a deserialize site |
| OFR3707 | per exception (rule data) | an exception the code catches is no longer the one thrown: `JToken.ToObject` of a mismatched token throws `JsonSerializationException`, not `InvalidCastException` (13.0.1); an invalid `\u` escape throws `JsonReaderException`, not `FormatException` (10.0.1) | a `catch` of the old type whose `try` block contains the API |
| OFR3708 | `[, 10.0.1)`, `[10.0.1, )` | `StringEnumConverter { AllowIntegerValues = false }` also rejects integer strings when reading and unnamed values when writing | that converter in a site's settings |
| OFR3709 | `[, 13.0.1)`, `[13.0.1, )` | readers stop at a depth of 64 by default (`JsonReaderException`) | a payload type that contains itself, at a deserialize or DOM site whose settings do not set `MaxDepth` |
| OFR3710 | `[, 12.0.1)`, `[12.0.1, )` | `JToken.ToObject` with a serializer honors its `DateTimeZoneHandling` | `ToObject(Type, JsonSerializer)`/`ToObject<T>(JsonSerializer)` where the payload has `DateTime` members |
| OFR3711 | `[, 11.0.1)`, `[11.0.1, 11.0.2)`, `[11.0.2, )` | a `[DataContract]` collection was written as an object in 11.0.1 only | a `[DataContract]` payload type implementing `IEnumerable` |
| OFR3712 | `[, 11.0.1)`, `[11.0.1, 12.0.1)`, `[12.0.1, )` | serializing an exception type without `[Serializable]` threw in 11.x | such an exception type among the payload types |
| OFR3713 | `[, 12.0.3)`, `[12.0.3, )` | `IImmutableSet<T>`/`IImmutableDictionary<K,V>` deserialize to the hash variants instead of the sorted ones | payload members of those interface types |

Data-dependent changes reported through `OFR3719`: comments inside a parsed
`JArray` are no longer items (`[, 8.0.1)`), negative zero and decimal overflow
in narrow ranges (13.0.2; 10.x–11.x), and camel-cased dictionary keys with spaces.

Severities are the defaults; `rules:` in `offramp.yml` overrides them as for
audits. Findings carry the labels of both versions and the release-note line.

Result (`schemas/v1/newtonsoft-scan.json`):

```jsonc
{
  "target": "13.0.4",
  "projects": [
    {
      "project": "src/Orders/Orders.csproj", "targetFramework": "net48",
      "newtonsoft": { "version": "7.0.1", "via": "packagesConfig", "hintPath": "packages/Newtonsoft.Json.7.0.1/lib/net45/Newtonsoft.Json.dll" },
      "redirects": [],
      "sites": [
        {
          "id": "Orders.OrderCodec.Write#1", "file": "src/Orders/OrderCodec.cs", "line": 14,
          "member": "M:Orders.OrderCodec.Write(Orders.Order)", "api": "M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonSerializerSettings)",
          "direction": "serialize", "payloadTypes": [ "Orders.Order" ],
          "settings": { "kind": "member", "symbol": "F:Orders.OrderCodec.Settings", "file": "src/Orders/OrderCodec.cs", "line": 9 },
          "entryPoint": { "member": "M:Orders.OrderCodec.Write(Orders.Order)", "project": "src/Orders/Orders.csproj", "depth": 0, "path": [ "M:Orders.OrderCodec.Write(Orders.Order)" ] },
          "uncovered": null
        }
      ],
      "configuration": [ { "kind": "settings", "file": "src/Orders/OrderCodec.cs", "line": 9, "symbol": "F:Orders.OrderCodec.Settings" } ]
    }
  ],
  "findings": [ /* the audit finding shape, plus "current" and "target" labels */ ],
  "summary": { "projects": 3, "sites": 12, "withEntryPoint": 7, "findings": { "error": 1, "warning": 4, "info": 6 } }
}
```

Site ids are `<containing type>.<member name>#<n>`, `n` counting the type's
sites in members of that name (overloads share a name) in file and position
order; they are stable while those sites keep their order, and probe ids derive
from them. `via` is one of `packageReference`, `centralPackage`,
`packagesConfig`, `hintPath`, `looseDll`, and `transitive` (through another
project or package). `redirects` are the project's own: those in its `app.config`
(`web.config` for a web project), so an application lists the redirects it
deploys. A local initialized once and never assigned again has the provenance
of its initializer. A behavior finding is reported once per payload member or
type, at the first site (in file order) that carries it, and names that site.

With no feed to ask (every source unreachable), the target is the newest
release the rule pack measures (13.0.4) when it is in the range, and `OFR3725`
says the trial compilation needs that release's package.

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
5. **observable**: returns a value or has an output parameter (`notObservable`);
6. for deserialize and DOM sites, **fed**: the JSON the site reads comes from one
   of its parameters, followed through `StringReader`, `JsonTextReader`,
   `StreamReader`, `Encoding.GetBytes`, parsed tokens, and locals initialized
   once (`inputNotParameter`). `entryPoint.input` names that parameter.

Callers in test projects are not candidates. Ties at the same depth go to the
ordinal-first documentation ID. A site with no such member is `uncovered` with
`depthExceeded` when callers remained beyond the depth limit, otherwise with the
reason that ruled out the site's own member; `candidates` lists the members
examined (nearest first, at most ten) with each one's reason. Every site has
exactly one of `entryPoint` and `uncovered` (ADR 0070).

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
- `<Production>SerializeProbes.cs` and `<Production>DeserializeProbes.cs`: one
  test class each per production project, writing probes in the first and
  reading (deserialize and DOM) probes in the second, each method marked
  `NewtonsoftCharacterization` (`[Trait("Category", …)]`, `[Category]`,
  `[TestCategory]`). Every type is named with `global::`, so nothing depends on
  the test project's usings.
- `Inputs/<file>.json`: the `--samples` files the probes read.
- `Baseline/<runtime>/<probe>.txt`: written by `--record`, never by generation.

Every generated file starts with an `<auto-generated>` header naming the command.
A file whose header was removed belongs to the user: it is not regenerated or
deleted, and the probes it would hold are not written (`OFR3742`, info).
Regenerating rewrites changed files (edits), deletes generated files that are no
longer produced (baselines are never touched), and leaves the rest untouched, so
a second run with the same inputs changes nothing. Legacy (non-SDK) test projects
get `Compile` items for the `.cs` files; nothing else is added to any project
file. Before anything is written, the generated files are compiled into each of
the test project's recorded compilations (earlier generated files replaced): a
probe that does not compile is dropped with every probe that reads what it
writes (`OFR3735`, with the first error), never written.

**Which probes.** A site with an entry point gets *entry probes*: they call the
entry point (static, or on an instance made with its cheapest constructor) with
generated arguments, read back `out`/`ref` parameters and a `Stream`,
`TextWriter`, or `StringBuilder` passed in empty, and observe the return value.
A site without one gets *type probes* when the test project can write them:
they call `JsonConvert` (`JsonSerializer` when the site's settings are a
serializer, `JToken.FromObject`/`ToObject` when the site uses those) with the
site's settings written out (`none`, `inline` rewritten from the creation, or the
accessible static `member` itself) and the payload type. Type probes reference
Newtonsoft.Json directly, so they are generated only when the test project's
compilation binds the same Newtonsoft.Json assembly identity as the production
project's (`OFR3743` otherwise); `upgrade` moves test projects with production
ones, so the identities stay equal. Probe ids are `<site id>.<entry|type|input>[<k>].<sample>`
(`k` numbers payload types when a site has several), made of letters, digits,
and `. # - _` so they are file names on every OS.

**Inputs of reading probes**, all that apply:

1. real payloads named after the payload type (below): one probe each,
   `sample[-<label>]`;
2. for a direct deserialize (not `ToObject`, not DOM), what a writing probe of the
   same payload type and the same settings (the same static member, the same
   inline creation, or none) records: a type probe, or an entry probe of the
   site's own member that returns a string and takes the payload. One probe per
   label, `roundtrip-<label>`, reading that probe's output;
3. when nothing writes the payload that way and the test project can write type
   probes, *input probes* (`<site>.input.<label>`, in the writing class) write a
   generated sample with the reading site's own settings, and the reading probes
   read that.

A reading site with none of these is uncovered (`OFR3732`). A DOM site reads
JSON of no known type, so only a real payload could feed it, and none is named
after a type: DOM sites are uncovered in v1. Every uncovered site has a reason:
the scan's (`notAccessible`, …, `depthExceeded`), or `noInput`,
`payloadNotGeneratable`, `settingsNotReproducible`, `newtonsoftNotBound`,
`doesNotCompile` (`OFR3731`, `OFR3732`).

**Samples.** For each payload type, `newtonsoft.tests.samplesPerType` (default
3) values written as C# 6 expressions: `typical` (every settable member set),
`empty` (references null, collections empty, values default), `edge` (values the
releases disagree about: `1.10m`, `-0.0`, `MaxValue` integers, an enum value with
no name, `DateTime` in `Unspecified` kind with milliseconds, a `DateTimeOffset`
with a negative fractional offset, strings with quotes, escapes, and non-ASCII
characters, a `Uri` with an unescaped original string). Typical values come from
a seed derived from the type and member names (FNV-1a), so the same type gets
the same samples everywhere. `DateTime` values are never `Local` (they would
depend on the machine's time zone). Members are set through accessible setters
(and the constructor with the fewest generatable parameters); abstract member
types take the compilation's single concrete implementation, else none;
collections get two items (one for `edge`), dictionaries distinct keys; nesting
stops at `newtonsoft.tests.objectDepth` (default 3) with null.

**Real payloads** (`--samples DIR` or `newtonsoft.samples`): files named
`<payload type full name>[.<label>].json` (`<element type>[].json` for a
collection root) become inputs of the reading probes of that type and are copied
into `Inputs/`; a file that matches no payload type of a reading site, or is not
JSON, is `OFR3737`. The dry run lists every file to be copied: payloads can hold
data the repository should not.

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

**Recording** (`--record`; baselines are written with `--apply`, previewed without):

1. The probes on disk must be what generation writes now: with `--apply` they are
   written first, without it a difference is `OFR3746` (exit 2).
2. Build the test projects (`verify` machinery, build mode; a failure is `OFR3740`).
3. Run each test project's writing probes (`<Production>SerializeProbes`), then
   its reading probes (`<Production>DeserializeProbes`), `newtonsoft.tests.runs`
   times (default 2), with `OFFRAMP_NEWTONSOFT_MODE=record`,
   `OFFRAMP_NEWTONSOFT_RESULTS` set to `.offramp/newtonsoft/runs/record-<n>/`, and
   `OFFRAMP_NEWTONSOFT_DIR` set to the generated folder (for builds whose
   `[CallerFilePath]` is mapped). Probes write only there; a reading probe whose
   input is a writing probe's output reads it from the same run.
4. A probe whose runs differ is `OFR3738` (warning, quarantined: no baseline is
   written, and the differing lines are shown). A probe with no result is
   `OFR3741`. A result recorded under a Newtonsoft.Json assembly version other
   than the production project's (`<major>.<minor>.0.0` of its release) is
   `OFR3745` (error) and nothing is written for that test project: the test
   project resolved another version, as SDK-style test projects on modern .NET do
   through `Microsoft.NET.Test.Sdk` 17.x (ADR 0067).
5. The results become `Baseline/<runtime>/<probe>.txt` through a change set with
   its own journal (`baselineJournal`, undone with `move rollback`); baselines of
   probes that no longer exist are deleted. `<runtime>` is `netfx` or `core`: the
   two runtimes format some doubles differently, and each compares only with
   itself.

**Verifying** (`--verify`): the probes on disk must be current (`OFR3746`); build;
run once into `.offramp/newtonsoft/runs/verify/` with
`OFFRAMP_NEWTONSOFT_MODE=verify` (reading probes read their input from the
baselines, so inputs stay those the old version wrote); compare each result with
its baseline. Statuses: `same`, `differs`, `nowThrows`, `noLongerThrows`,
`messageOnly` (`OFR3763`, info: only an exception message changed), `missing`
(`OFR3741`), `noBaseline` (`OFR3744`). Any `differs`, `nowThrows`, or
`noLongerThrows` is `OFR3762` (error, one per test project, with the probes
listed and the first line diff) and exit 1.

**Running.** SDK-style test projects: `dotnet test <project> --no-build -c
<verify.configuration> -f <tfm> --filter FullyQualifiedName~.<folder>.<class>.`;
legacy ones: `dotnet test <output assembly> --test-adapter-path <output folder>`.
`newtonsoft.tests.command` replaces both, run through the shell (placeholders
`{project}`, `{assembly}`, `{framework}`, `{filter}`, `{results}`), with the same
environment. The pass or fail of the test run is not the verdict: the result
files are (a recording run that exits non-zero is still `OFR3740`). A test project
runs its first modern .NET target (results under `core`); one that targets only
.NET Framework runs on Windows (`netfx`) and elsewhere is skipped with `OFR3739`
(warning), which gives the command to run on Windows (ADR 0067). There is no Mono
support.

Result (`schemas/v1/newtonsoft-tests.json`): `targets[]` (production project,
test project, framework, how it was chosen, folder), `probes[]` (id, kind
`entry|type`, direction, site, test project, sample, input
`generated|sample|roundtrip`, where the input comes from, file), `uncovered[]`
(site, project, reason, detail), `files[]` (path, `create|edit|delete|unchanged|kept`),
`coverage` (sites, with an entry probe, with a type probe, uncovered by reason),
`preview`, `applied`, `journal`, `mode` (`generate|record|verify`), `runs[]` (test
project, framework, runtime, phase `write|read`, results folder, command line, exit
code), `outcomes[]` (probe, test project, runtime, status, the Newtonsoft.Json
version that ran, diff), and `baselineJournal`.

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
     there first, outside the repository). A redirect that points at a version the
     application no longer deploys (`0.0.0.0-7.0.0.0 → 7.0.0.0` after the upgrade)
     is moved to the deployed one (`OFR1502`): left alone, anything built against
     an older release would fail to load. Only the `Newtonsoft.Json` redirects are
     synced: another assembly's out-of-date redirect was so before the upgrade and
     is `redirects sync`'s to change (ADR 0073).
   - Projects that get Newtonsoft.Json from another project are not edited
     (`transitive`); test projects are upgraded with production ones, so type probes
     keep compiling against the same assembly.
   - A package dependency whose range excludes the target is `OFR3765` (the
     package and its range are named).
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

Result (`schemas/v1/newtonsoft-upgrade.json`): `to`, `from` (each release in use
with its projects), `projects[]` (project, release, reference style, files edited),
`redirects` (the `redirects sync` result), `findings` (what `scan` predicts for the
projects), `verify` (the build; null in a dry run), `probes[]` (probe, test project,
runtime, status, diff), `onDiff`, `applied`, `journal`, `rolledBack`, `preview`.
The build covers what `verify.projects` selects (the solution by default).

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
| OFR3719 | info | behavior change that depends on data, not code (no detector) |
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
| OFR3746 | error | generated probes are out of date |
| OFR3760 | error | no target version supports every framework |
| OFR3761 | error | build failed after the upgrade; rolled back |
| OFR3762 | error | probe observes a behavior change |
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
