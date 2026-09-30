# dead-code-evidence

A `net48` solution in the shape of the field-tested codebases, for the evidence `audit dead-code`
weighs. No project sets `IsPackable` (they say `false`, as legacy projects never say `true`), so
whether a library ships is decided by the rules of ADR 0041.

- `Shop`: a console application; it uses `Engine`, `Client`, and `Tools`.
  - `BoardsController` (deriving from a stand-in `Controller`, which the analysis recognizes by
    name): `SetSellerNote`, linked from nowhere, is `medium` (MVC reaches actions from URLs);
    `ActiveDiscussionsRss`, named in `Program.cs` as `"ActiveDiscussionsRSS"`, is `low`.
- `Engine`: a library the application uses, with SmartStoreNET's ways of finding types.
  - `TypeFinder.cs`: `ITypeFinder.FindClassesOfType(Type)`, implemented with
    `assignTypeFrom.IsAssignableFrom(t)`, and the extension `FindClassesOfType<T>()` that passes it
    `typeof(T)`. `Bootstrapper` calls `FindClassesOfType<IStartupTask>()` and
    `FindClassesOfType(typeof(IMapper<,>))`, so `CacheWarmupTask` and `OrderMapper` are `low`.
  - `Mapping.cs`: `ModelBuilder` finds `EntityMap<T>` subclasses with
    `let definition = t.BaseType.GetGenericTypeDefinition() where definition == typeof(EntityMap<>)`,
    so `ProductMap` is `low`; `OrderList : List<int>` is `high`, although `IsList` compares with
    `typeof(List<>)`.
  - `Orphan`, and `UnusedTask` (it implements an interface nothing looks for): `high`.
  - `MapBridge` is `[ComVisible(true)]`, the page's `window.external`, as Open Live Writer's
    `JSMapController` is: `NextEvent` is called from `map.html`, `JsUpdateBirdsEye` from
    `scripts/map.js`, and `SetCenter` from nowhere; all three are `low`. `PlainBridge.Ping` is
    named only in `scripts/vendor.min.js`, which is not read: `high`.
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
  - `FormatSpecs`, with `[Test]` methods and no `[TestFixture]`: `low`; `SpecNotes`, whose method carries
    an attribute that is not a test framework's: `medium`.
