# 0001. Load workspaces from MSBuild binary logs only

- Status: accepted
- Date: 2026-09-25
- Spec section: `docs/spec/00-architecture.md`, `docs/spec/02-workspace-model.md`

## Context

Roslyn offers `MSBuildWorkspace`, which evaluates projects in-process via
MSBuild. In practice it couples the tool's runtime to the user's SDK version
(MSBuild assemblies from a .NET N SDK only load into a .NET ≥ N process),
struggles with legacy csproj on non-Windows, and evaluates thousands of
projects slowly. Offramp must analyze millions of lines, on macOS, against
projects that sometimes need Windows-only build steps.

## Decision

`scan` obtains everything from an MSBuild binary log: evaluated properties and
items through the structured-log reader, and exact compiler invocations
through a compiler log (`Basic.CompilerLog`), from which Roslyn
`Compilation`s are rebuilt on demand. The log is produced by running the
user's own `dotnet build -bl`, or supplied by the user (captured anywhere,
including a Windows agent). Project files are edited with
`ProjectRootElement` without evaluation.

## Alternatives considered

- `MSBuildWorkspace` + `MSBuildLocator`: rejected for the version coupling and
  the Windows dependency of legacy projects.
- Buildalyzer: closer (it also runs builds out of process and reads binlogs),
  but adds a layer over the same idea without the portable compiler-log
  snapshot.
- Parsing csproj XML directly: fast but wrong for anything conditional or
  SDK-implicit (globs, implicit references), which is most of a real repo.

## Consequences

- The first scan costs a build; subsequent scans can reuse logs. `--fast`
  (record compiler args without compiling) is to be evaluated in M1.
- Analysis on a Mac of a Windows-only project is possible from a Windows-made
  compiler log.
- Anything Offramp needs from evaluation must be present in the binlog; when a
  property is missing, the fix is to read it from the log, never to evaluate.
