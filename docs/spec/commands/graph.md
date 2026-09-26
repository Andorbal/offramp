# `graph`

Project dependency graph with framework class and project kind, in formats
for machines, documents, and people.

```
offramp graph [--format json|dot|mermaid|html] [--out PATH]
              [--exclude-kind test,console,...] [--include-kind ...]
              [--focus PROJECT] [--depth N] [--direction both|dependencies|dependents]
              [--cluster none|directory|kind] [--highlight cycles|frontier|blockers|none]
              [--edges project|all]
```

## Data (`--format json`)

The graph document (`schemas/v1/graph-document.json`); the HTML page embeds the
same JSON in `<script type="application/json" id="offramp-graph">`.

```jsonc
{
  "nodes": [
    { "id": "src/Foo/Foo.csproj", "name": "Foo", "kind": "library", "frameworkClass": "dual",
      "targetFrameworks": ["net48","net10.0"], "loc": 18234, "packageCount": 12,
      "readiness": "ready|blocked|done", "blockers": ["src/Legacy/Legacy.csproj"],
      "dependents": 7,                  // transitive, in the whole model
      "directory": "src",               // parent of the project's folder; "." near the root
      "cluster": null,                  // directory or kind with --cluster, else null
      "inCycle": false }
  ],
  "edges": [ { "from": "src/Foo/Foo.csproj", "to": "src/Bar/Bar.csproj", "kind": "project|assembly" } ],
  "cycles": [ ["src/A/A.csproj","src/B/B.csproj"] ],
  "legend": { "frameworkClass": { "framework": "#E07A1F", "standard": "#2B6CB0", "modern": "#2F855A", "dual": "#2C7A7B" },
              "kind": { "library": "rounded box", ... }, "edge": { ... }, "cycle": "#C53030" },
  "highlight": { "mode": "cycles|frontier|blockers|none", "nodes": [ ... ] },
  "view": { "includeKinds": [], "excludeKinds": ["test"], "focus": null, "depth": null,
            "direction": "both", "cluster": "none", "highlight": "cycles", "edges": "all" }
}
```

Readiness: `done` for standard, modern, and dual projects; a framework-only
project is `blocked` by every framework-only project it depends on, directly or
transitively, and `ready` when there is none. Readiness, blockers, and
dependents always come from the whole model, so a filtered view never hides why
a project is blocked, and cycles stay listed even when `--edges project` hides
the edges that form them (`docs/decisions/0013-graph-views.md`).

The command's envelope result (`schemas/v1/graph.json`) is
`{ format, output, graph, content }`: the format (null when none was asked for),
the file written, the document above, and the rendering when it was not written
to a file.

`kind: assembly` edges are references by `HintPath` to another project's
output; they are how cycles hide in legacy solutions. `--edges project` omits
them.

## Visual encoding (all visual formats)

| Channel | Meaning |
|---|---|
| Fill color | framework class (fixed palette above; colorblind-safe, also used by `report`) |
| Shape | kind: library = rounded box, console = box, service = hexagon, web = house/tab, test = dashed rounded box, winforms/wpf = double box |
| Border | red when the node is in a cycle |
| Edge style | solid = ProjectReference, dashed = assembly HintPath reference |
| Badge | readiness when `--highlight frontier|blockers` |

## Formats

- **DOT**: Graphviz, with `subgraph cluster_*` for `--cluster`. Deterministic
  node ordering.
- **Mermaid**: `flowchart LR` with `classDef` per framework class. Mermaid has
  no shapes for everything; kinds map to the closest available and the legend
  lists the mapping. Large graphs emit `OFR0201` (Mermaid renders poorly above
  ~300 nodes; suggest `--focus` or HTML).
- **HTML**: single self-contained file. Force-directed or layered layout
  (ELK-style layering preferred for readability; ship the layout library
  inline, no CDN). Features: search box, filter by kind and framework class,
  click a node to focus with depth slider, hover for details (targets,
  packages, LOC, blockers), toggle clusters, cycles list, "export PNG/SVG",
  light/dark, legend. The file embeds the JSON so other tools can read it
  back from the page (`<script type="application/json" id="offramp-graph">`).

## Options

- `--format` is inferred from `--out`'s extension (`.json`, `.dot`/`.gv`,
  `.mmd`, `.html`) when omitted; an unknown extension is a usage error. With a
  format and no `--out`, stdout carries exactly the document (diagnostics go to
  stderr), so `offramp graph --format dot | dot -Tsvg` works. `--out` receives
  the rendering itself, not the envelope; `--json` puts the rendering in the
  envelope's `content` when there is no `--out`. Without a format, the human view
  is a summary by framework class with the projects blocking the most dependents.
- `--focus PROJECT --depth N --direction`: subgraph around a project (a path or
  a unique name; unknown is `OFR0021`). `--depth` needs `--focus`; without it the
  walk is unlimited.
- `--exclude-kind test` is the common stakeholder view; `--include-kind`
  is the inverse whitelist; when both are given, exclusion wins.
- `--highlight frontier` marks projects portable today (`ready`); `blockers`
  marks the ten framework-only projects that block the most others (the ones
  whose porting unlocks the most); `cycles` (the default) marks cycle members.
- `--cluster directory|kind` groups nodes (DOT clusters, Mermaid subgraphs, HTML
  bands).

## Acceptance

- Snapshot tests for json/dot/mermaid on `dual-target`, `cycle`, `tests-in-prod`.
- HTML smoke test: file opens without network (assert no `http` in `src=`/`href=`
  except the repo link), embedded JSON round-trips.
- Deterministic across OSes.
