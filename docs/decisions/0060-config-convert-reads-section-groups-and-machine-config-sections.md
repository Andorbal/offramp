# 0060. config convert reads section groups, and knows the sections machine.config declares

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/scaffold.md#config-convert`

## Context

The spec says that "any other section not declared in `configSections` ... is `OFR4401`".
On SmartStoreNET's `Web.config` (`docs/field-tests/2026-09-smartstorenet-4.2.0.md`, P2),
`config convert` said "bundleTransformer is left out: it is not declared in
configSections": `bundleTransformer` is a `<sectionGroup>`, whose sections are elements
inside the group's element, and the converter keyed the declared sections by their own
names while it looked up the group's. `system.net`, `system.data`, and `system.codedom` got
the same message: .NET Framework's `machine.config` declares them, so no application does.
The spec does not say how a group becomes JSON, nor what the machine.config sections are.

## Decision

`configSections` is read as a tree: a section in a group is declared by its path
(`bundleTransformer/core`), and a group's element is converted section by section into a
nested object under the group's key (`BundleTransformer:Core`, what
`GetSection("BundleTransformer:Core")` reads). Each section in a group is converted like any
other declared section: a dictionary handler becomes a dictionary, a section class in the
solution an options class, and a class that is not in the solution is `OFR4401` naming the
class. `--sections` takes a group's name for all of its sections. A section's elements are
matched by their local names: an `xmlns` on a section, there for the editor's schema (as on
SmartStoreNET's `<bundleTransformer xmlns="...">`), puts them in a namespace that .NET's
configuration system ignores, and the converter ignores it too.

A section that is not declared but that machine.config declares is known by a fixed table
(`MachineSections`), with the advice for it. Those whose settings .NET makes in code are
`unsupported` and reported as `OFR4407`, with the code that replaces them: `system.net`
(the HttpClient handler's connection limit and proxy, SmtpClient), `system.data`
(`DbProviderFactories.RegisterFactory`), `system.transactions`, `system.runtime.caching`,
`system.runtime.serialization`, `system.identityModel` and `system.identityModel.services`,
`system.runtime.remoting`, `configBuilders`. Those .NET has nothing for are `dropped` with a
note: `system.codedom` (ASP.NET's run-time compilers), `system.serviceModel.activation`,
`system.xaml.hosting`, `system.xml.serialization`, `system.windows.forms`, the provider
settings `system.data.*`, `uri`, `mscorlib`, `satelliteassemblies`, `windows`.
`system.web.extensions` joins `system.web` (`OFR4403`).

## Alternatives considered

- **Reading the real machine.config**: it exists only where .NET Framework is installed,
  and differs between versions; a fixed list gives every machine the same answer.
- **`OFR4401` with a better message**: its fix ("add the setting to appsettings.json by
  hand") is wrong for settings that .NET takes only in code; a separate code carries the
  right fix and can be filtered on its own.
- **Converting machine.config sections to JSON**: nothing on .NET reads them from
  configuration, so the JSON would suggest a setting that has no effect.
- **Flattening a group's sections to top-level keys**: two groups can have sections of the
  same name, and the nested key keeps the file's structure.

## Consequences

Section groups convert (or report the right reason per section). The machine.config table
is a list to maintain; a name that is in neither configSections nor the table is still
`OFR4401` "not declared". Transforms of sections in groups are not converted (they are
listed by `OFR4404` as before).
