# 0033. Route the HTML graph's edges orthogonally around the boxes

- Status: accepted
- Date: 2026-09-28
- Spec section: `docs/spec/commands/graph.md` (HTML)
- Supersedes: the "HTML layout" bullet of 0013

## Context

ADR 0013's layout drew each edge as one curve from dependent to dependency,
arcing edges that skip layers above the nodes between. On a solution of about
ninety projects the curves ran through boxes and piled up between the layers,
so it was hard to see what references what. Tools such as JetBrains' project
diagrams route edges around boxes in a larger but orderly drawing.

## Decision

The page keeps a layered layout written for it (no third-party library), with
dependencies on the left, and routes edges orthogonally:

- Components are layered one right of their furthest dependency, then moved
  toward their dependents when they have more dependents than dependencies,
  which shortens edges to shared libraries.
- An edge that skips layers gets a lane in each layer it crosses. Edges of one
  kind into one project share their lanes and tracks, so they merge like a bus
  and end in one arrowhead.
- Rows are ordered by barycenter sweeps with a transpose pass, keeping the
  ordering with the fewest weighted crossings; clusters keep their rows
  together and stack as bands.
- Rows are placed by weighted isotonic regression (pool adjacent violators):
  lanes pull hardest, so long edges run straight; boxes keep an 18 px gap and
  lanes a 9 px gap; positions are whole pixels.
- Each gap between layers gets vertical tracks, one per edge target. Tracks for
  edges going down are ordered by where they start, tracks for edges going up
  the other way, so edges going the same way do not cross; edges within a
  cycle loop through the gap to the right of their layer.
- Hovering a project highlights its edges and brings them to the front.

The layout is its own script (`<script id="offramp-layout">`) with no DOM
access. `GraphLayoutTests` runs it with Node.js on the fixtures and on a
generated ninety-project solution and checks that every edge starts at its
source, ends at its target, is orthogonal, crosses no box, and that two runs
agree; it also proves the check fails when a box sits on a route. The test is
skipped where Node.js is not installed; the CI runners have it.

## Alternatives considered

- elkjs or dagre inline: 300 KB to 1.5 MB per page and a license to track, as
  0013 found; the routing needed is a few hundred lines.
- One track per edge instead of per target: no merging, but channels several
  hundred pixels wide on hub libraries.
- Computing the layout in C#: snapshot-testable, but no re-layout when the page
  filters, focuses, or clusters.

## Consequences

- Drawings are larger (about ten per cent wider and a quarter taller on the
  generated solution); zoom and pan cover it.
- A merged bus does not show which source an edge came from; hovering a
  project or selecting it does.
