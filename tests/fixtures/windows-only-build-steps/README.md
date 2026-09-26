# windows-only-build-steps

Projects whose builds need Windows, so `dotnet build` fails on macOS and Linux:

- `Soap`: `GenerateSerializationAssemblies=On` (sgen), an `EntityDeploy` EDMX
  model, a T4 template transformed at build time (`TransformOnBuild`), a
  Microsoft Fakes item, and a post-build event that calls `signtool.exe`.
- `Office`: a `COMReference` imported with `tlbimp`.
- `Database`: a classic SSDT `.sqlproj`, whose targets only exist with Visual
  Studio on Windows.

This fixture is **not built by tests**. `msbuild.binlog` was captured once with
`dotnet build WindowsOnly.sln -bl:msbuild.binlog` on Linux; the build fails at
`ResolveComReference` (MSB4803) and `SGen` (MSB3474), and the log still carries
every evaluation Offramp needs. Tests scan it with `offramp scan --binlog`,
which maps the capture machine's paths onto the fixture's copy.

Exercised by: `scan --binlog` (OFR0110–OFR0115, OFR0130), `doctor` and
`doctor --fix`.

To recapture after changing the projects: delete `msbuild.binlog`, run the
command above from this directory, and commit the new log.
