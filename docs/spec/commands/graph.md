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

```jsonc
{
  "nodes": [
    { "id": "src/Foo/Foo.csproj", "name": "Foo", "kind": "library", "frameworkClass": "dual",
      "targetFrameworks": ["net48","net10.0"], "loc": 18234, "packageCount": 12,
      "readiness": "ready|blocked|done", "blockers": ["src/Legacy/Legacy.csproj"], "directory": "src" }
  ],
  "edges": [ { "from": "src/Foo/Foo.csproj", "to": "src/Bar/Bar.csproj", "kind": "project|assembly" } ],
  "cycles": [ ["src/A/A.csproj","src/B/B.csproj"] ],
  "legend": { "frameworkClass": { "framework": "#E07A1F", "standard": "#2B6CB0", "modern": "#2F855A", "dual": "#2C7A7B" } }
}
```

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

- `--focus PROJECT --depth N --direction`: subgraph around a project.
- `--exclude-kind test` is the common stakeholder view; `--include-kind`
  is the inverse whitelist.
- `--highlight frontier` marks projects portable today; `blockers` marks the
  `framework`-only projects with the most dependents (the ones whose porting
  unlocks the most).

## Acceptance

- Snapshot tests for json/dot/mermaid on `dual-target`, `cycle`, `tests-in-prod`.
- HTML smoke test: file opens without network (assert no `http` in `src=`/`href=`
  except the repo link), embedded JSON round-trips.
- Deterministic across OSes.
