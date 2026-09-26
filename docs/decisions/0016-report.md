# 0016. Report: the model is the last point, applications by closure, charts drawn on the server

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/workspace.md#report`

## Context

The report spec says to read ledger snapshots and compute, per snapshot, totals
by framework class, kind, and "top-level directory", plus applications "and their
readiness from `plan --for`". Snapshots carry no reference edges, so readiness
cannot come from them; `plan` does not exist yet (M3); "top-level directory" is
`src` for most repositories; and the spec does not say what "now" is, what the
default format and title are, what `--since` accepts, or how a page with "no
external scripts" embeds the interactive graph.

## Decision

- **Now is the workspace model.** The series is every snapshot at or after
  `--since` and strictly older than the model, then the model itself (the scan
  that wrote the model also wrote a snapshot with the same time, so it is not
  counted twice). Snapshots newer than the model are left out: the report
  describes the model it was given. `asOf` is the model's `createdAt`; the
  report never reads the clock.
- **Areas, applications, and the frontier come from the model**, not from each
  snapshot. An area is the directory holding project folders, the same rule as
  `graph --cluster directory` (`0013`), so `src/Billing/Billing.Core/…` and
  `src/Billing/Billing.Web/…` share `src/Billing`.
- **Applications** are console, service, web, winforms, and wpf projects (the
  spec lists the first three; desktop applications are applications too). An
  application's status looks at its whole closure: `done` when nothing in it is
  framework-only, `ready` when only the application itself is, `blocked`
  otherwise. `next` lists the framework-only projects in the closure that are
  ready today. When `plan --for` arrives it must agree with these numbers.
- **Frontier**: framework-only projects that are `ready`, most dependents
  first, ties by id.
- **Formats and routing** follow `graph`: `--format html|json|markdown`,
  inferred from `--out`'s extension; with a format and no `--out`, stdout
  carries exactly the document. Without a format the terminal shows a summary.
  `--with-graph` needs HTML and is otherwise a usage error.
- **`--since`** accepts `yyyy-MM-dd` (compared as a prefix of `createdAt`, so the
  whole day is included) or an ISO 8601 time, normalized to UTC
  `yyyy-MM-ddTHH:mm:ssZ`.
- **Title**: `--title`, else `report.title`, else the repository folder's name.
- **Charts are SVG drawn in C#**: stacked areas by class over time (framework at
  the bottom, its top edge drawn as the burn-down line, x proportional to time),
  and one stacked horizontal bar per area. Numbers are formatted with the
  invariant culture and rounded to 0.1 units, so the same data gives the same
  bytes. A single point is drawn as one column.
- **No scripts in the page.** The graph, which needs its scripts, is embedded
  with `<iframe srcdoc sandbox="allow-scripts allow-downloads">`; the page itself
  stays script-free and hides the graph when printed.
- **`OFR0202`** (warning) names each JSON file in the ledger directory that is
  not a snapshot, instead of skipping it silently.

## Alternatives considered

- Recomputing areas per snapshot: possible from snapshot project ids, but the
  chart shows the present and the extra series would only grow the JSON.
- Top-level directory as the area: one bar called `src` in most repositories.
- A client-side charting library: scripts in a page meant for printing and
  pasting, and output that is not byte-stable.
- Embedding the graph page inline: its scripts and styles would collide with the
  report's, and the report would no longer be script-free.

## Consequences

- A report is reproducible from the committed ledger and a model; CI can publish
  it on every scan.
- `plan --for` (M3) owns application readiness from then on and must match the
  report's rule, or this ADR is superseded.
