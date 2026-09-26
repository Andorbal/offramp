# 03. Configuration: `offramp.yml`

One file at the repository root. `offramp init` writes it with detected
values and comments. Precedence, lowest to highest: built-in defaults,
`offramp.yml`, environment variables `OFFRAMP_*` (double underscore for
nesting: `OFFRAMP_VERIFY__TIMEOUT_SECONDS=1200`), command-line flags. The
merged result is echoed in every JSON envelope as `effectiveConfig`.

## Full example with defaults

```yaml
# offramp.yml
version: 1

target: 10                     # integer; --target overrides
solution: src/Monolith.sln     # optional when the repo has exactly one

paths:
  exclude:                     # globs, repo-relative; excluded projects stay in the model but are never modified
    - "legacy/unrelated/**"
    - "**/*.Samples.csproj"
  state: .offramp              # where workspace.json, cache, ledger, journals live

projects:                      # per-project overrides
  - path: src/Foo.Host/Foo.Host.csproj
    kind: service              # override detected kind
  - path: src/Legacy/Legacy.csproj
    frozen: true               # never move files into or out of, never edit csproj

verify:
  mode: build                  # build | command | none
  command: null                # when mode=command: run this; exit 0 = pass; stdout may be a JSON envelope
  timeoutSeconds: 1800
  configuration: Debug
  properties:                  # extra -p: values for every verification build (and for scan)
    GenerateSerializationAssemblies: "Off"
    TreatWarningsAsErrors: "false"
  noWarn: [ "CS1591", "NU1603" ]
  warnAsError: [ ]
  projects:                    # restrict what verify builds; default: affected projects
    include: [ ]
    exclude: [ ]
  restore: true
  onFailure: rollback          # rollback | keep (for movers)

deps:
  feeds: null                  # null = use nuget.config; or a list of source URLs
  includePrerelease: false
  preferNewest: false          # consolidation picks the lowest satisfying version unless true
  families:                    # packages that must share one version
    - prefix: "Microsoft.Extensions."
    - prefix: "System.Text.Json"    # single package but participates in the M.E. family on netfx
      family: "Microsoft.Extensions."
  pins:
    - package: Newtonsoft.Json
      project: src/Customer.Api/Customer.Api.csproj
      version: 9.0.1
      reason: "Customer integrations depend on 9.x serialization behavior"
    - package: log4net
      version: 2.0.15         # no project = pin everywhere
      reason: "Ops-approved version"
  ignore: [ "Our.Internal.BuildTools" ]   # never audit or consolidate
  packageMap:                 # successors, added to rules/package-map.yml
    - package: Contoso.Legacy.Reporting
      replacement: "Contoso.Reporting (the rewrite)"
    - prefix: "Contoso.Wcf."
      replacement: "Contoso.Grpc.* clients"
  cpm:
    file: eng/Packages.props  # where consolidate writes; if not Directory.Packages.props, projects opt in via DirectoryPackagesPropsPath
    scope: solution           # solution | repo
  redirects:
    manage: [ "src/Web/Web.csproj" ]      # app/web.config files to keep in sync

move:
  verify: end                 # none | per-project | batch:N | end
  namespaceMismatch: allow    # allow | warn | block
  coMove: closure             # closure | none  (move dependent files automatically, or refuse)
  suppressAnalyzers: [ "IDE0130" ]
  tests:
    frameworks: [ xunit, nunit, mstest, tunit ]
    helperMinConfidence: high # high | medium | low
    stripTestsSegment: true   # Foo/Service/Tests/X.cs -> Foo.Tests/Service/X.cs
    targetSuffix: ".Tests"

rules:                        # audit/behavior/serialization rule overrides
  OFR3105: { severity: none, reason: "We never run on Linux" }        # disable
  OFR3210: { severity: error }                                          # promote
  packs:
    disable: [ "desktop" ]    # rule packs: core, web, desktop, data, serialization, native

seams:
  unportableSources: [ audit ]   # audit | list
  unportableSymbols: [ ]         # explicit fully-qualified names when source=list
  hostFramework: auto            # auto | net10-windows | net48

service:
  host: linux                    # linux | windows | both
  dockerfile: true
  k8s: false
  healthEndpoint: true
  logging: json-console

llm:
  enabled: false
  provider: openai               # openai (any OpenAI-compatible URL) | anthropic
  url: http://localhost:1234/v1
  model: null                    # null = first model the server lists (openai) / required (anthropic)
  apiKeyEnv: OFFRAMP_LLM_API_KEY
  uses: [ naming, ranking ]      # what the LLM may be asked to do; never: decide moves/versions

report:
  title: "Monolith migration"
  ledger: .offramp/ledger
```

## Validation

`offramp doctor` validates the file against `schemas/v1/config.json` and
reports unknown keys as `OFR0050` (warning) so typos do not silently disable a
setting. A pin without a `reason` is `OFR0051` (warning). A rule override
without a `reason` is `OFR0052` (info) so suppression is always attributable.

Every command loads and validates the file the same way. Unknown keys are
ignored (and never echoed in `effectiveConfig`). A value of the wrong type or
outside the allowed set is `OFR0053` (error), YAML syntax errors are `OFR0054`,
a missing `--config`/`OFFRAMP_CONFIG` file is `OFR0055`, and a bad `OFFRAMP_*`
value is `OFR0056`; with any of these a command stops with exit 2 (`doctor` and
`init` report instead). Diagnostics about the file carry its line and column.
`effectiveConfig` lists keys in ordinal order. Details:
`docs/decisions/0003-configuration-loading.md`.

## `init`

`offramp init` interviews on a TTY (target, solution, verify mode, pins,
CPM file location), showing detected values as defaults, and writes the file
with comments. `init --defaults` writes without asking. It also:

- adds `.offramp/cache/`, `.offramp/*.binlog`, `.offramp/*.complog`, and
  `.offramp/journal/` to `.gitignore`;
- offers to add the macOS/Linux compile-only conditional block to
  `Directory.Build.props` when `doctor` found Windows-only build steps
  (`docs/compiling-on-macos.md`).

## Environment variables

| Variable | Purpose |
|---|---|
| `OFFRAMP_TARGET` | default target |
| `OFFRAMP_CONFIG` | config path |
| `OFFRAMP_STATE` | state directory |
| `OFFRAMP_LLM_PROVIDER`, `OFFRAMP_LLM_URL`, `OFFRAMP_LLM_MODEL`, `OFFRAMP_LLM_API_KEY` | LLM |
| `OFFRAMP_NO_COLOR`, `NO_COLOR` | disable color |
| `OFFRAMP_VERIFY__*` | any `verify.*` key |

In general `OFFRAMP_A__B_C` sets `a.bC`: `__` separates levels and each
`UPPER_SNAKE` segment becomes camelCase (`OFFRAMP_MOVE__TESTS__TARGET_SUFFIX` sets
`move.tests.targetSuffix`). Values are typed by the setting: integers, booleans
(`true`/`false`/`1`/`0`/`yes`/`no`), and lists separated by `;` or `,`. Names
that match no setting are ignored.
