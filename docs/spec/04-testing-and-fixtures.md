# 04. Testing and fixtures

Offramp is tested against real, tiny solutions checked into `tests/fixtures/`.
Each fixture is a scenario one or more commands must handle. Tests build the
fixture once per run (binlog cached in `tests/.cache/`, git-ignored), run the
command, and snapshot the JSON result with `Verify`.

## Fixture catalog

Each fixture directory contains a `README.md` stating what it exercises and
which commands have tests against it. All fixtures must build on ubuntu,
macos, and windows GitHub runners.

| Fixture | Contents | Exercises |
|---|---|---|
| `netfx-only` | `net48` library + console, packages.config-free, a few `System.Web`/`System.Drawing` usages | scan, graph, deps audit, audit api, gac |
| `dual-target` | `net48;net10.0` library, `netstandard2.0` library, `net10.0` console; `#if NETFRAMEWORK` blocks | scan, graph, move plan (destination compat), ifdef |
| `cycle` | two projects that reference each other through an assembly `HintPath` to the other's output | graph cycles, move plan refusal (OFR2001) |
| `tests-in-prod` | `Foo` with xunit tests under `Foo/Service/Tests/`, a `TestData/Builders.cs` helper used only by tests, a helper used by both prod and tests; `Foo.Tests` exists but is nearly empty; a second project `Bar` with no `Bar.Tests` | move tests, helper detection, project creation |
| `move-cases` | source `net48` project with files that: (a) move cleanly to a `netstandard2.0` dest, (b) need a package reference the dest lacks, (c) need a project reference, (d) would create a cycle, (e) use `System.Web` (unportable), (f) partial class across two files, (g) `.resx` + `.Designer.cs` pair, (h) reference internals | move plan/apply/rollback, purity, journal |
| `versions` | five projects with Newtonsoft.Json 9/11/12/13 and Microsoft.Extensions.* 6/8 mixed, one transitive constraint forcing a bump, one pinned project, `web.config` with binding redirects | deps audit, consolidate, redirects sync, pins, families |
| `cpm-shadowing` | root `Directory.Packages.props`, an unrelated project outside the solution under the root, a nested props file | consolidate preflight hazards (OFR1301–1303) |
| `loose-dlls` | `lib/` folder with a DLL that is actually a NuGet assembly and one that is another project's output | deps resolve-dlls |
| `windows-service` | `ServiceBase` service with `OnStart`/`OnStop`/`Timer`, `ProjectInstaller`, `EventLog`; a Topshelf service | service scaffold, kind detection |
| `windows-only-build-steps` | project with `GenerateSerializationAssemblies=On` and a COM reference; **excluded from default builds**, built only by tests that verify `doctor` detection using a hand-written binlog fixture | doctor, OFR0110–0119 |
| `behavior` | code hitting each behavior rule: culture compare, code pages, `Path` separators, `Registry`, `HttpContext.Current`, `Process.Start(url)`, `SqlClient`, `double.ToString`, `BinaryFormatter`, `Thread.Abort`, `AppDomain.CreateDomain`, `DllImport` | audit behavior/serialization/native/api |
| `seams` | a library where 3 of 12 types touch `System.DirectoryServices`; graph has an articulation point at `IDirectoryLookup` | seams, extract interface, remote |
| `dead-code` | public types unused anywhere, types used only via `Type.GetType("...")` string, types used by DI convention (`services.Scan`) | audit dead-code confidence levels |
| `mvc5` | ASP.NET MVC 5 + Web API 2 app with filters, routes, an `HttpModule`, `Global.asax` | web inventory/scaffold |
| `legacy-csproj` | old-style csproj with packages.config, `AssemblyInfo.cs`, explicit `Compile` items | csproj modernize (built only on windows runner or via committed binlog) |

The fixture generator (`tests/Offramp.Fixtures`) is a small library that can
also write parameterized fixtures to a temp directory for property-style tests
(e.g. "N projects in a chain, move a file from project i to project j").

## Snapshot hygiene

Scrubbers applied to every snapshot:
- `repositoryRoot`, `startedAt`, `durationMs`, `offramp.version`, `sdk.version`,
  `sha256` values, and cache paths.
- Ordering is not scrubbed. If a snapshot changes because ordering changed, the
  command lost determinism; fix the command.

Snapshots live in each test project's `Snapshots/` directory
(`*.verified.*`); `eng/accept-snapshots.sh` accepts pending `*.received.*`
files after review. Tests that trigger a diagnostic carry
`[ProducesDiagnostic("OFR####")]`, and a meta-test fails when a code in the
catalog has no such test. Test stack: `docs/decisions/0005-test-stack.md`.

## Categories

| Trait | Runs |
|---|---|
| (none) | every CI run, all three OSes |
| `Category=Windows` | windows runner only |
| `Category=Network` | needs nuget.org; CI runs it with a warm cache |
| `Category=Corpus` | `corpus.yml` on manual dispatch: NHibernate 4.x, DotNetNuke 8.x tags cloned and scanned; asserts no crashes and records counts |
| `Category=Slow` | > 60 s; nightly |

## Recorded feeds

Package commands are tested against recorded feeds, never live nuget.org
(`docs/decisions/0015-recorded-feeds.md`): `eng/record-feed.cs` records real
packages' structure (file paths, dependency groups, assembly identities and
references, listing, deprecation) into a fixture's `feed.json`, and tests
materialize it as a local folder feed of byte-identical `.nupkg` files with
metadata-only stub assemblies. Fixture builds still restore real packages from
nuget.org like every other fixture.

## Proving checks can fail

For every verification path there is a test that feeds a deliberately broken
input and asserts failure with the expected code:
- `verify` on a project with a compile error → exit 1, `OFR5001`.
- `move apply --verify end` where a moved file breaks the destination → rollback
  performed, `OFR2050`, working tree identical to before (asserted via
  `git status --porcelain` and content hashes).
- `deps consolidate --apply` producing NU1605 → not applied, `OFR1210`.

## Purity tests for movers

A move test runs inside a temporary git repository:
1. Commit the fixture.
2. Run the mover with `--apply`.
3. Assert `git status --porcelain` shows only `R ` (staged rename) entries for
   moved files and ` M` (unstaged modify) for project files.
4. Assert each moved file's SHA-256 equals its pre-move hash.
5. Assert `git diff --cached --stat -M` reports 100% similarity for every rename.
