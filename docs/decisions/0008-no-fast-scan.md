# 0008. Drop `scan --fast`

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/02-workspace-model.md#inputs`, `docs/spec/commands/workspace.md#scan`

## Context

The spec asked M1 to investigate `scan --fast`: build with
`-p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true` to record
compiler arguments without compiling, and to keep the flag only if compiler-log
rehydration works from such a log.

## Decision

Drop the flag. On the `dual-target` fixture (three projects, one project
reference chain), from a clean tree:

- The full build with a binary log took 3.5 s; the "fast" one 2.6 s. Restore
  and evaluation dominate, so the saving is small and shrinks with repository
  size relative to evaluation cost.
- The fast build fails: `MSB3030: Could not copy the file
  "obj/Debug/netstandard2.0/Contracts.dll" because it was not found.` No
  assembly is produced, so every project that references another project has
  no reference to compile against.
- Converting that log to a compiler log keeps only the leaf project's
  compiler call. `Shared` and `Tool` have none (`OFR0132: Missing file ...
  Contracts.dll`), so every semantic command would skip them.

## Alternatives considered

- Keep `--fast` for evaluation-only uses (graph, kinds): those already come
  from the binary log of an ordinary build, and a second build mode would
  double the paths to test for a one-second gain.
- Build leaf-first with reference assemblies only: that is an ordinary build.

## Consequences

- `scan` always compiles. Repositories that cannot compile on the scanning
  machine use a log captured elsewhere (`--binlog`, `--complog`,
  `docs/compiling-on-macos.md`).
- `--no-build` reuses the previous scan's log when only the model rules changed.
