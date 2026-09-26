# dead-code

A `net48` solution with one example of each confidence level for `audit dead-code`.

- `Core` (`IsPackable=false`, no `InternalsVisibleTo`):
  - high: `LegacyExporter` (public, unused), `InternalCache` (internal, unused),
    `OrderService.Archive` (an unused member of a used type)
  - low: `ReportPlugin` (named only in a `Type.GetType` string), `InvoiceHandler`
    (implements `IHandler`, and `App` calls a `Scan` registration), `Snapshot`
    (`[Serializable]`)
  - used only by tests: `FixedClock` (OFR3402 with `--include-tests`)
- `Contracts` (packable by default): medium: `LegacyDto` (public in a packable library).
- `App`: console application; low: `Program` (entry point).
- `Core.Tests` (`IsTestProject=true`): uses `OrderService` and `FixedClock`.

Lines removable at high confidence: 28 (`Archive` 5, `LegacyExporter` 14,
`InternalCache` 9).
