# Handoff prompt for an autonomous agent

Paste the block below as the task for Claude on the Web (or any agent) working
against this repository. It assumes the agent can read the repo, run
`dotnet`, and open pull requests.

---

You are implementing **Offramp**, an open-source .NET global tool that helps
migrate large .NET Framework codebases to modern .NET. The repository already
contains the complete specification. Your job is to build it, milestone by
milestone, until `docs/ROADMAP.md` is fully released.

**Read first, in this order:** `CLAUDE.md` (the contract you work under),
`docs/ROADMAP.md` (what to build and in what order, with acceptance criteria),
`docs/spec/00-architecture.md` through `04-testing-and-fixtures.md`, then the
`docs/spec/commands/*.md` file for whatever you are building. `docs/decisions/`
records resolved ambiguities; add to it whenever you resolve one.

**How to work:**

1. Start with M0 and proceed strictly in roadmap order. Do not start a
   milestone before the previous one is merged and released.
2. One branch and one pull request per milestone, or per command when a
   milestone has several commands. Never commit to `main`.
3. Before opening a PR, satisfy every acceptance criterion for the milestone,
   run `dotnet build` and `dotnet test` locally, and confirm CI is green on
   ubuntu, macos, and windows. If CI fails, fix it in the same PR.
4. Update `CHANGELOG.md` under `[Unreleased]` in every PR. Update any spec
   file whose contract you changed, in the same PR, and say why in the PR
   description.
5. When the milestone is merged, perform the release steps in
   `docs/RELEASING.md` (changelog PR, then tag). The first tag is `v0.1.0`.
6. When the spec is ambiguous, choose the more deterministic, more
   conservative, more testable option, write an ADR in `docs/decisions/`, and
   continue. Do not stop to ask unless proceeding would require deleting or
   rewriting user data in a way the spec forbids.
7. Follow the non-negotiables in `CLAUDE.md` literally: determinism, pure
   moves, real-toolchain verification, no LLM dependency in core libraries,
   JSON contracts and stable diagnostic codes for everything, dry-run by
   default, changelog in every PR.
8. Write the tests the spec calls for, including the negative tests that prove
   each verification path can fail. A check that cannot fail is a bug.
9. Prefer the libraries named in `CLAUDE.md` and the spec (Roslyn,
   Basic.CompilerLog, MSBuild StructuredLogger, NuGet client libraries,
   System.CommandLine, Spectre.Console, Verify). Do not write a NuGet resolver,
   a project evaluator, or a C# parser.
10. Keep the CLI pleasant: progress for anything over a second, a one-line
    headline result, tables for lists, colors with meaning, and identical
    behavior with `--json` for machines.

**Definition of done for the whole task:** every milestone in
`docs/ROADMAP.md` is marked released, `dotnet tool install -g offramp`
installs the latest version from nuget.org, and every command's `--json`
output validates against its schema in `schemas/v1/`.

Report progress at the end of each milestone: what shipped, the version tag,
ADRs written, and anything deferred with the reason.

---
