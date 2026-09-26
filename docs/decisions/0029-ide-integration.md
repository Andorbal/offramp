# 0029. Serve the IDEs from one Offramp language server over the recorded compilations, suggest only moves that are nothing but a move, and let new code be what differs from the merge base

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/ide.md`, `docs/spec/03-configuration.md`

## Context

The roadmap's "Later" section had "Visual Studio / Rider integration via the analyzer package".
The goal is now wider: VS Code, Visual Studio, and Rider extensions that help developers who
are not on the migration keep new code in .NET Framework-only projects migration-friendly. They
flag APIs modern .NET lacks, suggest a portable project for new classes, and show a lens on
existing classes that could move. They must be off unless the repository has `.offramp/`,
switchable either way, and able to use a map from a project to its portable counterpart. The
request left open:
- where the analysis runs, given three IDEs with different extension models
- how an extension gets a semantic model without the user's IDE sharing its own
- what "new code" and "easily movable" mean, precisely enough to test
- what the default counterpart is without a map, and where the map lives
- how a move from the editor squares with the purity contract and the model's staleness

## Decision

- **One engine, one language server, thin shells.** `Offramp.Ide` computes a file report.
  `offramp ide check` prints file reports and `offramp ide serve` renders them over the Language
  Server Protocol, so the terminal, CI, and every IDE agree, and there is one test suite. VS Code
  uses `vscode-languageclient`. Visual Studio's LSP client and Rider's LSP API cover diagnostics,
  code actions, and commands. Where a client does not render lenses (Visual Studio), the shell
  asks the server for `offramp/fileReport` and draws them with the IDE's own API.
- **The recorded compilations, with the current text laid over them.** The engine rebuilds
  compilations from the compiler log, as every other analysis does. Editor buffers and changed
  files on disk replace their recorded trees, deleted files drop out, and new `.cs` files inside
  an SDK-style project's folder join it (minus its `Compile Remove` patterns). Source edits never
  need a scan; project-file edits make the model stale as before. So a suggestion is exactly what
  `move plan` would decide, and `MovePlanner` takes an `ICompilationSource` so the editor's move
  plans against the same text.
- **Easily movable is the planner's `--co-move none` minus anything that is more than a move.**
  `MovePlanner.Assess` checks one file against one counterpart in rule order: no resource pair or
  partial part elsewhere, nothing else from the source, only projects and packages the
  counterpart already has, a free destination path not removed by the counterpart, a trial
  compilation with no errors, and the namespace rule when `block`. It reuses the planner's own
  helpers and trial compilation. The rest of the source and its dependents are not checked per
  keystroke: the move itself runs the full planner, and a disagreement surfaces there, before
  anything is written. The lens sits on files, anchored at the type named like the file, because
  files are what moves.
- **New code** is the lines that differ from the file at the merge base of `HEAD` and a ref
  (default `auto`: `origin/HEAD`, else `HEAD`), following renames. This is so commits on a feature
  branch keep their suggestions until merged. Line endings are ignored, and an unchanged prefix and
  suffix are trimmed before a Myers diff. Outside git, or with a ref that does not resolve, OFR6007
  says what happened: with no repository everything is new, and a bad ref falls back to `HEAD`.
- **Counterparts** come from `projectMap:` (a top-level key, because `move` and `plan` can use
  it later), with the editor's entries first. Names or paths are accepted, `*`/`?` in `from`, and
  `{name}` in `to` for conventions. Without an entry, the portable projects the project already
  references are its counterparts ("accessible from where the developer is"). Test projects get
  none implicitly. A counterpart must be `standard` or `dual`, referenceable from every target of
  the project, not depend on it, and not be frozen or excluded (OFR6004).
- **Enablement** lives in the IDE settings only (`auto`/`on`/`off`, per user or per workspace).
  `auto` looks for `.offramp/` literally. A repository that moved its state elsewhere sets `on`,
  because the shells decide before any server or config parser runs.
- **Moves from the editor** are `move plan --files FILE --co-move none` and `move apply`: the
  file must be saved (OFR6008), the user confirms a message that lists every project-file edit,
  verification follows `move.verify`, and the journal allows `move rollback`. An untracked file
  (a class written a minute ago) has no history for `git mv`, so `IGitService.MoveAsync` moves it
  as a plain file. That is the only way to move it, and its bytes still do not change. After a
  move, the engine lays the plan over its in-memory model (compile items, references) and
  remembers the hashes of the project files it wrote. Those files do not make the model stale for
  the next move from the editor; the command line still sees the model as stale until `scan`.
- **Severities one step down in the editor.** Error → Warning, warning and info → Information.
  Nothing the editor reports breaks the build, and a red squiggle on code that compiles would
  teach people to ignore it. `ide check` keeps Offramp's severities, so `--fail-on` gates CI.
- **A hand-written LSP layer** (framing, dispatch, a few message shapes as `JsonNode`) instead of
  `OmniSharp.Extensions.LanguageServer` or `StreamJsonRpc`. The server needs about fifteen
  methods, and this adds no dependency or reflection-based serialization.

## Alternatives considered

- **Roslyn analyzers in each IDE** (a VSIX analyzer in Visual Studio, the C# extension's
  undocumented `csharpExtensionLoadPaths` in VS Code, the NuGet package in Rider). The IDE's own
  semantic model costs nothing, but an analyzer sees only its own compilation: it cannot
  trial-compile in a counterpart, run git, or read `.offramp/`. Rider would still need a
  project reference to the package. It remains the in-build channel (`Offramp.Analyzers`).
- **MSBuildWorkspace in the server.** It needs no scan and builds out of process since Roslyn
  4.9. But it is a second loading path with different answers from `move plan`. Legacy projects
  on Linux and macOS load only on a best-effort basis without Mono. It would also load the
  solution a second time. The first scan is the cost of the chosen path; a project-scoped scan
  is the likely remedy (`ide.md#later`).
- **Suggest every counterpart the planner could reach** (with co-moves or new references). The
  request was to show lenses only where the move is easy; anything more belongs in `move plan`,
  where the user sees the whole plan.
- **`HEAD` as the default base.** A commit would then make the suggestions for the code it
  holds disappear before anyone reviewed them.
- **`projectMap` under `ide:`.** The map is a fact about the repository, not about editors.

## Consequences

- The first time in a repository, the extension asks to run `offramp scan`, which builds the
  solution. After that, only project-file changes need a scan.
- Adding a VS Code, Visual Studio, or Rider feature means adding it to the file report and to
  `LspRender`. Shells stay small, and `ide check` snapshots cover the behavior.
- The Visual Studio and Rider shells each start with a spike: a second language server on C#
  documents next to Roslyn or ReSharper is supported on paper but untested here (M16, M17).
- `effectiveConfig` in every envelope now includes `ide` and `projectMap`.
