# 0038. Recognize test projects from a test framework's assembly reference

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/02-workspace-model.md` (project kind detection)

## Context

Kind detection recognized a test project from `IsTestProject`, a test framework package
(`PackageReference` or `packages.config`), or the legacy test ProjectTypeGuid. Before NuGet,
test frameworks were DLLs checked into the repository and referenced by `HintPath`. NHibernate
4.1's test projects reference `lib/net/nunit.framework.dll` that way, so all three were
`library`: `audit dead-code` treated 4,786 tests as production code, and `guide` asked the user to
move the tests out of the test projects. The guide already had a list of test framework
assemblies; the model did not use it.

## Decision

A `Reference` to a test framework's assembly (`nunit.framework`, `xunit`, `xunit.core`,
`xunit.v3.core`, `MbUnit.Framework`, `TUnit.Core`, and the MSTest assemblies) makes an
`OutputType=Library` project a `test` project. The rule is evaluated after the `web` rules, not
with the package rules at the top: a web application project is a library too, and one that
references a test framework (tests kept in the application) is still the application. An `Exe`
that references one stays what its other evidence says. The guide uses the same list.

## Alternatives considered

- Evaluate it first, like the package rules. It would turn a web application with tests inside it
  into a test project, and that application would vanish from `plan` and `report`.
- Look for `[Test]` attributes in the sources. Kind detection runs from the build log, before any
  compilation is loaded, and a reference is as strong a signal as a package.

## Consequences

The guide's "Move test code out of production projects" step no longer lists libraries that
reference a test framework by `HintPath`: they are test projects now. A library that carries its
own tests next to production code is classified `test`, as it already was when the framework came
from a package; `offramp.yml`'s `projects: - kind:` overrides it.
