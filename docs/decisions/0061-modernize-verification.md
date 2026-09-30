# 0061. Verify a conversion against what the legacy build really compiled

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/scaffold.md#csproj-modernize`, `docs/spec/02-workspace-model.md` (inputs)

## Context

`csproj modernize` builds each converted project in a scratch copy and compares its compiler inputs
with the scan's build of the legacy project. On SmartStoreNET 4.2.0, after the fixes of ADR 0039
and ADR 0043, 10 of 11 conversions still failed that comparison (`OFR4303`), for reasons that were
not the conversion's:

- **Reference changes the toolchain makes.** The legacy builds referenced 103 .NET Standard facades
  (`System.Runtime`, `System.Collections`, ...) that `ImplicitlyExpandDesignTimeFacades` adds from the
  framework's `Facades` folder when a reference depends on them; the SDK-style builds do not need
  them. PackageReference adds the framework assemblies that packages declare in their nuspec
  (`frameworkAssemblies`: PresentationCore, System.Security, System.Net.Http) for every package in
  the graph, and a package's other assemblies (Microsoft.Web.Infrastructure). packages.config did
  neither. Only packages converted in the same run were allowed before.
- **The scratch copy lacked a working-tree fix.** The legacy projects import
  `$(SolutionDir)\.nuget\nuget.targets`; the file is `NuGet.targets`, and on Linux the working
  tree has an untracked `nuget.targets` link, as `OFR0117` suggests. Imported files became model
  inputs (ADR 0049), which the scratch copy takes from the working tree, but imports from
  dot-directories were left out, and one spelling of two was dropped as a duplicate.
- **A converted build event could not be switched off.** SmartStore.Data.Tests' post-build step
  (cmd's `md` and `xcopy`) became a `PostBuild` target with the command inline. `OFR0115`'s remedy
  for compile-only builds, `verify.properties: PostBuildEvent: ""`, empties the property, which the
  target no longer read, so verification failed with MSB3073.

## Decision

- **Reference changes with evidence are allowed.** A reference the converted build adds is allowed
  when its file is one of the compile assemblies of a package the converted project's restore
  resolved (its `project.assets.json`; a package's `build/` folder does not count, since
  Microsoft.NETFramework.ReferenceAssemblies keeps the framework itself there), or when a resolved
  package declares it as a framework assembly; one it
  removes is allowed when the legacy build took it from a `Facades` folder or
  `Microsoft.NET.Build.Extensions`. Each is listed (`transitiveReferencesAdded`,
  `frameworkReferencesAdded`, `facadesRemoved`) with its evidence in `explanations`: the package,
  the declaring packages, or the facades' folder. Every other added or removed reference is still a
  difference.
- **Imported files in dot-directories are inputs**, except in `.git/` and the state directory, and
  an imported file is recorded in each spelling the file system holds: once where it ignores letter
  case (as the disk spells it), in each spelling where it does not.
- **Build events stay, and a target sets them again when their macros have values.**
  `PreBuildEvent` and `PostBuildEvent` stay where the legacy project defines them. In the body of
  an SDK-style project their macros (`$(TargetPath)`, `$(TargetDir)`) expand to nothing, because the
  SDK defines them after the body, so a `SetBuildEvents` target that runs before `BeforeBuild` sets
  each event that is set again, with the same text and its conditions. The SDK's own
  `PreBuildEvent` and `PostBuildEvent` targets then run them as the legacy build did (from the
  output folder, by `RunPostBuildEvent`'s rule, now kept). A global `-p:PostBuildEvent=` wins over
  the body's definition, the property stays empty, the target (conditioned on it being set) leaves
  it so, and the event does not run. The target has to test the property: an assignment in a target
  overrides even a global property.

## Alternatives considered

- Allow any added framework assembly, or any removed `System.*` reference. It would hide a
  conversion that drops a reference the code needs; the evidence rule allows only what restore or
  the legacy targets explain.
- A `PostBuild` target with `Condition="'$(OfframpCompileOnly)' != 'true'"`, or another property of
  Offramp's. Users would carry Offramp's markup in their projects, and the switch users already know,
  `-p:PostBuildEvent=`, would still not work.
- Keep the command inline and have verification drop the target in its scratch copy. Verification
  would then build a project other than the one `--apply` writes, and every later compile-only build
  (scan, move verification) would still run it.
- Keep `PostBuildEvent` in the project body only. Its macros would expand to nothing there.
- Set the events only in a target. A target's assignment overrides the global property, so
  `-p:PostBuildEvent=` would not turn them off.
- Explicit SDK imports (`Sdk.props` and `Sdk.targets`) with the events after `Sdk.targets`, as in a
  legacy project. Exact, but it changes the shape of every converted project that has an event.

## Consequences

Conversions of solutions that reference .NET Standard libraries or packages with framework
assemblies verify as identical, and the result says why each reference changed. A converted
project's build events appear twice, in the body and in `SetBuildEvents`, and look like neither
the legacy form nor Visual Studio's SDK-style form (a `PostBuild` target), but behave like the
legacy form, switch included. A later edit to an event has to change both; the conversion's
`OFR4302` note says where the second copy is. The assets file of the
converted project must be at `obj/project.assets.json`; where a project moves it, packages
converted in the same run still count, and other package references show as differences.
