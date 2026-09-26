# versions

Five projects with mixed package versions, for `deps audit`, `deps gac`, and (in
later milestones) `deps consolidate` and `redirects sync`.

| Project | Target | Packages | Notes |
|---|---|---|---|
| `Customer.Api` | net48 (web) | Newtonsoft.Json 9.0.1 (pinned in offramp.yml), Microsoft.AspNet.WebApi.Core 5.2.3 | `web.config` with binding redirects; WebApi has no modern version (replace) |
| `Billing` | net48 | Newtonsoft.Json 11.0.2, Microsoft.Extensions.Logging.Abstractions 6.0.0, EntityFramework 6.2.0, Contoso.Legacy.Reports 1.1.0 | EF 6.2.0 needs an upgrade; Contoso.Legacy.Reports is blocked; `System.Drawing` referenced but unused |
| `Reporting` | net48 | Contoso.Serialization 2.0.0, WindowsAzure.Storage 9.3.3 | Contoso.Serialization needs Newtonsoft.Json >= 13.0.3 (the transitive constraint that forces a bump); WindowsAzure.Storage is deprecated |
| `Shared` | netstandard2.0 | Newtonsoft.Json 13.0.1, Microsoft.Extensions.Logging.Abstractions 8.0.0, Contoso.Windows.Controls 1.0.0 | Contoso.Windows.Controls references System.Windows.Forms |
| `Modern.App` | net10.0 | Newtonsoft.Json 12.0.3, Microsoft.Extensions.* 6.0.0, System.Drawing.Common 8.0.0 | System.Drawing.Common is Windows-only on net6+ |

Packages restore from nuget.org, except `Contoso.*`, which come from
`synthetic-feed/` (see `nuget.config`). `feed.json` is the recorded feed the
tests audit against, made by `dotnet run eng/record-feed.cs -- feed.seeds.json
feed.json` (network needed); regenerate `synthetic-feed/` afterwards with
`OFFRAMP_REGENERATE=1 dotnet test tests/Offramp.NuGet.Tests --filter Synthetic_feed`.
`NuGetAudit` is off: the old versions are the point, and advisories change over time.
