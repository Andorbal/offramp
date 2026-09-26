# 0018. Move tests: helper evidence, trial compilation, and journals that refuse to clobber

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/move.md#move-tests`, `#move-apply`

## Context

The spec defines a helper as "a file that is not a test file and every
reference to every symbol it declares comes from test or helper files". Read
literally, that makes a library's public API a helper whenever the library's
own tests are its only users in the solution: `OrderService` in the
`tests-in-prod` fixture would move into `Foo.Tests`. The spec also leaves open:
- how trial compilation handles a destination that does not exist yet
- which references a move may add
- which target framework it compiles for
- what the journal contains and what rollback does when files changed since
- how severe an excluded file is
- how a file linked from outside the project is handled

## Decision

- **Helpers need evidence.** A helper candidate must also show test-support
  evidence: it uses a test framework, assertion, or mocking library; a
  declared type's name contains `Builder`, `Fake`, `Stub`, `Mock`, `Fixture`,
  `TestData`, or `Harness`; or it lives under a folder such as `Tests`,
  `Testing`, or `TestData`. The fixpoint then keeps candidates whose users are
  all tests or helpers (or the destination), limited to those a test reaches.
  Code used only by tests but without evidence is the code under test and
  stays. This moves less, never more.
- **Users are found semantically.** Every simple name in the source project
  and in all its dependents' compilations is bound with the semantic model.
  Metadata symbols from dependents map back to source symbols by
  documentation ID. A dependent other than the destination that uses a
  candidate keeps it (`OFR2201`). Implicit uses that no name reveals (implicit
  conversion operators, query-expression extension methods) are caught by the
  source check (`OFR2104`).
- **Candidates are for review.** Unused files with evidence are `medium` and
  listed. Unused files without it are `low`, not listed (they are unused
  code), and move only with `--include-helpers low`.
- **Trial compilation compiles one target framework**, the source's preferred
  one (the first `net4x`). The destination compilation is built as follows:
  - For an existing destination: its recorded compilation, with the trimmed
    source in place of its old reference to the source.
  - For a new project: the source's .NET Framework references, because the
    template copies them.
  
  In both cases the needed package and project references are added. A needed
  .NET Framework reference is never added to an existing destination, so a
  file needing one stays (`OFR2103`). Checking every target framework is left
  to `move plan` in M5, where multi-targeted destinations matter.
- **`OFR2103` is a warning.** An excluded file is a finding about that file;
  the rest of the move proceeds and the command succeeds. `OFR2104`, which
  stops the whole move, stays an error.
- **Linked files** (outside the source's folder) have no destination path
  (`OFR2206`), and **other languages** are refused (`OFR2205`). Both are new
  codes in the `move tests` range.
- **The journal records each step's resulting SHA-256.** Rollback checks every
  completed step first. If any file changed since the move, nothing is undone
  (`OFR2151`), so a rollback never discards later work. The journal name is
  `<yyyyMMdd-HHmmss>-<command>.json`, from the host's clock.
- **Solutions** are edited with `Microsoft.VisualStudio.SolutionPersistence`.
  A new project goes into its siblings' solution folder. For a solution filter
  model, it is added to both the filter and the solution it filters.

## Alternatives considered

- The spec's literal helper rule: it moves library APIs into test projects.
- Treating every public type as production: most builders and fakes are
  public, so almost nothing would move.
- Adding `<Reference>` items for .NET Framework assemblies to an existing
  destination: that edits the destination beyond what the files need, and the
  spec says the framework case is "decided by trial compilation".

## Consequences

- In a project whose helpers have no marking name, folder, or library, only
  the tests move. The helpers stay in the source, and the moved tests reach
  them through the destination's reference to it (with `InternalsVisibleTo`
  when they are internal).
- M5's `move plan`/`move apply` reuse the change set, journal, trial
  compilation, and project editing built here.
