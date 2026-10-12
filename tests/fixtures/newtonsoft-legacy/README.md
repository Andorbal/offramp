# newtonsoft-legacy fixture

Legacy (non-SDK) projects on Newtonsoft.Json **7.0.1** through `packages.config` and a `HintPath`,
as most codebases that still use it are. It exercises `newtonsoft scan`, `newtonsoft tests`
(generation into a legacy MSTest project at C# 6), and `newtonsoft upgrade`'s `packages.config`,
`Reference` identity, `HintPath`, and binding redirect edits (`docs/spec/commands/newtonsoft.md`).

The projects build on any OS like `legacy-csproj`: `Directory.Build.props` gives them the
.NET Framework reference assemblies as a package, and the tests fill `packages/` the way
`nuget restore` would (`PackagesConfigRestore`). Their tests run on Windows only (ADR 0067).

| File | Case |
|---|---|
| `src/Orders/OrderCodec.cs` | settings in a static member with a camel-case resolver and `TypeNameAssemblyFormat` (obsolete from 10.0.1) |
| `src/Orders/Order.cs` | the payload: `decimal`, `DateTime`, an enum, a list of lines |
| `src/Orders/Comment.cs` | a recursive type that is only ever deserialized |
| `src/Orders.App/App.config` | a binding redirect to 7.0.0.0 that the upgrade must move |
| `tests/Orders.Tests` | legacy MSTest (v2 through `packages.config`, adapter imports), `LangVersion` 6, references Newtonsoft.Json itself |
| `samples/Contoso.Orders.Order.json` | a real payload for `Order` |
| `samples/Contoso.Orders.Comment.deep.json` | a thread 71 levels deep: past 13.x's default `MaxDepth` of 64 |
| `samples/Contoso.Orders.Invoice.json` | a payload whose type does not exist (OFR3737) |
