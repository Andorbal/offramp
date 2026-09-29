# 0044. Report an API once, under the rule that says what to do about it

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/audit.md` (`audit api`, rules and packs, `ifdef wrap`)

## Context

The NHibernate 4.1 field test found `audit api` findings that were true but unhelpful:

- `CallContext.SetData` was reported twice at one place: `OFR3001` ("does not exist on the
  target") and `OFR3007` (.NET Remoting, "use gRPC, HTTP, or named pipes"). The type lives
  in `System.Runtime.Remoting.Messaging`, but it is an ambient context, and `AsyncLocal<T>`
  replaces it.
- 23 of 24 `OFR3009` errors ("isolate the code in a separate process") were
  `[SecurityCritical]` and `[AllowPartiallyTrustedCallers]`. Both exist on the target and do
  nothing there.
- `AppDomain.DefineDynamicAssembly` got the generic "see the assembly's mapping" although
  `AssemblyBuilder.DefineDynamicAssembly` replaces it.

SmartStoreNET 4.2 added 133 extension methods on System.Web types attributed to the
solution's own assemblies ("`HttpRequestBase.IsHttps()` (SmartStore.Core) does not exist"),
and NHibernate a project whose build failed audited without a word.

## Decision

- Where a removed-technology rule (category `removed-technology`) matched, `OFR3001` is not
  reported: at the same name, or on the same line for the same symbol. The removed
  technology's finding carries the direction; "does not exist" adds nothing.
- `CallContext`, `LogicalCallContext`, and `ILogicalThreadAffinative` are their own
  removed-technology rule, `OFR3013`, recommending `AsyncLocal<T>`. A rule's new `exclude`
  list keeps them out of `OFR3007`.
- Security transparency attributes (`SecurityCritical`, `SecuritySafeCritical`,
  `SecurityTransparent`, `SecurityTreatAsSafe`, `AllowPartiallyTrustedCallers`,
  `SecurityRules`) are `OFR3014`, info: "no effect; can stay". `OFR3009` keeps the Code
  Access Security attributes, `PermissionSet`, and sandboxed domains. `ifdef wrap` skips
  info findings, so it never wraps code that works on the target.
- `OFR3001` has a `replacements` map in `rules/audit-api.yml`, by documentation ID; a
  finding whose API has one names it in its message and in `details.replacement`.
- An extension method declared elsewhere whose receiver type the target lacks is attributed
  to the receiver's assembly and namespace, with `details.extensionAssembly`.
- A project the model marks partial is still audited when it has a compiler call, with
  `OFR3016` (warning); without one, `OFR3012` names the failed build instead of asking for a
  scan the user just ran.

## Alternatives considered

- A "most specific rule wins" order among symbol rules instead of `exclude`: it would have
  changed which rule reports overlapping patterns elsewhere (`AppDomain.CreateDomain` is both
  `OFR3003` and, with a permission set, `OFR3009`), and made the rule files harder to read.
- Dropping `[SecurityCritical]` from the audit entirely: users porting libraries written for
  partial trust ask whether those attributes matter; an info finding answers it.
- Skipping a partial project: the failed call usually lacks one generated file, and the rest
  of the project is worth auditing, with a warning.

## Consequences

The Web Forms, ASMX, WCF, Remoting, WF, and COM+ APIs a project uses show up under their
technology's rule and no longer also as `OFR3001`, so `OFR3001` counts drop on such projects
while `seams` and the porting ledger, which count both, see the same code as unportable.
Disabling a removed-technology rule (or its pack) brings the `OFR3001` findings back.
