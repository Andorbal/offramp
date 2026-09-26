# 0013. Graph views: readiness, output routing, and an in-page layout

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/graph.md`

## Context

The graph spec lists node fields (`readiness`, `blockers`, `directory`) without
defining them, says `--highlight blockers` marks "the framework-only projects
with the most dependents" without a count, asks for an HTML page with an
"ELK-style" layout shipped inline, and leaves open where a DOT or Mermaid
rendering goes when there is no `--out`, given that `--out` normally receives
the envelope.

## Decision

- **Readiness** (shared with `plan`, `Offramp.Workspace.Model.Readiness`):
  standard, modern, and dual projects are `done`; a framework-only project is
  `blocked` by every framework-only project it reaches through its dependencies,
  and `ready` when it reaches none. Readiness, blockers, and `dependents`
  (transitive) are computed on the whole model before any view filter.
- **Directory** is the parent of the project's folder (`src` for
  `src/Foo/Foo.csproj`, `.` near the root), which groups sibling projects.
- **Cycles** are the model's cycles restricted to shown projects; hiding
  assembly edges (`--edges project`) never hides a cycle.
- **Blockers highlight** marks the ten framework-only projects that appear in
  the most `blockers` lists, ties broken by id.
- **Output routing**: the rendering is the command's primary output. With
  `--out` it is written there (not the envelope); with a format and no `--out`,
  stdout carries exactly the document and diagnostics go to stderr, so it can be
  piped. `slice` without `--out` follows the same rule. The format is inferred
  from the `--out` extension when omitted.
- **HTML layout**: a layered (Sugiyama-style) layout written for the page:
  strongly connected components condensed, longest-path layers with dependencies
  on the left, barycenter ordering sweeps with id tie-breaks, and edges that skip
  layers arced above the nodes between. No third-party library is embedded.

## Alternatives considered

- Embedding elkjs or dagre: 300 KB to 1.5 MB of script in every page and a
  license to track, for a layout the page needs only in its simplest form.
- Computing coordinates in C#: deterministic output but no re-layout when the
  viewer filters or focuses in the page.
- Direct-only blockers: understates what must be ported first.

## Consequences

- The page's layout is not snapshot-tested; the embedded data is, and the page
  was checked in Chromium (rendering, focus, filters, clusters, export).
- `plan` (M3) reuses the readiness computation as is.
