# cpm-shadowing

Central package management hazards for `deps consolidate --cpm` (and `doctor`):

| Path | Hazard |
|---|---|
| `tools/Stray/Stray.csproj` | not in the solution, but under the root `Directory.Packages.props` (OFR1301) |
| `src/Nested/Directory.Packages.props` | shadows the root file without importing it (OFR1302) |
| `src/Legacy/packages.config` | a solution project on packages.config (OFR1303) |

`src/Api` (Newtonsoft.Json 12.0.3) and `src/Worker` (13.0.1) consolidate. Because
OFR1301 fires, the central file gets a non-default name (`CpmShadowing.Packages.props`)
and the two projects opt in, so `tools/Stray` is not touched.
