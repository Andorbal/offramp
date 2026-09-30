# tests-in-prod

- `Foo` (`net48`) ships its xunit tests next to the code:
  - `Service/Tests/OrderServiceTests.cs`: tests, which call the internal `OrderService.Discount`.
  - `TestData/Builders.cs`: `OrderBuilder`, internal and used only by the tests (a helper).
  - `Shared/Clock.cs`: `SystemClock`, used by production code and by the tests (stays).
  - `Testing/FakeClock.cs`: referenced from nowhere; its name and folder look like
    test support, but it is public and no application uses `Foo`, so it may be someone's
    API: a `low` candidate that never moves (ADR 0045).
  - `Health/StartupChecks.cs`: a test class that production code (`Service/Startup.cs`)
    calls, so it stays (`OFR2201`).
  - `Web/Tests/UrlTests.cs`: needs `System.Web`, which `Foo.Tests` does not reference, so
    it does not compile in the destination (`OFR2103`).
  - `../Common/SharedTests.cs`: a test file linked from outside the project's folder
    (`OFR2206`).
- `Foo.Tests` exists but holds one smoke test.
- `Bar` (`net48`) has NUnit tests in `CalculatorTests.cs` and no `Bar.Tests`; once they
  move, `NUnit` is prunable (`OFR2210`).

Because `Foo` and `Bar` reference test frameworks, the model classifies them as
`test` projects (docs/spec/02-workspace-model.md); moving their tests out and
pruning the packages is what turns them back into libraries.

Exercised by: `move tests` (detection, helper fixpoint, target selection,
`--create`, path mapping, `InternalsVisibleTo`, `--prune-packages`), `graph`.
