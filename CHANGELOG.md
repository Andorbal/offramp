# Changelog

All notable changes to Offramp are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/). Until 1.0, minor versions may
contain breaking changes to command output; every such change is called out
under **Changed** with a migration note.

Each pull request adds its entries under `[Unreleased]`. A release moves that
block under a version heading with the date. `docs/RELEASING.md` has the steps.

## [Unreleased]

### Added
- Handoff pack: README, contributor contract (CLAUDE.md), specification
  (`docs/spec/`), roadmap, release process, CI and release workflows.
- Solution scaffold: `Offramp.slnx`, `global.json` (.NET 10 SDK), central package
  management, `.editorconfig`, `.gitattributes`, and `Offramp.Core`,
  `Offramp.Workspace`, `Offramp.Cli` with one test project each plus
  `Offramp.Fixtures` for shared test support.
- `offramp` global tool (`net8.0;net10.0`, `RollForward=LatestMajor`) built on
  System.CommandLine and Spectre.Console: all global options, exit codes
  (0/1/2/3/4/130), the JSON envelope (`schemas/v1/envelope.json`), NDJSON progress
  on stderr with `--json` (`schemas/v1/progress.json`), a live progress display on
  terminals, plain progress lines otherwise, and `NO_COLOR`/`TERM=dumb` support.
- `offramp doctor`: checks the .NET SDKs, `global.json` selection, the target,
  .NET Framework reference assemblies, git, the repository, `offramp.yml`, and the
  workspace model, each with a remedy (`schemas/v1/doctor.json`).
- `offramp init`: writes `offramp.yml` with detected values (interview on a
  terminal, `--defaults` without), adds Offramp's state to `.gitignore`, previews
  with `--dry-run`, and never overwrites without `--force` (`schemas/v1/init.json`).
- `offramp --version` and `--help` with examples for every command.
- `offramp.yml` loading with the documented precedence (defaults, file,
  `OFFRAMP_*`, flags), validation against `schemas/v1/config.json`, positions on
  every configuration diagnostic, and the merged `effectiveConfig` in every envelope.
- Diagnostic catalog with codes OFR0001, OFR0010–0016, OFR0020, OFR0030,
  OFR0050–0056, OFR0099, and OFR1006; `docs/diagnostics.md` is now generated from
  it (`eng/gen-diagnostics.sh`), and tests fail when a code lacks documentation or
  a test that produces it.
- Workspace model records (`docs/spec/02-workspace-model.md`), `ICache`,
  `IGitService` (`git mv`, porcelain status), and a git-compatible unified diff
  renderer for dry runs.
- ADRs 0002–0006: CLI foundations, configuration loading, `init` writes, test
  stack, and the doctor contract.

### Changed
- `Directory.Build.props` no longer produces a separate symbols package: PDBs
  are embedded, so `dotnet pack` failed with NU5017 when asked for a `.snupkg`.

[Unreleased]: https://github.com/Andorbal/offramp/compare/main...HEAD
