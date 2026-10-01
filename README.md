# Offramp

**The off-ramp from .NET Framework.** A single CLI with many commands that
makes migrating large .NET Framework codebases to modern .NET mechanical,
deterministic, and observable. Built for multi-million-line monorepos where
wholesale "upgrade my app" tools give up.

> Status: pre-1.0, under active development. See [CHANGELOG.md](CHANGELOG.md)
> and [docs/ROADMAP.md](docs/ROADMAP.md).

## What it does

Offramp does not try to migrate an application in one shot. It gives you
sharp tools for each step of an incremental migration, and every tool emits
machine-readable output so the next tool, a CI job, or an AI agent can use it.

| Area | Commands | What you get |
|---|---|---|
| Get started | `guide` | A step-by-step walk through the migration that explains each step, runs it, and remembers where you are |
| Understand | `scan`, `doctor`, `graph`, `report`, `plan` | A workspace model, a dependency graph you can show stakeholders, and a leaf-first migration order |
| Dependencies | `deps audit`, `deps consolidate`, `deps resolve-dlls`, `deps gac`, `redirects sync` | Which packages support your target, one version per package, no more binding-redirect archaeology |
| Move code | `move tests`, `move plan`/`move apply`, `move extract`, `forwarders` | Pure, git-friendly moves of files and test code between projects, verified by compilation |
| Find problems | `audit api`, `audit behavior`, `audit serialization`, `audit native`, `audit dead-code`, `audit api-compat`, `ifdef` | Exactly what won't port, what will silently change behavior, and what nobody uses |
| Isolate the unportable | `seams`, `extract interface`, `remote` | The smallest interface that fences off unportable code, and a generated HTTP boundary for it |
| Scaffold | `service`, `web`, `csproj modernize`, `config convert`, `codemod` | Hosted console apps for Linux containers, strangler-fig web setups, and bulk Roslyn rewrites |
| Automate | `verify`, `slice`, `mcp serve` | Configurable build verification, fast sub-solution builds, and an MCP server for agents |
| Stop adding debt | `ide check`, `ide serve`, the VS Code extension | New code in .NET Framework projects checked as it is written: APIs modern .NET lacks, and classes that could live in a portable project, moved there in one click |

Every command takes `--target N` (8, 9, 10, ...) or a target framework (`net8.0`,
`netstandard2.0` for a library that keeps serving .NET Framework), `--json`, and
`--dry-run` where it makes sense. The default target is .NET 10.

## Install

```bash
dotnet tool install -g offramp
offramp doctor        # checks SDKs, reference packs, git, and your repo
offramp init          # writes offramp.yml with detected defaults
offramp scan          # builds the workspace model in .offramp/
```

## Not sure where to start?

```bash
offramp guide
```

The guide walks through the migration one step at a time: it explains why each
step matters, runs the Offramp command for it, remembers what is done in
`.offramp/guide.json`, and asks whenever more than one step could come next. It
does not try to migrate the application for you, and it never changes your code
unless you start it with `--apply`. Without a terminal (or with `--json`) it
reports where you are; `--run`, `--done`, and `--skip` move it along. See
[docs/spec/commands/guide.md](docs/spec/commands/guide.md).

## With an AI agent

`offramp mcp serve` exposes every command to an agent over MCP, as a dry run unless you start
it with `--allow-apply`. For Claude Code and other agents that load skills,
[`skills/offramp-migration`](skills/offramp-migration/SKILL.md) is a guide to running a
migration with Offramp: the phases, the ground rules (dry runs first, no commits, verify every
step), how to check Offramp's answers against the code before reporting them, and which decisions
to leave to people. It is drawn from the field tests on four large codebases; copy it into your
repository's `.claude/skills/` ([skills/README.md](skills/README.md)).

## Five-minute tour

