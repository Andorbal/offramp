# `ide`: editor integration (VS Code, Visual Studio, Rider)

The rest of Offramp helps a migration team move existing code off .NET
Framework. The editor integration serves everyone else: developers who are not
working on the migration but write code every day in projects that are still
.NET Framework only. Every class they add to such a project is one more class
the migration team will have to move later. The integration points that out
while the code is being written, says where the code could live instead, and
moves it there on request.

It does this with the same engine and the same rules as the command line. A
suggestion in the editor is exactly what `offramp ide check` reports for the
file, and a move started from the editor is a `move plan` of that file followed
by `move apply`: pure, staged with `git mv`, journaled, and verified. Decisions
are recorded in `docs/decisions/0029-ide-integration.md`.

```
offramp ide check [--file PATH ...] [--base REF] [--scope lines|files|all]
offramp ide serve
```

## Shape

```
 VS Code extension ─┐                        ┌─► Offramp.Ide (engine) ──► Refactoring (move planner, applier)
 Visual Studio ext. ├─ LSP over stdio ─► offramp ide serve                 ──► Analysis (audit api rules)
 Rider plugin ──────┘                        └─ scan, same command tree    ──► Workspace (model, compiler log)
                                    offramp ide check ──► Offramp.Ide (the same file reports, as JSON)
```

- **The engine** (`src/Offramp.Ide`) answers one question per file: what should an
  editor show here? The answer is a file report (below): the new lines, the
  findings on them, the types the file declares, and where the file could move.
  It reads the workspace model and the compiler log like every other command,
  with the text currently in the editor laid over the recorded sources.
- **`offramp ide check`** prints file reports as a JSON contract. It is how the
  engine is tested, and it works as a gate in CI too.
- **`offramp ide serve`** is a Language Server Protocol server over stdio. Every
  diagnostic, lens, and code action it sends is a rendering of a file report.
- **The shells** are thin per-IDE extensions: they decide whether to start the
  server, start it, and add what their IDE's LSP client lacks. No shell analyzes
  code.

The server runs next to the IDE's own C# language service and never replaces it:
it adds diagnostics, lenses, and actions to C# documents, and IDEs merge those
with Roslyn's.

## When it is on

Each shell resolves one setting, `offramp.enabled`, for each repository it has
open:

| Setting | Result |
|---|---|
| `auto` (default) | on when the repository root has an `.offramp/` directory, off otherwise |
| `on` | on, whether or not `.offramp/` exists |
| `off` | off |

The repository root is the top of the git work tree that contains the IDE's
workspace folder, or the folder itself outside git. Users set the value per user
(everywhere) or per workspace (one repository, which a team can commit), so it
can be turned on in repositories without `.offramp/` and off in repositories
that have one. When the result is off, the shell does not start the server and
shows nothing. `auto` looks for `.offramp/` literally; a repository that moved
its state elsewhere (`paths.state`) sets `on` instead.

The server resolves the same rule from the `enabled` initialization option (so
`ide check` and every shell agree) and, when it is off, answers requests with
nothing.

## What the editor shows

