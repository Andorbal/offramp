# 0022. Dead code counts every bound name solution-wide, and doubt lowers confidence; ApiCompat compares fresh builds in strict mode

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/audit.md#audit-dead-code-ofr3400-3499`, `#audit-api-compat-ofr3500-3599`

## Context

The spec asks `audit dead-code` for `FindReferencesAsync` across all compilations, with
three confidence levels, and `audit api-compat` for a wrapper around ApiCompat. It leaves
these open:
- how references are found without a Roslyn `Solution` (the model holds recorded
  compilations, not MSBuild projects)
- which members can be dead at all
- what "packed" means for SDK-style projects, where `IsPackable` defaults to true
- how a "DI convention pattern" is recognized without the containers' packages
- how lines are counted
- how ApiCompat is obtained, which version, where the builds go, and how differences
  in both directions are found

## Decision

- **One reference index for the solution.**
  - Every project's recorded compilation is walked once. Every bound name, plus the
    members the compiler calls for `foreach`, `await`, collection initializers,
    deconstruction, and query clauses, is recorded under its documentation ID, so uses
    from other projects match.
  - A member's use also counts for the types containing it, because extension methods
    are called without naming their class.
  - Uses inside a symbol's own declaration do not count.
  - This gives the same answer as `FindReferencesAsync` without building a `Solution`.
- **What can be dead.** Only code a person could delete without breaking the build. Not
  candidates:
  - overrides, abstract and virtual members
  - interface members and implementations
  - constructors, operators, and conversions
  - enum members
  - members of an unused type (the type is reported once)
- **Confidence starts from visibility and lowers on doubt.**
  - `high`: private and internal code (unless `InternalsVisibleTo` names an assembly
    outside the solution), and public code in assemblies that are not packed.
  - `medium`: public code in a packable project (`IsPackable` as evaluated, so SDK-style
    libraries count unless they opt out) or in one listed in
    `deadCode.externalConsumers`.
  - `low` whatever the base level, for anything static analysis cannot rule out:
    - the name in a string literal or a resource or configuration file
    - a convention registration call in the solution plus an implemented solution
      interface
    - controllers, hubs, and handler interfaces
    - serialization and reflection-driven attributes
    - entry points
    - public properties and fields, which serializers, ORMs, and binding reach by
      reflection
  - Conventions are recognized by the called method's name (`Scan`,
    `RegisterAssemblyTypes`, `AddMediatR`, ...) through the semantic model, which works
    without the container packages and errs toward `low`.
- **Lines** run from a declaration's documentation comment to its last line.
  Recorded compilations of projects without documentation generation parse `///` as plain
  comments, so those count too.
- **Test projects** are never scanned for candidates, and their uses count. With
  `--include-tests`, code only tests use is listed separately (OFR3402) rather than
  hidden.
- **ApiCompat is Microsoft's tool, at the SDK's version.** It is installed with
  `dotnet tool install --tool-path .offramp/tools/apicompat/<version>` (the newest release
  when the SDK's version is not published). Strict mode reports additions and removals
  in one run.
- **Fresh builds, in a folder of their own.** Each side is built with
  `-p:OutDir=.offramp/cache/api-compat/...`, so the user's `bin/` is untouched, and the
  folder is removed afterwards. A baseline builds in a scratch work tree at the revision.
  The user's `obj/` changes as with any build.
- **New codes:** OFR3503 (nothing to compare: usage) and OFR3504 (a side did not build,
  the baseline could not be checked out, or the tool could not be installed:
  environment).

## Alternatives considered

- `SymbolFinder.FindReferencesAsync` over an `AdhocWorkspace` rebuilt from the compiler
  log: the same answers, much slower on large solutions.
- Treating only `IsPackable=true` written in the project file as packable: SDK-style
  libraries are packable by default, and assuming nobody consumes them would put public
  APIs at `high`.
- Detecting DI conventions from package references (Scrutor, Autofac): misses
  hand-written scanning code and vendored containers.
- Comparing the assemblies already in `bin/`: they may be stale or from another
  configuration. Building both sides is the verification.

## Consequences

- Large solutions pay one pass over every name for dead code. The index is
  proportional to the code, not to the number of candidates.
- `audit api-compat` needs the NuGet feed that hosts the tool the first time.
- Reflection that uses computed names (`Type.GetType(prefix + name)`) is not seen. Such
  code shows up at the confidence its visibility gives, which is why public code in
  packable libraries is at most `medium`.
