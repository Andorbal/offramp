# 0074. A project an application uses is not a test project by its framework alone

- Status: accepted
- Date: 2026-10-15
- Spec section: `docs/spec/02-workspace-model.md` (project kind detection)

## Context

Kind detection made any project with a test framework package (`PackageReference NUnit`, or the
same in `packages.config`) a `test` project, before every other rule. ADR 0038 kept that order for
packages and moved only the checked-in assembly rule after `web`, accepting that "a library that
carries its own tests … is classified `test`".

In a legacy solution of 82 projects (issue #52) the eight largest production projects carried
NUnit: shared test helpers, a framework added through common props, tests kept next to the code.
All eight were `test`. Test projects are left out of the call graph, so `newtonsoft scan` reported
96% of the solution's 1,744 sites as `notAccessible`, "the site's member is not declared in
source", and `newtonsoft tests` generated nothing for them. The same classification removes a
project from dead-code analysis, `report`, `plan`, and the target choice. Overriding the kind in
`offramp.yml` worked, but not where `IsTestProject=true` came from shared props: the model kept the
raw `IsTestProject`, and every reader asks `IsTestProject || kind == test`.

## Decision

- **A test framework is evidence after the `web` rules**, packages as well as assemblies:
  `IsTestProject=true` and the test ProjectTypeGuid stay first. A web application that references
  NUnit is `web`, and an executable (`Exe`, `WinExe`) with a service's, Windows Forms', or WPF's
  evidence is that application. A WPF or Windows Forms *library* with a framework stays `test`.
- **What an application uses is not a test project.** After every project is classified, a project
  that is `test` only by a framework it references (a package, `packages.config`, or an assembly),
  and that an application (a `web`, `service`, `winforms`, or `wpf` project that is not a test
  project) references, directly or through other projects, is classified again without that
  evidence (usually `library`): it ships with the application. The evidence keeps the framework
  and names the application:
  `OutputType=Library (PackageReference NUnit ignored: the application src/Web/Web.csproj uses it)`.
  A library or console application that references a test project proves nothing: a library can
  get its framework through the reference and be a test project nothing marks
  (`dead-code-evidence`'s `Specs`), and a console application can be a test runner (NUnitLite).
- **What says so in so many words is kept, and named.** A project with `IsTestProject=true` or the
  test ProjectTypeGuid that an application uses stays `test`, and `OFR0107` (warning) names the
  application and says to set the kind in `offramp.yml`.
- **An override decides for every reader.** `kind:` in `offramp.yml` also sets `isTestProject`
  (true for `test`, false otherwise).
- **A site in a test project says why it has no entry point**: `notAccessible`, "<project> is a
  test project (<evidence>), and test code is not searched for entry points". The finder's fallback
  for a member outside the call graph no longer claims it is not declared in source.
- **A project is not its own test project.** `newtonsoft.tests.projects` or `--to` naming the
  production project itself is `OFR3733` (error): probes in production code would ship with it.

## Alternatives considered

- **Require `IsTestProject` or `Microsoft.NET.Test.Sdk`.** Legacy test projects have neither:
  NHibernate's and MassTransit's are test projects by their framework alone (ADR 0038).
- **Use the project's name (`*.Tests`).** A convention, not evidence; many solutions do not follow
  it, and it would make `Orders.Testing` (a production library of test doubles) a test project.
- **Only report, never reclassify.** It would leave the default wrong for the common case, and a
  user with 82 projects would have to write eight overrides before any command was useful.
- **Count every production project as a referencer.** A library that references a test kit gets
  its framework through it (`dead-code-evidence`'s `Specs`): it is a test project nothing marks,
  and the kit would become a library. A console application that references test projects is a
  test runner. Only an application certainly ships what it references.

## Consequences

`tests-in-prod`'s `Foo` and `Bar` stay `test` (only their own test project references `Foo`). A
library an application uses now gets entry points, probes, dead-code analysis, and a
place in `report` and `plan`, and `guide` offers to move the tests it carries (`move tests`). A
library that carries a test framework and that only libraries, console applications, or nothing
reference stays `test` without a diagnostic; `offramp.yml` overrides it. Kind detection now needs
the project graph, so it is a pass over all projects after each is built.
