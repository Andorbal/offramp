# 0040. Compile a project for the target with what it uses, not with its kind or assets file

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/audit.md` (`audit api` target compilation)

## Context

`audit api` compiles each .NET Framework project against the target's reference assemblies
and the project's packages, and reports what no longer binds. Two inputs came from the wrong
place, and field tests showed both.

- **Packages.** They came from the assets file's direct packages. A `packages.config`
  project's assets file (the compile-only block restores legacy projects the
  `PackageReference` way) has none of its packages, so none was resolved for the target, and
  every `HintPath` DLL was added as it was: `packages/Microsoft.AspNet.Mvc.5.2.7/lib/net45/System.Web.Mvc.dll`,
  the .NET Framework assembly itself. On SmartStoreNET 4.2, 363 files use `System.Web.Mvc`
  and the audit reported none of it (1,439 `OFR3001`; 11,082 with the packages resolved).
  DotNetNuke's P1 #7 described the same gap.
- **`-windows`.** A project was compiled for `net10.0-windows` with the Windows desktop
  framework only when its kind was `winforms` or `wpf`. Kind detection gives those kinds to
  applications; Open Live Writer keeps its forms and controls in 18 class libraries, which
  were compiled for `net10.0`: 13,374 of 13,910 `OFR3001` findings were Windows Forms and
  `System.Drawing` APIs that `net10.0-windows` has.

## Decision

The target compilation takes every package `packages.config` lists, except development
dependencies, as well as the direct `PackageReference` packages, and leaves out a `HintPath`
DLL that goes through the `packages/<Id>.<Version>/` folder of one of them. `packages.config`
lists transitive packages too, with exact versions, which is what the project runs with.
Packages NuGet reports as unsupported (NU1202) are left out as before (`OFR3011`). A package
NuGet cannot find at its version (NU1101, NU1102, NU1103) is left out of the restore and
reported (`OFR3015`, info); its `HintPath` DLLs stay, so nothing is claimed about it.

A project is compiled for `-windows` with `Microsoft.WindowsDesktop.App` when its kind is
`winforms` or `wpf`, it sets `UseWindowsForms` or `UseWPF`, or it references a Windows
Forms or WPF assembly (`System.Windows.Forms`, `PresentationFramework`, `PresentationCore`,
`WindowsBase`, `System.Xaml`, `UIAutomationProvider`). The rule lives in
`Offramp.Core.Model.WindowsDesktop`, which the guide's `csproj modernize --tfm` suggestion
uses too. On `-windows`, the Windows Forms types .NET keeps only as throwing shims
(`[Obsolete]` with `WFDEV006`: `MenuItem`, `ContextMenu`, `DataGrid`) are `OFR3003`.

## Alternatives considered

- Keep the `HintPath` DLLs next to the packages: the .NET Framework build and the target's
  build of the same package define the same types, and the .NET Framework one hides what is
  missing.
- Only the `packages.config` packages that no other listed package depends on, as if they
  were the direct ones: `packages.config` has no dependency graph, and NuGet would pick the
  versions of the others, which the project does not run with.
- Fail the project (`OFR3010`) when a package cannot be found: a package kept only in a
  committed `packages/` folder would stop the audit of the whole project, where before it
  compiled.
- Decide `-windows` from the APIs the code uses: that needs the compilation this decides.

## Consequences

`audit api` takes longer on `packages.config` solutions (SmartStoreNET: 142 s to 201 s),
since NuGet resolves their packages for the target once per distinct package set. It needs
the feeds the packages come from, as `PackageReference` projects already did. A library that
references `WindowsBase` only for `System.IO.Packaging` is compiled for `-windows`, and
`OFR3002` does not flag its Windows-only APIs, as for any desktop project.
