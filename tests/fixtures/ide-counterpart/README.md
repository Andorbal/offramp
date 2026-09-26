# ide-counterpart

The editor integration (`docs/spec/commands/ide.md`): `Foo` (`net48`) references its
portable counterpart `ModernF` (`netstandard2.0`) and another portable project, `Shared`.
`Legacy` (`net48`) references nothing portable. The tests commit the fixture, then add and
change files to make new code.

| File | Move to `ModernF` |
|---|---|
| `src/Foo/Pricing/PriceCalculator.cs` | movable: the base class library only |
| `src/Foo/Formatting/Names.cs` | movable: two types, the file moves whole |
| `src/Foo/Orders/OrderService.cs` | `OFR2101`: uses `PriceCalculator`, which stays |
| `src/Foo/Web/CookieReader.cs` | `OFR2103`: `System.Web` (and `OFR3001` when it is new code) |
| `src/Foo/Json/Payload.cs` | `OFR6003`: `ModernF` lacks `Newtonsoft.Json` |
| `src/Foo/Text/Casing.cs` | `OFR6003`: `src/ModernF/Text/Casing.cs` exists |

Without a project map, `Foo`'s counterparts are `ModernF` and `Shared` (the portable projects
it references). `Legacy` has none (`OFR6005`) until a map entry gives it one; with
`Legacy → ModernF`, moving `Greeter.cs` adds a reference from `Legacy` to `ModernF`, because
`Welcome.cs` stays and uses it.
