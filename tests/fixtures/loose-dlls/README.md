# loose-dlls

`src/App` (net48) references four DLLs in `lib/` through `HintPath`, for
`deps resolve-dlls`:

| DLL | What it is | Expected resolution |
|---|---|---|
| `Newtonsoft.Json.dll` | a NuGet assembly (13.0.0.0, Newtonsoft's public key) | `PackageReference Newtonsoft.Json 13.0.1` (OFR1402) |
| `Legacy.Core.dll` | the output of `src/Legacy.Core` | `ProjectReference` (OFR1401) |
| `Vendor.Reporting.dll` | .NET Framework 4.8 only, no package | blocker (OFR1404) |
| `Vendor.Common.dll` | .NET Standard 2.0, no package | unmatched (OFR1403) |

The DLLs are metadata-only stubs written by `LooseDlls.Write` in
`tests/Offramp.Fixtures`; regenerate them with
`OFFRAMP_REGENERATE=1 dotnet test tests/Offramp.NuGet.Tests --filter Loose_dlls`.
