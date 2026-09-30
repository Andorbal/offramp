# 0046. Judge a package version by what it ships

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/deps.md` ("Determining supports target", `deps audit`)

## Context

The spec says a package with neither assets nor dependency groups supports every target,
and that `deps audit`'s `newestSupporting` walks down from the newest version. It does not
say what the audit proposes when the only supporting versions are older than the one in use,
or whether a release with nothing to compile against can replace one with assemblies. On
SmartStoreNET 4.2 the audit proposed to "upgrade" EntityFramework.SqlServerCompact 6.4.4 to
4.3.1, a 2012 release with only `Content/*.transform` files and an install script, which
"supports" `net10.0` because it has no framework-specific assets at all. The same rule made
LibSassHost.Native.win-x64 and JavaScriptEngineSwitcher.V8.Native.win-x64, whose only code is
`runtimes/win-x64/native/*.dll`, `ok` and not Windows-only: the Windows-only check read
`lib/` and `ref/` assemblies only.

## Decision

- **A version without managed assemblies never replaces one with them.** When a version in
  use has assemblies (`lib/`, `ref/`, `runtimes/*/lib/`), a candidate without any does not
  count as supporting the target. Packages that never had assemblies (build-only tools,
  native packages, meta-packages) keep the spec's rule.
- **An upgrade is newer.** `status` is `upgrade` only when a supporting version is newer than
  every in-use version that does not support the target. When only older versions support
  it, the package is `replace` or `blocked` and `OFR1007` (error) names the versions;
  `newestSupporting` and `lowestSupporting` keep reporting the facts of the feed.
- **Native code for Windows is Windows-only.** A package with nothing in `lib/` or `ref/` whose
  runtime-specific files (`runtimes/<rid>/native/`, and `runtimes/<rid>/lib/` for a mixed-mode
  assembly such as JavaScriptEngineSwitcher.V8.Native.win-x64's) are all for Windows runtime
  identifiers (`win`, `win-x64`, `win10-arm64`, ...) is `windowsOnly` with the first such file as evidence
  (`OFR1004`). When the id ends in a Windows runtime identifier and a feed has the same id
  for `linux-x64`, the message names it. The status still says only whether the target
  framework is supported, as for System.Drawing.Common.
- Inspections record the native files and, per assembly, its file version, informational
  version, and SHA-256 (ADR 0042 uses them); the cache format is bumped so older entries are
  recomputed.

- **P/Invoke and COM are Windows-only evidence** (Open Live Writer). A managed assembly that
  calls a library only Windows has (`user32`, `shell32`, `msdelta`, ...) or declares a
  `[ComImport]` class (a coclass, which creates a registered Windows component) is
  Windows-only, like one that references Windows Forms. `kernel32`, `ntdll`, `advapi32`, the
  COM runtime (`ole32`, `oleaut32`), the C runtime, and `[ComImport]` interfaces alone are not
  evidence, for the reason `Microsoft.Win32.Registry` is not: portable libraries use them behind
  an OS check. Measured on SmartStoreNET, counting them made EPPlus 4.5 (its `IEnumSTATSTG`
  interface) and ClearScript (`ole32`) Windows-only.
- **Nothing to judge is not `ok`.** A version with no assemblies, no framework-specific assets,
  no dependency groups, and no native code "supports" every target vacuously. When the package
  map names a successor for such a package (Microsoft.Bcl.Build: built in on modern .NET), it is
  `replace` with `OFR1009`; without an entry it stays `ok`.
- **A codemod's package is judged the same way.** `codemod run` inspects each package it
  would add (the pinned version, on the configured feeds) and checks it against the target
  frameworks it would be added for. On NHibernate 4.1, `sqlclient` added Microsoft.Data.SqlClient
  7.1.0 (net462 and later) to a net40 project without a word. When the package does not support
  a framework, the codemod leaves its sites in that project alone and reports `OFR4511`:
  rewritten code whose package cannot restore is a broken build, not a migration step.

## Alternatives considered

- Dropping the "no assets supports everything" rule: build-only and native packages would
  become `blocked`, which is wrong for most of them (a Windows native package supports every
  framework; it is the operating system it is tied to).
- A `downgrade` status: a new enum value breaks the `status` contract for one rare case,
  and a downgrade is not a recommendation anyway; the diagnostic carries the facts.
- A `windowsOnly` status for native packages: status answers "does a version support the
  target framework"; Windows-only is the separate flag every other package already uses.
- Deciding by the id suffix (`.win-x64`) alone: the files say it; the suffix only helps
  find the sibling package.
- Recording each codemod package's frameworks in the codemod catalog: offline, but a second
  copy of what the nupkg says, which drifts when a version is bumped; CLAUDE.md asks for the
  nupkg to be inspected.

## Consequences

EntityFramework.SqlServerCompact on SmartStoreNET is `blocked` (no version with assemblies
supports `net10.0`), and the four native packages are Windows-only with the Linux package
named where one exists. A package with native code for Windows and managed assemblies is
still judged by its assemblies only.
