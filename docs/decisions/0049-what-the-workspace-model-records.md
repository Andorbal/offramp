# 0049. Record in the workspace model only what its inputs determine

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/02-workspace-model.md` (schema, staleness); extends ADR 0009

## Context

Two full scans of the same tree wrote different models (SmartStoreNET field test P1 #8,
NHibernate P2), against non-negotiable 1 of `CLAUDE.md`:

- `compilerCalls.<tfm>.index` was the call's position in the compiler log. Basic.CompilerLog
  lists calls in the order the binary log records them, which is the order a parallel build
  finished its compilations: 10 of SmartStoreNET's 25 projects had another index in the second
  scan. The snapshot tests scrubbed the index, so they could not see it.
- `source.sha256` was the hash of the binary log. For `kind: build` that log is Offramp's own
  build output, and MSBuild writes a different one for every build of the same inputs (timings,
  node assignment, environment). The spec already used the hash for staleness only for a
  supplied log (`--binlog`, `--complog`); for a built log, `inputs` decide.

Basic.CompilerLog has no public way to write a compiler log with its calls in another order.

The model's `inputs` (ADR 0009) were the files found by name: project files, solutions,
`Directory.*.props/targets`, and `packages.config`. Open Live Writer's projects import
`writer.build.settings`, and its restore reads `NuGet.config`; editing either did not make the
model stale, and the scratch copies `csproj modernize` verifies in (HEAD plus the model's inputs)
had HEAD's versions, so the verification builds failed where the working tree did not (field test
P1 #9).

A project MSBuild evaluated entered the model whatever its language: Open Live Writer's
`OpenLiveWriter.Ribbon.vcxproj` became `language: other`, `kind: library`, `frameworkClass:
framework` (no target frameworks classify as framework), and `plan` put it in wave 1 as `ready`
while `report` counted it (field test P1 #8). When MSBuild did not evaluate it, the same project was
`OFR0101`, "unsupported project type".

## Decision

A compiler call is named by what identifies it, not by where it is: `compilerCalls.<tfm>` is
`{ complog, project, targetFramework }`, the repository-relative project and the target framework
the log records for the call (`null` for a legacy project's call, which has none). Whoever reads the
compiler log finds the position there: `CompilerLogIngest.CallIndexes` maps the log's regular calls
by project (paths mapped the way `scan` maps them, from the capture root inferred from the calls'
project files) and target framework, the first call winning when a project was compiled twice for
one target, as `scan` already chose. `source.sha256` is the hash of a supplied log and `null` for
`kind: build`; staleness of a built model comes from `inputs` alone, as before.

The inputs are also every file the evaluations imported from inside the repository (outside
`bin/`, `obj/`, `packages/`, dot-directories, and the state directory, as for the others), and
every `NuGet.config`. The comparison hashes the recorded imported files again by path, since their
names say nothing.

The model's projects are C#, Visual Basic, and F# projects only. Any other project, evaluated or not,
is named once by `scan`: `OFR0024` (info) with its kind from the extension, or `OFR0025` (warning) for a
C++ project whose file sets `CLRSupport` (`true`, `Pure`, `Safe`): C++/CLI compiles .NET Framework code,
so it is part of the migration even though Offramp cannot analyze it. A SQL Server Database Project that
MSBuild did not evaluate stays `OFR0101` with its Windows-only step (`OFR0114`), as before; a Web Site
project (a folder, not a project file) is not "another kind" either.

## Alternatives considered

- Sort the compiler log's calls when creating it. Not possible with Basic.CompilerLog's public
  API, and the position would still be meaningless to anyone reading the model.
- Keep the index and resolve it again when it points at another project. It keeps a number that
  differs between identical scans in the model, which is the bug.
- Record the hash of the compiler log instead of the binary log for built models. The compiler
  log embeds the calls in build order, so its bytes differ between builds too.
- Hash the inputs into `source.sha256`. `inputs` already carries the same information file by
  file, and the field would mean two different things depending on `kind`.
- Take the imported files at every comparison from the evaluations. Commands other than `scan`
  have no evaluations, only the model.

- Keep other projects in the model with a flag. Every consumer (plan, report, graph, audits, the
  framework counts) would have to skip them; leaving them out is what each one needs.
- `OFR0101` for every other project. It is a warning that something could not load; a C++ or
  installer project is not a failure, and C++/CLI deserves its own, stronger message.

## Consequences

Two scans of the same tree write byte-identical models, apart from `createdAt`, and the snapshot
tests no longer scrub compiler calls. The model's JSON contract changes: `index` is gone and
`source.sha256` can be `null`. A model an older Offramp wrote is stale ("an older Offramp wrote
it", `OFR0002`), since its numbered calls cannot be found any more; `scan --if-stale` rescans it.
Loading a compilation now reads the compiler log's call list once per log and process, which
costs milliseconds. The inputs extend ADR 0009's list; the scratch copies verification builds in
take the imported files and `NuGet.config` from the working tree, and a model scanned before this
change becomes stale once, as its inputs gain the new files. `plan`, `report`, and `graph` no
longer show C++ or installer projects, and the model's `language` is never `other` from a scan.
