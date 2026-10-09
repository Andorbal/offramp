# 0069. Number sites per member name, report each change once, and judge offline against the measured release

- Status: accepted
- Date: 2026-10-09
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-scan`

## Context

Building `newtonsoft scan` left five questions the spec did not answer:

1. Site ids were `<type>.<member>#<n>` with `n` counted per member. Overloads
   share a name, so two members' first sites got the same id.
2. A payload type that several sites carry would be reported at every one of
   them: a `Product` written by three methods gave three identical camel-case
   findings.
3. A test project that gets Newtonsoft.Json only through the project it tests
   (an SDK-style `ProjectReference`) is neither a package reference nor
   `packages.config`.
4. The spec put an application's redirects on every project it deploys, which
   needs the deployment graph `redirects sync` builds.
5. The target is the newest release a feed lists. Without network there is no
   list, and every behavior rule needs a target to compare labels with.

## Decision

1. `n` counts the sites of all members with that name in the type, in file and
   position order. Ids stay readable and are unique.
2. A behavior finding is reported once per payload member (or type), at the
   first site in file order that carries it, and its details name the site.
   Caught-exception and converter findings are at the code itself.
3. `via` gains `transitive`.
4. A project's `redirects` are those in its own `app.config` (`web.config` for
   a web project). An application lists what it deploys. Computing the redirects
   each library would get is `upgrade`'s job, through `redirects sync`.
5. With every source unreachable, the target is the newest release the
   differential test measures (13.0.4) if the range allows it. OFR3725 says the
   trial compilation needs that release's package. The behavior rules are
   measured facts up to that release, so this judgment is the same one a feed
   would give whenever 13.0.4 is the newest release.

The serializer-argument tracing `audit serialization` already had moved into
`PayloadTypes`, which both commands use. The serialization audit snapshots are
unchanged.

## Alternatives considered

- **Number sites by member documentation ID.** Unique, but ids such as
  `M:Contoso.Catalog.CatalogCodec.Write(Contoso.Catalog.Product)#1` are
  unreadable, and probe ids derive from them.
- **Report at every site.** Complete, but the count measures how often a type is
  serialized, not how many things change.
- **Fail without a feed.** CI and air-gapped machines are where scans run
  unattended.

## Consequences

- Adding an overload with a site shifts the numbers of later sites with that name.
  Probe ids follow, and `tests` regenerates them (a rename, not a lost baseline,
  since baselines are matched by id and a missing one is reported).
- A finding's location is one representative site. `details.site` names it, and
  the project's sites show the others.
