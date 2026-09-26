# 0021. Audits compile against the SDK's reference packs; findings are data, one diagnostic per rule and project; `ifdef` edits whole lines

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/audit.md` (audit api, behavior, serialization, native, ifdef)

## Context

The spec describes what each audit finds but leaves these open:
- where `audit api` gets the target's reference assemblies, and which packages
  "support the target"
- what a finding is in the envelope: every finding a diagnostic, or none
- which projects the target compilation covers, and what happens to references
  between projects
- how a missing namespace (`System.Web.UI`), a using directive, or an attribute is
  reported
- how far the serialization "data-flow heuristics" go, and what counts as serialized
  for OFR3205
- where `ifdef wrap` puts directives, which statements it may wrap, and what
  "required by callers on both targets" means
- what `ifdef strip` does with conditions that name other symbols too

## Decision

- **The SDK supplies the target.**
  - `audit api` asks MSBuild for the references of a scratch project under
    `.offramp/cache/targets/`, with the target framework, the shared frameworks the
    project kind needs, and the project's direct packages at their resolved versions.
    The command is `dotnet msbuild -restore -getItem:ReferencePathWithRefAssemblies`.
  - The SDK picks the reference packs and NuGet picks package assets. Nothing is
    guessed from folder names.
  - With implicit asset target fallback off, a package that does not support the
    target fails restore with NU1202. It is dropped, with the direct packages whose
    closure (from the assets file NuGet writes even on failure) reaches it, and the
    restore runs again (OFR3011).
  - Any other failure leaves the project without a target compilation (OFR3010).
  - Results are cached by request and reused while their files exist.
- **Only `framework`-class projects are compiled against the target.** Dual projects
  already build for it. The recorded sources, options, and language version stay;
  the preprocessor symbols switch to the target's. Referenced framework projects are
  referenced as their own target compilations, so one project's missing API is not
  reported again in its callers.
- **What a missing API is.**
  - It is CS0234, CS0246, CS0103, CS1061, CS0117, or CS1069 at a position that binds,
    in the recorded compilation, to a type or member from metadata. CS1069 is in the
    list because the target's `System.Drawing` facade forwards `Bitmap` to an assembly
    it does not reference.
  - A namespace is never a finding. When the error is on a namespace segment, the
    first type or member to its right is.
  - An attribute or constructor is reported as its type.
- **OFR3002** comes from `[SupportedOSPlatform("windows")]` on the symbol, a
  containing type, or the assembly. Guards are not analyzed: .NET Framework code
  cannot contain `OperatingSystem.IsWindows()`. Desktop projects compile for
  `-windows` and get none.
- **Findings are the result; diagnostics summarize.**
  - Every finding is in `result.findings`.
  - Each rule with findings in a project is one diagnostic, at the first finding,
    with the count. `--fail-on` works on audits without an envelope of thousands of
    entries.
  - Severity overrides apply to both, and `none` removes the rule from the run.
- **Rules are data.**
  - Packs are YAML. Symbol rules match documentation IDs through the semantic model,
    never names in text.
  - Named matchers cover what identity cannot:
    - culture-sensitive overloads
    - constant code pages, paths, and time zone IDs
    - `ToString` without a format
    - regular expressions without a timeout
    - delegate `BeginInvoke`
    - `app.config` runtime settings
    - serialization flow
    - P/Invoke declarations and COM references
  - Every rule has a class in the `behavior` fixture whose `Positive` members must
    match and whose `Negative` members must not.
- **Serialization classification is conservative.**
  - Transient means one member, a local `MemoryStream` created empty, both directions
    through it, and no other use. Everything else, including what cannot be traced,
    counts as persisted.
  - Carried types follow `object` parameters to call sites in the same compilation,
    three levels deep.
  - OFR3205 is decided after every project has been read. A type counts as reached
    through base types and serialized fields.
- **`--pack` overrides `rules.packs.disable`**: naming a pack on the command line is
  an explicit request.
- **`ifdef wrap` only inserts lines.**
  - `#if CONDITION` and `#endif` go at the start of the line, where `dotnet format` and
    Visual Studio put directives.
  - A statement is wrapped only when removing it cannot break the method: it has lines
    of its own, it has no exit of its own, and it declares no local used later.
    Otherwise the member (or type) is wrapped.
  - "Required by callers on both targets" means any of:
    - used, in any project, from code outside the ranges being wrapped and outside
      regions with the same condition (matched by documentation ID, so it works across
      compilations)
    - an override, abstract, or an interface implementation
  - Such a member is OFR3601.
  - Stale findings (the file changed since the audit) are OFR3602, not guessed at.
- **`ifdef strip` decides a chain only when the one symbol decides it** (three-valued
  evaluation). Anything that still depends on another symbol stays (OFR3603), because
  removing a branch that might compile somewhere is not reversible by reading the
  code.
- **New codes:** OFR3010, OFR3011, OFR3602, OFR3603, OFR3604 (unreadable findings
  file). The reserved ranges OFR3001–3009, 3101–3120, 3201–3211, 3301–3320, and 3601
  move to the catalog.

## Alternatives considered

- Reading reference assemblies from `dotnet/packs` directly: breaks for targets older
  than the SDK, whose packs come from NuGet, and for packages.
- Every finding as a diagnostic: the envelope would duplicate the result, often
  thousands of entries, and the human view would list every location twice.
- Wrapping only statements, or only members: statements alone break methods that
  return; members alone wrap far more than needed.
- Evaluating `strip` conditions with every unknown symbol undefined: silently wrong
  for `DEBUG` and custom symbols.

## Consequences

- `audit api` needs the target's reference packs, which `dotnet restore` fetches from
  the configured feeds the first time (then from the cache).
- A package without target support shows up as OFR3001 findings on its APIs, which is
  what a porter has to deal with, and OFR3011 names it.
- Deferred:
  - ledger trending of `ifdef report` counts
  - `bool`, `LPStruct`, and `SafeHandle` marshalling checks in OFR3302
  - `dynamic` over COM objects in OFR3310
  - following OFR3204 call sites into other projects
