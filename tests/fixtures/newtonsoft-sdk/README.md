# newtonsoft-sdk fixture

SDK-style projects on Newtonsoft.Json **7.0.1**, targeting `net48;net10.0`, with test projects
that run on every OS. It exercises `newtonsoft scan`, `newtonsoft tests` (generation, recording,
verification), and `newtonsoft upgrade` for PackageReference projects (`docs/spec/commands/newtonsoft.md`).

The test projects use `Microsoft.NET.Test.Sdk` 18.x and reference `System.Security.Permissions`
on `net10.0`: 17.x's test host brings Newtonsoft.Json 13 to .NET targets, and 7.0.1's `net45`
assembly needs the permissions assembly to load on .NET (ADR 0067). Both are checked by the
fixture build: the test projects resolve 7.0.1 on both targets.

| File | Case |
|---|---|
| `src/Catalog/CatalogCodec.cs` | settings in a static member (camel-case resolver, `StringEnumConverter { AllowIntegerValues = false }`); serialize and deserialize sites that are their own entry points |
| `src/Catalog/Product.cs` | payload members for the behavior rules: an all-caps prefix (`SKUCode`), `decimal`, `DateTime`, `Uri`, a dictionary keyed by an enum with `[EnumMember]` |
| `src/Catalog/ProductExporter.cs` | inline settings in a private helper; the entry point is one caller up |
| `src/Catalog/Envelope.cs` | an `object` payload whose concrete type (`Product`) comes from a caller |
| `src/Catalog/Category.cs` | a type that contains itself, read without `MaxDepth` |
| `src/Catalog/PriceReader.cs` | `catch (InvalidCastException)` around `JToken.ToObject` |
| `src/Catalog/Money.cs` | `ISerializable` without `[Serializable]` |
| `src/Catalog/ProductStore.cs` | a site behind `File.WriteAllText`: not hermetic, and `void` |
| `src/Catalog/JsonDefaults.cs` | a `JsonConvert.DefaultSettings` assignment |
| `src/Inventory/StockSnapshot.cs` | a writer whose constructor takes an interface (not constructible); a reader of the same type |
| `src/Inventory/Suppliers.cs` | a type that is only ever deserialized (needs a real payload); an internal site without a friend assembly (not accessible) |
| `tests/Catalog.Tests` | xunit, `InternalsVisibleTo` from `Catalog`, references Newtonsoft.Json itself (type probes allowed) |
| `tests/Inventory.Tests` | NUnit, Newtonsoft.Json only through `Inventory` |
