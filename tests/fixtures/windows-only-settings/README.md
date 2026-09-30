# windows-only-settings

Settings that only Windows, or only .NET Framework's MSBuild, can carry out, set
in the project files themselves, where the compile-only block in
`Directory.Build.props` cannot turn them off: MSBuild reads
`Directory.Build.props` first, so the project's own value wins.

- `Site`: an `MSBuild.SDK.SystemWeb` site with `MvcBuildViews=true`
  (`AspNetCompiler`, MSB4803) and an `xcopy` post-build event.
- `Contracts`: an SDK-style `net48` library with
  `GenerateSerializationAssemblies=On` (sgen, MSB3474) and a `PostBuild` target
  whose `Exec` runs `xcopy`.
- `Legacy`: a legacy project with sgen on in its Release property group and a
  cmd.exe post-build event after the `Microsoft.CSharp.targets` import, where
  Visual Studio writes it.

Built by tests on every OS: with the compile-only block alone, a plain `dotnet
build -c Release` fails on sgen (on Windows too: .NET's MSBuild has no `SGen`
task); after `doctor --fix` conditions each setting, a plain `dotnet build` of
the whole solution succeeds in both configurations, with no Offramp properties.
Outside Windows the build events write nothing; on Windows they run and fill
`drop/`, and sgen and `AspNetCompiler` are skipped as they are elsewhere
(`docs/decisions/0063-condition-windows-only-settings-in-project-files.md`).