```bash
# What am I dealing with?
offramp graph --format html --out graph.html --exclude-kind test
offramp deps audit --target 10

# What can I port today?
offramp plan --frontier

# Move the tests out of production assemblies, purely, with git mv
offramp move tests --project src/Foo/Foo.csproj --dry-run
offramp move tests --project src/Foo/Foo.csproj

# Move files in bulk to a .NET Standard or dual-target project
offramp move plan --from src/Foo/Foo.csproj --to src/Foo.Core/Foo.Core.csproj --out moves.json
offramp move apply --plan moves.json --verify end

# One version per package, with your hard pins respected
offramp deps consolidate --package Microsoft.Extensions.Logging --dry-run
offramp deps consolidate --all --apply

# What will break at runtime even though it compiles?
offramp audit behavior --target 10 --json > behavior.json
```

Add `--json` to any command and pipe it into whatever comes next.

## In your editor

Most people in a large codebase are not working on the migration, but every class they add to a
.NET Framework-only project is one more to migrate. The VS Code extension (`editors/vscode`;
Visual Studio and Rider are next on the roadmap) runs `offramp ide serve` next to the C#
extension and, on the lines you add or change:

- flags APIs modern .NET does not have, with what to use instead;
- notices a new class that needs nothing from .NET Framework and offers to move it to the
  project's portable counterpart, a `git mv` that leaves the file's contents alone;
- puts a **Move to …** lens on existing classes that can move there as they are.

It is on by default in repositories with an `.offramp` folder and off elsewhere
(`offramp.enabled`). Tell it where each project's portable code lives with `projectMap` in
`offramp.yml`, or let it use the portable projects a project already references. The same checks
run in CI with `offramp ide check --base origin/main --fail-on error`. See
[docs/spec/commands/ide.md](docs/spec/commands/ide.md).

## Compiling .NET Framework code on macOS and Linux

Yes, you can. Offramp's analysis, and your IDE's IntelliSense, work on a Mac
against `net48` projects because the compiler only needs reference assemblies,
which Microsoft ships as a NuGet package. You cannot *run* the result, and a
handful of build steps such as `sgen` need Windows. See
[docs/compiling-on-macos.md](docs/compiling-on-macos.md) for the setup and for
the compiler-log fallback that lets a Windows build agent do the parts a Mac
can't.

## Configuration

`offramp.yml` at the repository root controls targets, verification, package
pins, rule severities, and exclusions. Everything Offramp decides can be
overridden there, and every override is recorded in the output so the debt
stays visible. See [docs/spec/03-configuration.md](docs/spec/03-configuration.md).

## Design principles

- **Deterministic first.** Roslyn, MSBuild logs, and NuGet's own libraries make
  the decisions. An LLM is optional garnish for naming and ranking, never for
  what gets moved or which version gets picked. Every command runs with
  `--no-llm`.
- **Verify with the real toolchain.** Moves are checked by compiling. Version
  choices are checked by restoring. Offramp does not reimplement the compiler
  or NuGet's resolver.
- **Pure moves.** A move never edits file contents, so pull requests show
  renames and nothing else.
- **Output for machines, rendering for humans.** Stable JSON envelopes with
  schemas, stable diagnostic codes, NDJSON progress on stderr, and pretty
  terminal output only when there is a terminal.
- **Don't reinvent the wheel.** Offramp stands on Roslyn, Basic.CompilerLog,
  the NuGet client libraries, ApiCompat, try-convert, YARP, CoreWCF, and the
  Windows Compatibility Pack. See
  [docs/spec/00-architecture.md](docs/spec/00-architecture.md).

## Contributing

Read [CLAUDE.md](CLAUDE.md) for conventions (it is written for AI agents and
humans alike), [docs/ROADMAP.md](docs/ROADMAP.md) for what's next, and
[docs/spec](docs/spec) for the command contracts. Keep
[CHANGELOG.md](CHANGELOG.md) current in every pull request.

## License

MIT. See [LICENSE](LICENSE).
