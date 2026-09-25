# Diagnostics

Every Offramp diagnostic has a stable code. This file is the index; from M0 on
it is generated from the code (`eng/gen-diagnostics`) and a test fails when a
code exists without an entry. Until then, the codes referenced by the
specification are listed here so implementations use the same numbers.

Severity may be overridden per code in `offramp.yml` (`rules:`); overridden
findings carry `"overridden": true`.

## Ranges

| Range | Area |
|---|---|
| OFR0001–0099 | workspace/model and configuration |
| OFR0100–0199 | project loading |
| OFR0200–0299 | graph/report |
| OFR1000–1999 | dependencies |
| OFR2000–2999 | moves |
| OFR3000–3999 | audits |
| OFR4000–4999 | scaffolding, seams, codemods |
| OFR5000–5999 | verification |
| OFR9000–9999 | MCP and LLM |

## Codes referenced by the specification

| Code | Severity | Meaning |
|---|---|---|
| OFR0001 | error | workspace model missing; run `offramp scan` |
| OFR0002 | warning | workspace model stale (inputs changed since scan) |
| OFR0050 | warning | unknown key in `offramp.yml` |
| OFR0051 | warning | pin without a reason |
| OFR0052 | info | rule override without a reason |
| OFR0101 | warning | project could not be loaded (reason attached) |
| OFR0102 | info | project kind unknown |
| OFR0110 | warning | Windows-only build step: sgen |
| OFR0111 | warning | Windows-only build step: COM reference |
| OFR0112 | warning | Windows-only build step: EDMX EntityDeploy |
| OFR0113 | warning | Windows-only build step: T4 / Fakes |
| OFR0114 | warning | Windows-only build step: SSDT |
| OFR0115 | warning | Windows-only build step: build event calling Windows executable |
| OFR0120 | warning | project reference cycle |
| OFR0130 | error | analysis build failed; model partial |
| OFR0201 | info | Mermaid output too large to render well |
| OFR1001 | error | no package version supports the target |
| OFR1002 | warning | in-use version does not support the target |
| OFR1003 | warning | package deprecated |
| OFR1004 | warning | package assets are Windows-only |
| OFR1005 | warning | package not found on any feed |
| OFR1006 | warning | feed unreachable; result partial |
| OFR1203 | warning | pin kept a package below the otherwise-selected version |
| OFR1210 | error | pin conflicts with a transitive lower bound (chain attached) |
| OFR1211 | error | restore verification reported NU1605/NU1107/NU1608/NU1010 |
| OFR1220 | warning | family member lacks the family version |
| OFR1301 | warning | project outside the solution would inherit CPM |
| OFR1302 | warning | nested Directory.Packages.props shadows the root |
| OFR1303 | warning | packages.config project cannot use CPM |
| OFR1401 | info | loose DLL is another project's output |
| OFR1402 | info | loose DLL matched to a package |
| OFR1403 | warning | loose DLL unmatched |
| OFR1404 | error | loose Framework-only DLL with no replacement |
| OFR1501–1504 | info/warning | binding redirect added/changed/pruned/stale |
| OFR2001 | error | move would create a project reference cycle |
| OFR2002 | error | destination equals source |
| OFR2010 | error | move crosses a solution slice boundary |
| OFR2050 | error | verification failed; changes rolled back |
| OFR2101 | warning | file needs co-move |
| OFR2102 | error | required package unavailable for destination |
| OFR2103 | error | file does not compile in destination |
| OFR2104 | error | source still depends on moved code |
| OFR2105 | warning | Windows-only API in moved file |
| OFR2110 | info | partial type co-moved |
| OFR2111 | warning | destination excludes the file path |
| OFR2120 | warning | namespace differs from destination root namespace |
| OFR2150 | warning | file changed since plan |
| OFR2201 | warning | candidate referenced by production code; not moved |
| OFR2202 | error | multiple candidate test projects |
| OFR2203 | error | no test project found; use `--to` or `--create` |
| OFR2204 | warning | destination path collision |
| OFR2210 | info | test-framework packages removable from source |
| OFR2301 | warning | string reference to a moved type |
| OFR3001 | error | API missing on target |
| OFR3002 | warning | Windows-only API |
| OFR3003 | error | API throws on modern .NET |
| OFR3004–3009 | error | removed technology (WebForms, ASMX, WCF server, Remoting, WF, CAS) |
| OFR3101–3120 | varies | behavior rules (see `spec/commands/audit.md`) |
| OFR3201–3211 | varies | serialization rules |
| OFR3301–3320 | varies | native interop rules |
| OFR3401–3402 | info | dead code candidates; test-only usage |
| OFR3501–3502 | warning | public API differs between targets / from baseline |
| OFR3601 | warning | member cannot be wrapped in `#if` |
| OFR4001–4003 | varies | seams |
| OFR4010 | warning | caller instantiates concrete type directly |
| OFR4020 | warning | sync member over remote boundary |
| OFR4030 | error | gRPC unavailable for net48 host |
| OFR4101–4105 | varies | service conversion notes |
| OFR4201–4202 | varies | web scaffold notes |
| OFR4301–4303 | varies | csproj modernize notes |
| OFR4401–4404 | varies | config convert notes |
| OFR4501, OFR4510 | varies | codemod skipped site; SqlClient encrypt default |
| OFR5001 | error | verification build failed |
| OFR5002 | error | verification timed out |
| OFR5010 | warning | new error code relative to baseline |
| OFR5090 | info | verification skipped by configuration |
| OFR9101 | error | MCP request outside allowed root |
