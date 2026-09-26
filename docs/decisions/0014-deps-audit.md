# 0014. How `deps audit` searches versions and reports what it cannot know

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/deps.md#deps-audit`, `#deps-gac`

## Context

The spec defines "supports target" per version but not how many versions to
inspect (Newtonsoft.Json has over 80), what `lowestSupporting` ranges over, what
the status is when no feed has the package or a feed is down, where the
Windows-only check looks, or how the package map is extended from `offramp.yml`.
`deps gac` asks for "a count of usages" without saying what is counted.

## Decision

- **Candidates** are listed stable versions (prerelease when asked) plus every
  in-use version. **`newestSupporting`** walks down from the newest candidate.
  **`lowestSupporting`** is a binary search below it that assumes support, once
  added, is kept; every version it returns was inspected and supports the target,
  and a test compares it with inspecting every recorded version. Inspections are
  cached per version forever; version lists and deprecation are fetched each run.
- **`status: unknown`** (a fifth status) when no feed has the package (`OFR1005`)
  or the feeds could not be reached (`OFR1006`, `partial: true`, exit 4).
- **Windows-only** looks at the assets NuGet would pick for the target (the
  nearest `lib/`, else `ref/` folder) of the newest supporting version, then the
  in-use ones, and reports the assembly and the reason.
- **Deprecation** is reported per in-use version, and for the package when the
  newest version is deprecated (how feeds deprecate a whole package).
- **`deps.packageMap`** adds `{ package | prefix, replacement }` entries on top
  of `rules/package-map.yml`: an exact id beats any prefix, the longest prefix
  wins, configuration beats the built-in table.
- **Rule tables** live at `rules/package-map.yml` and
  `rules/framework-assemblies.yml` and are embedded in `Offramp.NuGet`.
- **`deps gac` usages** count names in C# source that bind to a type or member
  of the assembly, in the compilation rebuilt from the compiler log for the
  project's .NET Framework target (`Offramp.Analysis`); `null` without a compiler
  log or for other languages.
- **Formats**: `--format table|markdown|json` choose the human view; `markdown`
  and `json` print the document alone to stdout (diagnostics to stderr), `--json`
  the envelope.

## Alternatives considered

- Inspecting every published version: exact without the monotonic assumption,
  but hundreds of downloads for a large solution's first audit.
- Reading frameworks from registration metadata instead of the nupkg: the spec
  forbids guessing from dependency groups alone, and registrations list no files.

## Consequences

- A package that dropped and later re-added support for the target could report
  a `lowestSupporting` above the true lowest; the version reported still works.
  `deps consolidate` verifies its choices with restore regardless.
