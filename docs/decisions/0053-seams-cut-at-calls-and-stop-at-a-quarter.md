# 0053. Build `seams` cycles from structure, leave package-supplied APIs out, and propose no extraction over a quarter of the project

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/seams.md` (`seams`, method steps 2–3 and details)

## Context

`seams --project src/NHibernate/NHibernate.csproj` on NHibernate 4.1.2 tainted 1,445 of 2,355
types (1,470 before ADR 0044) and proposed moving all of them to `NHibernate.Windows`; its 13
seams had nothing to do with the unportable APIs. The spec was inconsistent:

- Step 2 says calls do not taint, "that is where a seam can go", but step 3 took the strongly
  connected components of the whole reference graph, calls included, and tainted every
  component with a tainted type. Seven directly tainted types sat in one component of 979
  types, the core of the ORM, joined by calls.
- Three of the seven (`Cfg.Configuration`, `Cfg.Environment`, `ConnectionProvider`) were
  unportable only until `System.Configuration.ConfigurationManager` is referenced, which the
  `OFR3001` finding itself names.
- Nothing bounded the extraction: a proposal to move most of a project is a split, not a seam.
- Each cycle-tainted type's reason listed every tainted member of its component: one reason
  was 42,036 characters, repeated for 286 types, and the JSON was 14 MB.

## Decision

- **Structural cycles.** The components are those of the structural graph: `A → B` when `B` is
  a base type or interface of `A`, or appears (as the type or a type argument or element type)
  in a non-private field, property, indexer parameter, event, or method parameter or return of
  `A`. Constructors are left out, as in step 2's exposure rule, because a constructor parameter
  is where `extract interface` puts the interface. Calls, bodies, and private members make no
  structural edge, so they stay places to cut. The type reference graph (all references) is
  still what the minimum cut and the graph views use.
- **Fixed point.** Exposure, inheritance, and structural cycles are applied until nothing
  changes, so a type that inherits from a type tainted by its cycle is tainted too.
- **Package-supplied APIs.** An `OFR3001` finding whose mapping (`details.mapping`) is `package`
  or `compat-pack` is supplied on the target by that package and does not taint, unless the
  package is Windows-only (`details.windowsOnly`) and the target is not `-windows`. Each such
  package is reported once as `OFR4032` (info) with the types that use it. Only the mapping
  decides: `builtin` ("the assembly is on the target, but not this API"), `none`, and unknown
  assemblies still taint, and the removed-technology rules (`OFR3004`–`OFR3009`, `OFR3013`)
  always do.
- **A cycle is named once.** Partitions are the structural components with a tainted type,
  numbered from 1 in the order of their first type's name. A type tainted by its cycle has the
  reason `in a structural cycle with a tainted type (partition N)`, and `partitions[N-1].types`
  lists the cycle.
- **Extraction share.** When more than a quarter of the project's types, and more than 10, are
  tainted, `extraction` is null and `OFR4031` (warning) says how many types the extraction would
  take and lists the directly tainted types (those with a reason of their own: an unportable
  symbol or interop), in its message (the first 20) and in `data.directlyTainted` (all). The
  taint, partitions, and seams are still reported.

## Alternatives considered

- Dropping step 3: a structural cycle across the boundary cannot compile on either side of it,
  so its types do have to move together.
- Deciding "a package supplies it" by compiling against the package: the finding already names
  the package from `rules/framework-assemblies.yml`, and the removed-technology rules keep what
  the packages leave out (WCF hosting is `OFR3006`, not `OFR3001`). A package that lacks one API
  of its assembly shows up when the project is built against it.
- A share alone as the limit: in a project of 7 types (the `seams` fixture), moving 3 is a
  seam-sized extraction, so extractions of at most 10 types are always proposed.
- Proposing an extraction of the directly tainted types only: it would not compile, since
  their subclasses and exposers stay behind; fencing them in place is the advice.

## Consequences

On NHibernate 4.1.2, `seams` taints 11 of 2,355 types (the SqlClient, ODBC, OLE DB, CodeDom,
`CallContext`, `SecurityManager`, and `AppDomain.DefineDynamicAssembly` users), proposes 7 seams
and an extraction of those 11, reports `System.Configuration.ConfigurationManager` and
`System.ServiceModel.Http` as `OFR4032`, and writes 2.7 MB of JSON instead of 14.6 MB. With the
configuration APIs listed as unportable (`--unportable-from list`), the taint still reaches
1,173 types through exposure and inheritance (`Configuration` is in many public signatures);
`OFR4031` then names the 6 types to fence instead of proposing to move half the project.
Signatures now matter more than calls: a clean type that exposes a tainted one in a public
signature is still tainted, as step 2 says.
