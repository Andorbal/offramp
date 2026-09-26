# Audit native (net10.0)

10 findings in 2 projects: 0 errors, 2 warnings, 8 info.

| Rule | Severity | Title | Findings | Projects |
|---|---|---|---:|---:|
| [OFR3301](https://offramp.dev/diagnostics/OFR3301) | info | P/Invoke declaration | 5 | 1 |
| [OFR3302](https://offramp.dev/diagnostics/OFR3302) | warning | ANSI string marshalling by default | 1 | 1 |
| [OFR3303](https://offramp.dev/diagnostics/OFR3303) | info | candidate for [LibraryImport] | 2 | 1 |
| [OFR3310](https://offramp.dev/diagnostics/OFR3310) | warning | COM interop | 1 | 1 |
| [OFR3320](https://offramp.dev/diagnostics/OFR3320) | info | structured exception interop | 1 | 1 |

## src/Behavior.Legacy/Rules/Native.cs

- `src/Behavior.Legacy/Rules/Native.cs:10` OFR3301 info: Behavior.Rules.OFR3301.Positive() calls Positive in kernel32.dll, a Windows library.
- `src/Behavior.Legacy/Rules/Native.cs:21` OFR3301 info: Behavior.Rules.OFR3302.Positive(System.IntPtr, string, string, uint) calls MessageBox in user32.dll, a Windows library.
- `src/Behavior.Legacy/Rules/Native.cs:24` OFR3301 info: Behavior.Rules.OFR3302.Negative(System.IntPtr, string, string, uint) calls MessageBoxW in user32.dll, a Windows library.
- `src/Behavior.Legacy/Rules/Native.cs:30` OFR3301 info: Behavior.Rules.OFR3303.Positive(int) calls abs in libc.
- `src/Behavior.Legacy/Rules/Native.cs:33` OFR3301 info: Behavior.Rules.OFR3303.Negative(string) calls getenv in libc.
- `src/Behavior.Legacy/Rules/Native.cs:21` OFR3302 warning: Behavior.Rules.OFR3302.Positive(System.IntPtr, string, string, uint) marshals 'text', 'caption' as ANSI (the default CharSet).
- `src/Behavior.Legacy/Rules/Native.cs:10` OFR3303 info: Behavior.Rules.OFR3301.Positive() has a blittable signature; [LibraryImport] generates its marshalling at compile time.
- `src/Behavior.Legacy/Rules/Native.cs:30` OFR3303 info: Behavior.Rules.OFR3303.Positive(int) has a blittable signature; [LibraryImport] generates its marshalling at compile time.
- `src/Behavior.Legacy/Rules/Native.cs:38` OFR3310 warning: COM interop: System.Runtime.InteropServices.ComImportAttribute.
- `src/Behavior.Legacy/Rules/Native.cs:54` OFR3320 info: Structured exception interop: System.Runtime.InteropServices.Marshal.GetHRForException(System.Exception).
