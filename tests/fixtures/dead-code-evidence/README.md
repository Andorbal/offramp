# dead-code-evidence

A `net48` solution in the shape of the field-tested codebases, for the evidence `audit dead-code`
weighs. No project sets `IsPackable` (they say `false`, as legacy projects never say `true`), so
whether a library ships is decided by the rules of ADR 0041.

- `Shop`: a console application; it uses `Engine`, `Client`, and `Tools`.
- `Engine`: a library the application uses.
  - `Orphan`: public, named by nothing: `high`.
- `Client`: `Client.nuspec.template` sits beside the project, as NHibernate's does: shipped.
  - `ShopClient.Reset`: public, called by nothing: `medium`.
- `Tools` (`Evidence.Tools.dll`): `build/Evidence.Sdk.nuspec` packs its DLL from the build output, as
  Open Live Writer's SDK package does: shipped.
  - `Checksum.OfBytes`: `medium`.
- `Formats`: a library only `TestKit` (a test project, by its NUnit reference) uses: shipped.
  - `CsvFormat.Quote`: `medium`; `CsvFormat.Unused` (internal): `high`.
- `TestKit`: NUnit assertions over `Formats`.
- `Specs`: a library that gets NUnit through `TestKit`, so nothing marks it a test project, and that
  nothing references: shipped.
  - `FormatSpecs`: `medium`.
