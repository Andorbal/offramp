# 0070. Find probe entry points through a method-level call graph, nearest hermetic caller first

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#entry-points`

## Context

A characterization probe is most useful when it calls the code the application
calls: the method that builds the settings, picks the payload, and serializes,
not `JsonConvert` with settings reconstructed by Offramp. That means finding,
for each site, a member a test can call. Offramp had no method-level call graph.
`seams` works between types, and `audit dead-code` indexes uses of symbols but
not which member makes them.

## Decision

A `CallGraph` over every non-test C# project's recorded compilation records,
by documentation ID, which member uses which. Uses are calls, creations, and
property and field accesses. A use inside a lambda or local function counts for
the member around it, and an accessor for its property. A use of an interface
member or a virtual method counts for every source implementation and override.
`audit dead-code`'s alias logic moved to `SymbolIdentity` so both share it.

The entry-point search goes breadth first from the site's member up through its
callers, to `newtonsoft.tests.entryDepth` callers (default 4). It takes the first
member that passes six checks in a fixed order:

1. accessible (public, or internal with a test project as friend);
2. constructible (static, or a concrete type with a constructor whose
   parameters can be generated);
3. callable (every parameter generatable or an output);
4. hermetic (nothing within six calls, through implementations too, is in
   `rules/newtonsoft-hermetic.yml`);
5. observable;
6. for deserialize and DOM sites, the JSON comes from a parameter.

Callers in test projects never count. `object` parameters are not generatable,
so a site in `Wrap(object)` resolves to the caller that passes a `Product`.

When nothing qualifies, the site is uncovered:

- with `depthExceeded` if callers remained beyond the limit;
- otherwise with the site member's own reason.

The candidates examined are listed so the user sees the whole chain.

## Alternatives considered

- **Probe every site through `JsonConvert` only.** That is simpler, but it
  misses what the application adds: settings built in code, the payload it
  assembles, and post-processing.
- **Prefer the outermost public API.** That probes more code per test, but
  reaches I/O and dependencies far more often, and makes failures harder to
  attribute.
- **Use SymbolFinder over an MSBuildWorkspace.** That needs MSBuild at analysis
  time, which `scan` deliberately removed (compiler logs). It is also slower on
  large solutions.

## Consequences

- The hermetic check is conservative: it follows every call a member could
  make, so a method that only sometimes touches a file is not an entry point.
  `newtonsoft.hermetic.nonHermetic` can only add to the list, so a false
  "hermetic" needs a code fix, never configuration.
- Calls made through reflection, dependency injection, or delegates stored in
  fields are invisible. Sites reached only that way are uncovered, with the
  reason of their own member.
- Building the graph loads every project's compilation once. On the fixtures
  this is under a second; very large solutions pay the compile-log read they
  already pay for audits.
