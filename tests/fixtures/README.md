# Fixtures

Real, tiny solutions that Offramp's tests scan, build, and move. Each directory
is a standalone repository: tests copy it to a temporary directory, initialize
git there, and run the command under test. See
`docs/spec/04-testing-and-fixtures.md` for the catalog.

The `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
and `.editorconfig` in this folder stop MSBuild and the analyzers from picking up
Offramp's own settings when a fixture is built in place.

| Fixture | Exercises |
|---|---|
| `netfx-only` | scan, graph, deps audit, audit api, deps gac |
| `dual-target` | scan (including a Windows-captured compiler log), graph, move plan, ifdef, audit api-compat |
| `cycle` | graph cycles, move plan refusal |
| `windows-only-build-steps` | doctor Windows-only build step detection from a committed binlog |
| `versions` | deps audit and gac against a recorded feed; later consolidate, pins, families, redirects |
| `tests-in-prod` | move tests (detection, helpers, `--create`, `InternalsVisibleTo`, pruning, rollback), graph |
| `move-cases` | move plan (one case per rule: clean, package, co-move, cycle, framework-only, partial, resources, internals, Windows-only API, removed path, source still depends), move apply, forwarders; see its README |
| `loose-dlls` | deps resolve-dlls (a project's output, a package's DLL, two vendor DLLs); stub DLLs from `LooseDlls` |
| `cpm-shadowing` | deps consolidate --cpm hazards (OFR1301–1303), non-default central file with opt-in |
| `dead-code` | audit dead-code (one example per confidence level, test-only usage, removable lines); see its README |
| `behavior` | every audit rule, one class per rule with `Positive` and `Negative` members; `ifdef wrap`; see its README |
| `seams` | seams (taint, min cut, articulation point, wire-friendliness), extract interface, remote (net10.0-windows host, net48 fallback, round trip); see its README |

`hollow` is generated, not checked in (`GeneratedFixtures.Hollow` in
`tests/Offramp.Fixtures`): 500 files in `src/Big` to be moved into `src/Big.Core`
by one `move plan --all` and `move apply` run, with `src/App` depending on Big.
