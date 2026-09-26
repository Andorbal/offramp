# 0009. Detect a stale model by content hashes

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/02-workspace-model.md#staleness`

## Context

The spec compares `source.sha256` and the modification times of the files that
shape the model. Modification times change on every clone, checkout, and
`git stash`, so a fresh clone of the commit the model was built from would be
stale, and a file edited and reverted would not. The spec also does not say
where the recorded values live, nor whether solution filters count (`slice`
writes `.slnf` files, which would make every model stale after a slice).

## Decision

The model records `inputs`: every project file (`.csproj`, `.vbproj`,
`.fsproj`, `.sqlproj`), solution (`.sln`, `.slnx`), `Directory.*.props`,
`Directory.*.targets`, and `packages.config` in the repository, outside
`bin/`, `obj/`, `node_modules/`, `packages/`, `TestResults/`, `artifacts/`,
dot-directories, and the state directory, each with its SHA-256. A solution
filter is an input only when it is the model's own solution. A command compares
the current set against the recorded one: any changed, added, or removed file,
or a changed `--binlog`/`--complog` source, makes the model stale (`OFR0002`,
naming what changed). `--fail-on-stale` raises `OFR0002` to an error.

## Alternatives considered

- Modification times: cheap but wrong across clones and checkouts.
- Hashing source files too: more precise for compile lists, but a model stays
  useful while code changes; only project structure invalidates it, and hashing
  every `.cs` file of a large repository on every command costs seconds.

## Consequences

- Each command hashes the project files of the repository, which is fast
  (thousands of small files).
- Edits to `.cs` files never make the model stale; commands that depend on
  source (moves, audits) read sources from disk or rebuild compilations.
