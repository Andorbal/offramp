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
| `dual-target` | scan (including a Windows-captured compiler log), graph, move plan, ifdef |
| `cycle` | graph cycles, move plan refusal |
| `windows-only-build-steps` | doctor Windows-only build step detection from a committed binlog |
