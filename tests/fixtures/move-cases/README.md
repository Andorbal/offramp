# move-cases

`Legacy` (`net48`) holds one file per case of `move plan --from Legacy --to Core`
(`Core` is `netstandard2.0`):

| Case | File | Expected |
|---|---|---|
| (a) moves cleanly | `Clean/Money.cs` | moved |
| (b) needs a package the destination lacks | `Json/Serializer.cs` | moved; `Core` gets `Newtonsoft.Json` |
| (c) needs a project reference | `Orders/OrderMapper.cs` | moved; `Core` references `Contracts` |
| (d) would create a cycle | `Cycle/Reporter.cs` (uses `Reports`, which references `Core`) | excluded, `OFR2001` |
| (e) unportable | `Web/LinkBuilder.cs` (`System.Web`) | excluded, `OFR2103` |
| (f) partial class across two files | `Partial/Invoice.cs` + `Invoice.Totals.cs` | moved together, `OFR2110` |
| (g) resx + Designer pair | `Resources/Strings.resx` + `Strings.Designer.cs` | moved together; the resource keeps its manifest name |
| (h) internals used by code that stays | `Internal/Rounding.cs` (used by `Internal/Billing.cs`) | moved; `Core` grants `InternalsVisibleTo` to `Legacy` |

`Legacy` references `Core` after the move (the code that stays uses what moved).

More cases, each with its own `move plan` in the tests:

| Case | Move | Expected |
|---|---|---|
| .NET Framework-only package | `Data/ShopContext.cs` (EntityFramework 6.2) to `Core` | excluded, `OFR2102` |
| destination above the source | `src/Core/Existing/Slug.cs` to `Reports` (which references `Core`); `Paths.cs` stays and uses it | excluded, `OFR2104` |
| Windows-only API | `Platform/RegistryReader.cs` to `Modern` (`net10.0`) | moved, `OFR2105` |
| destination removes the path | `Generated/Stamp.cs` to `Modern` (`Compile Remove="Generated\**"`) | excluded, `OFR2111` |
