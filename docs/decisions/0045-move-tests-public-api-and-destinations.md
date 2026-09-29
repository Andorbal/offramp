# 0045. `move tests` leaves a shipped library's public API alone and names test support only by non-public types

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/move.md#move-tests`

## Context

`move tests` classifies a helper from usage inside the solution (used only by tests and other
helpers) and from evidence such as a type name containing `Builder`, `Fake`, or `Stub`. It had no
notion of a public surface consumed outside the solution. On NHibernate 4.1 it planned to move
`QueryOverBuilderExtensions`, public QueryOver API that only the repository's tests call, into the
test project at `high` confidence: the build and every test would have passed, and the next NuGet
package would have lost the API. The same name rule called DotNetNuke's
`LocalizationExpressionBuilder` (an ASP.NET expression builder registered in `web.config`) and
SmartStoreNET's `LinqContainsPredicateBuilder` test support.

The spec did not say whether a public type can be test support, nor what "name contains" means.

## Decision

- **A shipped library's public API never moves.** When the source project is shipped (ADR 0041:
  listed in `deadCode.externalConsumers`, packable, packed from a `.nuspec`, or a library no
  application depends on), a non-test file that declares a public type (public all the way out)
  is never a helper, and it takes no part in the helper fixpoint, so what it uses stays too. If it
  has test-support evidence and no production user, it is a `low` candidate whose first reason
  says why it stays; `--include-helpers low` does not move it. Test files still move: moving tests
  is the command's purpose.
- **A name is evidence only for a type that is not API.** The hint must be a whole word of the
  type's name (followed by the end, an upper-case letter, a digit, or a plural `s`), and the type
  must be non-public or live in a test namespace (a segment from the test-folder list, or one
  ending in `Tests`).

## Alternatives considered

- **Keep the name rule and only add the shipped rule.** A public `…Builder` in an application's
  library is as likely to be product code; the name alone should not make it a helper there
  either. Folder, namespace, and test-framework usage remain evidence.
- **Let `--include-helpers low` move shipped public API.** It would make the dangerous move one
  flag away and silent in the plan; `move plan` exists for moving a type deliberately.

## Consequences

- On NHibernate, the plan moves nothing instead of `QueryOverBuilderExtensions`, and
  `WhereBuilder` is no longer a candidate.
- In a library that ships, public helpers used only by tests are listed, not moved; making them
  internal (with `InternalsVisibleTo` for the test project) lets `move tests` take them.