Only in C# files of **.NET Framework-only projects** (`frameworkClass:
framework`) that are in the workspace model and not excluded by `paths.exclude`.
Files of other projects already build for modern .NET; the compiler speaks for
them.

### New code

"New" means the lines that differ from the new-code base:

- `ide.newCode.base` names a git ref (default `auto`). The base is the merge base
  of `HEAD` and that ref, so everything on a feature branch counts as new until
  it is merged, and a commit on the branch does not make a suggestion go away.
  `auto` is `origin/HEAD` (the remote's default branch) when it exists, else
  `HEAD` (uncommitted changes only).
- A file is compared with its text at the base (following a rename in `git diff
  -M`), line by line, ignoring line-ending differences. Lines that were added
  or changed are new; a file that did not exist at the base is new throughout.
- `ide.newCode.scope` widens it: `lines` (default), `files` (every line of a
  changed file), or `all` (everything, for people working on the migration who
  want the whole picture in the editor).
- Outside a git repository, or when the base does not resolve, `OFR6007` says so:
  without a repository everything counts as new; with a ref that does not
  resolve, the base falls back to `HEAD`.

### Findings on new code

The `audit api` rules (`commands/audit.md`), run over the file as it is in the
editor, reported only on new lines: APIs missing on the target (`OFR3001`),
Windows-only APIs (`OFR3002`), APIs that throw on modern .NET (`OFR3003`), and
technologies with no port (`OFR3004`–`OFR3009`, ...), each with its
recommendation and, for a missing API, the assembly's mapping (the package or
replacement). The codes, rule packs, `rules.packs.disable`, and per-code
`rules:` overrides are `audit api`'s; nothing is new here except the filter. The
point is to catch `HttpContext.Current` or `BinaryFormatter` while someone is
typing it, not in the migration team's next audit.

### Code that could live in the counterpart

A **counterpart** is a project that is .NET 8/10 friendly (`standard` or `dual`),
that the .NET Framework project can reference, and that does not depend on it:
the place where code the project needs, but that needs nothing from .NET
Framework, can live. See [Counterparts](#counterparts-and-the-project-map).

For each file, the engine checks whether the file can move to each counterpart
**as it is**: no co-moves, no new references for the counterpart, no edit to the
file ("easily movable", rules below). The answer attaches to the types the file
declares:

- **New types** (the type's name is on a new line) in a file that can move get
  `OFR6001` (info): "`PriceCalculator` needs nothing from .NET Framework; it can
  live in `ModernF`, which `Foo` references, and will not need migrating." Its
  quick fix moves the file.
- **Every** file that can move, new or not, gets a lens on its first type
  declaration (the type named like the file, else the first one): `Move to
  ModernF`, one per counterpart. The same move is offered as a refactoring code
  action on the type's name, for editors or users without lenses. The lens is
  only shown when the file is easily movable, so it never offers a move that
  needs more than a move.
- A file that cannot move gets nothing in the editor; the file report says why
  (a `moves[]` entry with a code, as `move plan` reports exclusions).

### Easily movable

Checked per file and counterpart, in this order; the first rule that fails is
the reason in the file report:

1. The source project is not `frozen` (`OFR2003`), and the file is a compile
   item inside the source project's folder that declares at least one type and no
   top-level statements (otherwise it is not considered at all).
2. It has no `.resx`/`.Designer.cs` pair and no part of a partial type in another
   file (`OFR2110`).
3. Everything it uses from the source project is declared in the file itself
   (`OFR2101`, naming the other file).
4. Every other project it uses is the counterpart or a project the counterpart
   references directly (`OFR2001` when the reference would close a cycle, else
   `OFR6003`: the counterpart would need a new project reference).
5. Every package it uses is one the counterpart already has (`OFR2102` when the
   package has no assets for a counterpart target, else `OFR6003`: the
   counterpart would need the package).
6. The destination path (the file's path under the source folder, under the
   counterpart folder, as `move plan` maps it) is free (`OFR6003`) and not removed
   by the counterpart's `Compile Remove` patterns (`OFR2111`).
7. It compiles in the counterpart, as it is, for every counterpart target
   (`OFR2103`, with the first three errors). This is `move plan`'s trial
   compilation for this one file.
8. With `move.namespaceMismatch: block`, its namespaces are under the
   counterpart's root namespace (`OFR2120`).

These are the planner's rules for `--co-move none`, minus what makes a move more
than a move. Whether the rest of the source project and its dependents still
compile is not checked here (it needs the whole project); the move itself
checks it before anything is written.

## Counterparts and the project map

For each .NET Framework project, its counterparts are, in order:

1. The **project map**: entries from the IDE setting `offramp.projectMap`, then
   entries from `projectMap:` in `offramp.yml`. Every entry whose `from` matches
   the project adds its `to`. `from` and `to` are project paths
   (repository-relative) or project names. In `from`, `*` and `?` match any
   characters of a name or path. In `to`, `{name}` is the matched project's
   name. So one entry can cover a repository with a naming convention.

   ```yaml
   projectMap:
     - from: Foo                     # the project named Foo
       to: ModernF                   # has its portable code in ModernF
     - from: src/Billing/Billing.csproj
       to: src/Billing.Core/Billing.Core.csproj
     - from: "Contoso.*"             # every Contoso.X ...
       to: "{name}.Portable"         # ... keeps it in Contoso.X.Portable, where that exists
   ```

2. **Without a matching entry**, the portable projects the project already
   references directly, sorted by path (`ide.implicitCounterparts`, default
   `true`). They are accessible from where the developer is, by definition. Test
   projects get none implicitly (a test class does not belong in a production
   library), and a test project is never an implicit counterpart.

A counterpart must be C#, not `frozen`, not the project itself, `standard` or
`dual`, referenceable from every target of the project (by `NuGet.Frameworks`,
as `move plan` decides), and must not depend on the project. An entry that names
no project, or a name several projects share, is `OFR6002`; a counterpart that
breaks a rule is `OFR6004` and is dropped. A .NET Framework project with no
counterpart gets `OFR6005` (info) once in `ide check` and a hint in the shell's
status: add a `projectMap` entry. A `{name}` entry whose project does not exist
is not an error (that is how conventions work); an exact entry is.

The counterpart does not have to be referenced yet. When the source project does
not reference it and code left behind uses the moved file, the move adds the
reference (and says so before it happens), which is how "accessible from where
the developer is" stays true for the code left behind.

## Moves from the editor

A lens, a quick fix, and a refactoring action all run the server command
`offramp.move` with `{ "file": "src/Foo/Pricing/PriceCalculator.cs", "to": "src/ModernF/ModernF.csproj" }`:

1. The file must be saved (`OFR6008` otherwise), and the workspace model must
   exist (`OFR0001`) and be fresh (`OFR0002`, except for project-file edits the
   server's own moves made, below).
2. `move plan --from SRC --to TO --files FILE --co-move none`, with the editor's
   text laid over the recorded sources. When the plan moves anything other than
   exactly the file, or excludes it, nothing happens and the reason is shown.
3. The user confirms: "Move `PriceCalculator.cs` to `ModernF`? It is renamed
   with `git mv` to `src/ModernF/Pricing/PriceCalculator.cs` and stays in
   namespace `Foo.Pricing`; `Foo` gets a project reference to `ModernF`." No
   confirmation, no change.
4. `move apply` of the plan (saved under `.offramp/plans/`), verified with
   `move.verify`, rolled back on failure per `verify.onFailure`, with progress.
   The moved file opens at its new path; the journal is named so
   `offramp move rollback --journal PATH` can undo it.

Everything the move contract promises holds: the file's bytes do not change
(its namespace stays), the rename is staged, project-file edits are left
unstaged, and nothing is committed. After a move, the server lays the plan's
effects (compile items, references) over its copy of the model and remembers the
project files it wrote, so the next move does not need a new scan; `offramp`
commands on the command line still see the model as stale until the next
`scan`.

## Configuration

`offramp.yml` (`03-configuration.md`), shared by the team:

```yaml
projectMap: []                 # see above; also the default counterparts for the editor
ide:
  newCode:
    base: auto                 # auto (origin/HEAD, else HEAD) or a git ref
    scope: lines               # lines | files | all
  implicitCounterparts: true   # without a projectMap entry, use portable projects already referenced
