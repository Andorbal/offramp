# 0054. COM interop and P/Invoke into Windows libraries taint a cross-platform target, not a `-windows` one

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/seams.md` (`seams`, unportable symbols)

## Context

`seams --unportable-from audit` takes its taint from `audit api` findings only. COM interop and
P/Invoke compile on every target, so `audit api` never reports them, and `seams` never saw
them. In Open Live Writer 0.6.3 the editor is MSHTML, reached through 121 `[ComImport]`
interfaces, and the solution declares 314 P/Invokes (`audit native`), yet none of it tainted a
type, while Windows Forms did. The spec did not say whether code that compiles everywhere but
only works on Windows is unportable.

## Decision

From `audit` (not `list`, which is only the symbols given), and only when the target is not
`-windows`, `seams` takes these as unportable:

- a type declared `[ComImport]` (reason `COM interop ([ComImport])`);
- a type that declares a `[DllImport]` of a Windows system library (`P/Invoke into LIB (ENTRY)`);
- a type that uses a COM type (a type with `[ComImport]`, from an interop assembly, an embedded
  interop type, or another project) declared outside the project (`COM interop: T`), or calls a
  P/Invoke of a Windows system library declared outside the project (`P/Invoke into LIB (M)`).

The Windows system libraries are the list `audit native` uses to set `details.windowsOnly`
(`user32`, `kernel32`, `ole32`, `oleaut32`, ...). A call to the project's own interop
declaration is a reference between the project's types: it does not taint, it is where a seam
goes (an interface over the `NativeMethods` class or the COM wrapper). The target is
`-windows` when the project uses Windows Forms or WPF, the rule `audit api` uses for its
target compilation; there COM and Windows P/Invoke work and taint nothing.

## Alternatives considered

- Consuming `audit native` findings (`OFR3301`, `OFR3310`): they cover the declarations only,
  not a project that uses COM interfaces declared in another project (Open Live Writer's
  `PostEditor` uses `OpenLiveWriter.Mshtml`), and `OFR3310` for a `COMReference` points at the
  project file, not a type.
- Tainting every P/Invoke: a library such as `sqlite3` or one the application ships for each
  platform works off Windows; only the system libraries are known not to.
- Tainting callers of the project's own interop declarations: `NativeMethods` classes are called
  from everywhere, and the interface belongs on them, which is what the cut finds.
- Tainting interop on `-windows` too: it works there, and `seams` is about what cannot run on
  the target.

## Consequences

`seams` on a cross-platform target now fences COM and Windows P/Invoke like any unportable API.
Libraries loaded by name at run time (`LoadLibrary`, `NativeLibrary`) and late-bound COM
(`Type.GetTypeFromProgID`, `dynamic`) are not seen. `[LibraryImport]` is not read yet: legacy
code has none.
