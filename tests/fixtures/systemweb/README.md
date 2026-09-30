# systemweb

An ASP.NET (System.Web) site on the `MSBuild.SDK.SystemWeb` SDK, and the
library it references:

- `Site`: `Sdk="MSBuild.SDK.SystemWeb/4.0.88"`. The SDK's `Sdk.targets` ends with
  an unconditional `<Import Project="$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets" />`,
  and only Visual Studio installs that file, so `dotnet build` stops with MSB4019
  (on any OS, since the .NET SDK's `VSToolsPath` points into the SDK).
- `Core`: a plain `net48` library, which builds either way.

`web.config` is lower case because the SDK sets `AppConfig=web.config`, and Linux
file systems are case-sensitive.

Built by tests: scanned as is (`OFR0116`, step `web-targets`), and again after
`doctor --fix` added the compile-only block, which for `dotnet build` (outside
Windows, and on Windows since ADR 0064) takes the web targets from the
`MSBuild.Microsoft.VisualStudio.Web.targets` package and then builds cleanly.