```

IDE settings, per user or per workspace, in each shell's own settings UI:

| Setting | Default | Meaning |
|---|---|---|
| `offramp.enabled` | `auto` | `auto`, `on`, `off` ([When it is on](#when-it-is-on)) |
| `offramp.projectMap` | `[]` | entries consulted before `offramp.yml`'s |
| `offramp.newCode.base` | unset | overrides `ide.newCode.base` |
| `offramp.newCode.scope` | unset | overrides `ide.newCode.scope` |
| `offramp.implicitCounterparts` | unset | overrides `ide.implicitCounterparts` |
| `offramp.codeLens` | `true` | show move lenses |
| `offramp.server.path` | unset | the `offramp` executable; else the repository's local tool, else `offramp` on `PATH` |

The shell sends them as the server's `initializationOptions` and again on
`workspace/didChangeConfiguration`; a set value wins over `offramp.yml`, as a
command-line flag does.

## `offramp ide check`

```
offramp ide check [--file PATH ...] [--base REF] [--scope lines|files|all]
```

- `--file` (repeatable, relative to the current directory): the files to report
  on. Without it, every C# file that is new or changed since the base (`git diff
  --name-only`, plus untracked files), in any project of the model.
- `--base` and `--scope` override `ide.newCode.base` and `ide.newCode.scope`.
- Always runs; `enablement` says what an editor would do with `auto`.
- Diagnostics: every finding on new code with its own code and severity, one
  `OFR6001` per new movable type, and `OFR6002`, `OFR6004`–`OFR6007`. So
  `offramp ide check --base origin/main --fail-on error` fails a pull request that
  adds code using APIs modern .NET does not have, and `--fail-on info` one that
  adds a class to a .NET Framework project that could live elsewhere.

Result (`schemas/v1/ide-check.json`):

```jsonc
{
  "base": { "ref": "origin/HEAD", "commit": "3f2a…", "scope": "lines" },  // commit null outside git
  "enablement": { "mode": "auto", "enabled": true, "reason": "state-directory" },
  "counterparts": [   // every .NET Framework project of the model
    { "project": "src/Foo/Foo.csproj", "counterparts": ["src/ModernF/ModernF.csproj"], "source": "map" }  // map | referenced | none
  ],
  "files": [
    {
      "file": "src/Foo/Pricing/PriceCalculator.cs",
      "project": "src/Foo/Foo.csproj",            // null when no project of the model compiles it (OFR6006)
      "applies": true,                            // a C# file of a .NET Framework-only project
      "newLines": [[1, 42]],                      // 1-based, inclusive, sorted
      "findings": [
        { "code": "OFR3001", "severity": "error", "line": 12, "column": 17, "endLine": 12, "endColumn": 36,
          "symbol": "System.Web.HttpContext.Current", "message": "…", "recommendation": "…", "details": { "assembly": "System.Web", "mapping": "none" } }
      ],
      "types": [
        { "name": "Foo.Pricing.PriceCalculator", "kind": "class", "line": 5, "column": 18, "endLine": 5, "endColumn": 33, "new": true }
      ],
      "moves": [   // one per counterpart; code, message, and details say why a file cannot move, as move plan's exclusions do
        { "to": "src/ModernF/ModernF.csproj", "movable": true, "destination": "src/ModernF/Pricing/PriceCalculator.cs",
          "referenced": true, "code": null, "message": null, "details": [] }
      ]
    }
  ],
  "summary": { "files": 1, "newLines": 42, "findings": 1, "newMovableTypes": 1, "movableFiles": 1 }
}
```

Files, findings (by line, column, code), types (by position), and moves (by the
counterpart order) are sorted. Positions are 1-based; the server converts them to
LSP's 0-based positions.

## `offramp ide serve`

A Language Server Protocol 3.17 server on stdin/stdout for one repository (the
client's root folder). Logging goes to stderr only. Everything it sends is built
from the file reports above.

- **Initialization options:** `enabled`, `projectMap`, `newCode` (`base`,
  `scope`), `implicitCounterparts`, `codeLens`, and `clientCommands` (true when the
  shell registers `offramp.*` commands itself; otherwise the server lists them in
  `executeCommandProvider`).
- **Documents:** full text sync of C# files (`didOpen`, `didChange`, `didSave`,
  `didClose`); `workspace/didChangeWatchedFiles` for files the editor does not have
  open, and for project files (freshness).
- **Diagnostics:** pushed with `textDocument/publishDiagnostics` for open
  documents, after a pause in typing. Offramp's severities map to LSP's one step
  down, because nothing here breaks the build: error → Warning, warning →
  Information, info → Information. Each has `code`, `codeDescription` (the
  code's entry in `docs/diagnostics.md`), `source: "offramp"`, and the
  recommendation after the message.
- **`textDocument/codeLens`:** the move lenses (command `offramp.move`), when
  `codeLens` is on.
- **`textDocument/codeAction`:** `quickfix` for `OFR6001`, `refactor.move` on a
  movable type's declaration; both run `offramp.move`.
- **`workspace/executeCommand`:** `offramp.move` ([Moves](#moves-from-the-editor);
  asks with `window/showMessageRequest`, reports with `window/showMessage`, shows
  progress with `window/workDoneProgress/create` and `$/progress`, opens the moved
  file with `window/showDocument`; returns `{ plan, apply }`), `offramp.scan`
  (runs `offramp scan` through the same command tree, then reloads), and
  `offramp.refresh`.
- **`offramp/status` (notification to the client):** `{ enabled, reason, model:
  missing|stale|fresh, counterparts, message }` after start, after a scan or
  move, and when freshness changes; shells show it in a status bar. When the model
  is missing the server also asks once whether to run `offramp scan`.
- **`offramp/fileReport` (request from the client):** `{ uri }` → the file report,
  for shells that draw with their IDE's own APIs (Visual Studio's lenses).

## The shells

### VS Code (M15)

`editors/vscode`, a TypeScript extension on `vscode-languageclient`.
- Activates in folders with `.csproj` files or an `.offramp/` directory; resolves
  `offramp.enabled` per workspace folder and starts one client per enabled
  repository.
- Finds the server: `offramp.server.path`; else the repository's local tool
  (`.config/dotnet-tools.json` lists `offramp`: `dotnet tool run offramp`); else
  `offramp` on `PATH`; else offers `dotnet tool install -g offramp`.
- Registers the `offramp.move`, `offramp.scan`, and `offramp.refresh` commands and
  forwards them to the right folder's server (so multi-root workspaces work), plus
  `Offramp: Enable in this workspace`, `Disable in this workspace`, `Restart`, and
  `Show output`.
- Status bar: on/off and why, the model's state, and a click to scan.
- Published to the Marketplace and Open VSX; the `.vsix` is built in CI.

### Visual Studio (M16)

A VisualStudio.Extensibility (out-of-process) extension, built on Windows.
- `LanguageServerProvider` starts `offramp ide serve` for C# documents: diagnostics,
  code actions, and commands come from the server. Visual Studio's LSP client
  does not render `textDocument/codeLens`, so the extension adds an
  `ICodeLensProvider` for types that asks the server for `offramp/fileReport`.
- The spike that starts M16 confirms that a second language server on C#
  documents behaves next to Roslyn (Microsoft documents the LSP client as not
  meant to extend existing languages); if it does not, the fallback is the same
  extension calling `offramp ide check` for the file and drawing everything with
  the Extensibility editor APIs.
- Enablement uses the same three values, stored per solution and per user.

### Rider (M17)

An IntelliJ platform plugin.
- The spike that starts M17 checks the IntelliJ LSP API (`LspServerSupportProvider`,
  code lens from 2026.1) on `.cs` files, which Rider serves from its ReSharper
  backend. If it works, the plugin is that client plus enablement and settings.
- If it does not, a ReSharper backend plugin (`JetBrains/resharper-rider-plugin`)
  talks to `offramp ide serve` and renders highlightings, quick fixes, and Code
  Vision, which the template shows working on C# members.

## Later

Suggestions that build on the same engine, in rough order of value:

- **Portability regressions:** a change that makes a movable type unmovable (the
  first `System.Web` call in a class that could have moved) is the moment debt is
  created; report it against the base's answer for the same file.
- **Pull-request gate:** `ide check --format sarif` so code scanning annotates the
  diff, and a GitHub Action wrapping it.
- **Codemod quick fixes:** where a finding on new code has a codemod
  (`commands/codemod.md`), offer it as the quick fix.
- **Members, not only types:** move a static method or a nested helper to the
  counterpart through `extract`-style edits, when the purity contract allows it.
- **The rest of the CLI:** the guide's next step, the project's wave in `plan`,
  and the graph, as editor views; `scan --if-stale` on idle.
- **Project-scoped scan:** building only the projects an editor needs, so the
  first scan in a very large repository is cheap.

## Diagnostics

`OFR6001` new type could live in its counterpart (info), `OFR6002` project map
entry does not resolve (warning), `OFR6003` more than a move (info, in
`moves[]` only), `OFR6004` counterpart cannot take the project's code (warning),
`OFR6005` no counterpart for a .NET Framework project (info), `OFR6006` file not
in the workspace model (warning), `OFR6007` new-code base unavailable (warning),
`OFR6008` file has unsaved changes (error, from `offramp.move`). Also, from the
commands the engine reuses: the `audit api` codes, `OFR2001`, `OFR2003`,
`OFR2101`–`OFR2103`, `OFR2110`, `OFR2111`, `OFR2120`, `OFR0001`, and `OFR0002`.
