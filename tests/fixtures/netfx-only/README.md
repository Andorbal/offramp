# netfx-only

A .NET Framework 4.8 solution in SDK-style project format (no packages.config):

- `Legacy.Core`: library using `System.Web` (`HttpUtility`, `HttpContext.Current`)
  and `System.Drawing` (`Bitmap`, `Size`).
- `Legacy.App`: console application referencing `Legacy.Core` and Newtonsoft.Json.

Exercised by: `scan` (framework-class detection, kinds, graph), later `graph`,
`deps audit`, `deps gac`, `audit api`.
