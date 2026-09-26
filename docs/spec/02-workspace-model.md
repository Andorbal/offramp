# 02. Workspace model

`offramp scan` writes `.offramp/workspace.json`. Every other command reads it.
This document is the schema and the rules for producing it.

## Inputs

One of:

1. **A solution** (`--solution`, or auto-detected when the repo has one). `scan`
   runs `dotnet build <sln> -bl:.offramp/msbuild.binlog -c <verify.configuration>
   -p:<verify.properties>` (restore included), ingests the binlog, and converts
   it to `.offramp/build.complog`. `--no-build` reuses the previous scan's binlog.
2. **`--binlog PATH`**: an existing MSBuild binary log, typically captured on a
   Windows agent with `dotnet build -bl` or `msbuild /bl`. A log from another
   checkout or machine is not converted to a compiler log here (its conversion
   would read this machine's files); scan reports `OFR0132`.
3. **`--binlog PATH --complog PATH`**: the full model from logs captured
   elsewhere: evaluation from the binary log, compiler calls from the compiler
   log (copied to `.offramp/build.complog`).
4. **`--complog PATH`** alone: a compiler log (`complog create`) is
   self-contained (sources and references embedded) but records compiler
   invocations only, so the model is reduced: target frameworks, compile items,
   project references, assembly names, and define constants, without packages,
   SDK, test detection, or Windows-only steps (`OFR0103`).

Absolute paths in a log are mapped onto this checkout by inferring the capture
root from the logged project paths, case-insensitively for Windows and macOS
captures (`docs/decisions/0010-scanning-logs-from-elsewhere.md`). Assets files
are read from this checkout; a missing one is `OFR0104` (restore first).

There is no `--fast` mode: a build that skips the compiler produces no
assemblies for project references, so dependents lose their compiler calls
(`docs/decisions/0008-no-fast-scan.md`).

## Ingest

From the binlog, via the structured log reader (`MSBuild.StructuredLogger`):

- Per project × target framework ("evaluation"): `TargetFramework(s)`,
  `OutputType`, `AssemblyName`, `RootNamespace`, `UsingMicrosoftNETSdk`,
  `Sdk` name (`Microsoft.NET.Sdk`, `.Web`, `.Worker`, `.WindowsDesktop`),
  `UseWPF`, `UseWindowsForms`, `IsTestProject`, `IsPackable`,
  `GenerateSerializationAssemblies`, `ManagePackageVersionsCentrally`,
  `DirectoryPackagesPropsPath`, `ProjectTypeGuids` (legacy), `LangVersion`,
  `Nullable`, `TreatWarningsAsErrors`, `NoWarn`, `DefineConstants`.
- Items: `Compile`, `ProjectReference`, `PackageReference` (with `Version`,
  `VersionOverride`, `PrivateAssets`), `Reference` (with `HintPath`),
  `COMReference`, `EmbeddedResource`, `None` with `CopyToOutputDirectory`,
  `InternalsVisibleTo`, `Analyzer`.
- Windows-only build steps, from evaluated properties, items, and imports so
  they are found even when the build failed or never ran them:
  `GenerateSerializationAssemblies=On` (or `Auto` when the sgen target ran),
  `COMReference`/`COMFileReference`, `EntityDeploy`, `TransformOnBuild` or the
  TextTemplating targets, `Fakes` items, `.sqlproj` or SqlTasks targets, and
  pre/post-build events calling Windows commands (`OFR0110`–`OFR0115`).
- `project.assets.json` path per project → parsed with `NuGet.ProjectModel`
  for the resolved transitive package graph per target framework.

Toolchain items are left out so the model is the same on every OS
(`docs/decisions/0011-workspace-model-rules.md`): references the SDK or a
package's build files inject (`IsImplicitlyDefined`, `_SDKImplicitReference`,
`NuGetPackageId` metadata, `Pack=false` without a hint path, `mscorlib`), and
resolved packages reachable only from `autoReferenced` dependencies
(NETStandard.Library, Microsoft.NETFramework.ReferenceAssemblies).

From the compiler log, via `Basic.CompilerLog.Util`: one `CompilerCall` per
project × target framework, from which a Roslyn `Compilation` is created on
demand. Compilations are **not** stored in the model; the model stores enough
to rebuild them (the complog path and call index).

## Project kind detection

Evaluated in order; first match wins; the evidence is recorded.

| Kind | Evidence |
|---|---|
| `test` | `IsTestProject=true`, or a PackageReference to a known test framework (xunit, NUnit, MSTest.TestFramework, TUnit) or adapter, or legacy test ProjectTypeGuid |
| `web` | `Sdk=Microsoft.NET.Sdk.Web`, or legacy web ProjectTypeGuid, or `Reference Include="System.Web"` with `OutputType=Library` and a `web.config` |
| `winforms` | `UseWindowsForms=true`, or `Reference Include="System.Windows.Forms"` with `OutputType=WinExe` |
| `wpf` | `UseWPF=true`, or `Sdk=Microsoft.NET.Sdk.WindowsDesktop` with `PresentationFramework` reference |
| `service` | `Reference Include="System.ServiceProcess"` with `OutputType=Exe`, or a PackageReference to Topshelf or `Microsoft.Extensions.Hosting.WindowsServices`, or `Sdk=Microsoft.NET.Sdk.Worker` |
| `console` | `OutputType=Exe` |
| `library` | `OutputType=Library` |
| `unknown` | anything else; emits `OFR0102` |

Users can override a kind in `offramp.yml` (`projects: - path: ... kind: ...`).

## Schema (v1)

```jsonc
{
  "$schema": "https://offramp.dev/schemas/v1/workspace.json",
  "version": 1,
  "createdAt": "2026-09-25T20:00:00Z",
  "repositoryRoot": "/abs/path",
  "solution": "src/Monolith.sln",
  "source": { "kind": "binlog|complog|build", "path": ".offramp/msbuild.binlog", "sha256": "...",
              "complog": { "path": "win.complog", "sha256": "..." } },   // null unless --complog came with --binlog
  "sdk": { "version": "10.0.100", "os": "osx-arm64" },
  "projects": [
    {
      "id": "src/Foo/Foo.csproj",                 // repo-relative path is the id
      "name": "Foo",
      "assemblyName": "Foo",
      "rootNamespace": "Foo",
      "language": "csharp",                       // csharp | vb | fsharp | other, from the extension
      "kind": "library",
      "kindEvidence": "OutputType=Library",
      "sdkStyle": true,
      "sdk": "Microsoft.NET.Sdk",
      "targetFrameworks": ["net48", "net10.0"],
      "frameworkClass": "dual",
      "outputType": "Library",
      "isTestProject": false,
      "properties": { "LangVersion": "latest", "Nullable": "disable", "GenerateSerializationAssemblies": "On" },
      "defineConstants": { "net48": ["TRACE", "DEBUG", "NETFRAMEWORK", "NET48", "NET48_OR_GREATER"] },  // what the compiler saw
      "windowsOnlyBuildSteps": ["sgen"],          // ids: sgen, com, entity-deploy, t4, fakes, ssdt, build-event
      "packagesConfig": false,                    // a packages.config sits beside the project
      "compile": ["src/Foo/A.cs", "src/Foo/Sub/B.cs"],
      "compileExplicit": false,                   // true when csproj lists Compile items explicitly
      "projectReferences": ["src/Bar/Bar.csproj"],
      "packageReferences": [
        { "id": "Newtonsoft.Json", "version": "13.0.3", "versionOverride": null, "privateAssets": null, "tfms": ["net48", "net10.0"] }
      ],
      "assemblyReferences": [
        { "name": "System.Web", "hintPath": null, "kind": "framework" },
        { "name": "ThirdParty.Thing", "hintPath": "lib/ThirdParty.Thing.dll", "kind": "file",
          "metadata": { "assemblyVersion": "2.1.0.0", "targetFramework": ".NETFramework,Version=v4.5", "publicKeyToken": "..." } }
      ],
      "comReferences": [],
      "internalsVisibleTo": ["Foo.Tests"],
      "resolved": {                               // from project.assets.json, per tfm
        "net10.0": {
          "packages": [ { "id": "Microsoft.Extensions.Logging", "version": "8.0.1", "dependencies": [ { "id": "Microsoft.Extensions.Logging.Abstractions", "range": "[8.0.1, )" } ], "direct": true } ]
        }
      },
      "compilerCalls": { "net48": { "complog": ".offramp/build.complog", "index": 17 }, "net10.0": { "complog": "...", "index": 18 } },
      "loc": 18234,                               // lines in Compile items, cheap count
      "partial": true,                            // only when a target framework has no compiler call
      "config": { "kindOverride": null, "excluded": false }
    }
  ],
  "graph": {
    "edges": [ { "from": "src/Foo/Foo.csproj", "to": "src/Bar/Bar.csproj", "kind": "project" } ],
    "cycles": [ ["src/A/A.csproj", "src/B/B.csproj"] ],
    "topologicalOrder": ["src/Bar/Bar.csproj", "src/Foo/Foo.csproj"]
  },
  "packages": {                                    // index across projects
    "Newtonsoft.Json": { "versions": { "9.0.1": ["src/Customer.Api/Customer.Api.csproj"], "13.0.3": ["src/Foo/Foo.csproj"] } }
  },
  "inputs": [ { "path": "src/Foo/Foo.csproj", "sha256": "..." } ],   // for staleness
  "diagnostics": [ ... ]                           // OFR01xx from loading
}
```

## Staleness

The model records `inputs`: the SHA-256 of every project file (`.csproj`,
`.vbproj`, `.fsproj`, `.sqlproj`), solution (`.sln`, `.slnx`, and the model's own
`.slnf`), `Directory.*.props/targets`, and `packages.config` in the repository
(outside `bin/`, `obj/`, `packages/`, dot-directories, and the state directory).
Every command that reads the model compares them, and `source.sha256` for a
supplied log, with the files on disk; any changed, added, or removed input
produces `OFR0002` naming what changed (warning by default; `--fail-on-stale`
makes it an error). Content hashes, not modification times, so a fresh clone of
the same commit is fresh (`docs/decisions/0009-staleness-by-content-hash.md`).
`scan --if-stale` rescans only when needed.

## Ledger and snapshots

`scan` also appends a snapshot to `.offramp/ledger/<date>-<shorthash>.json`
containing per-project `frameworkClass`, `kind`, `loc`, and package counts.
Snapshots are small and intended to be committed; `report` uses them to draw
progress over time. `.offramp/cache/` and `.offramp/*.binlog|complog` are
ignored by git (`init` writes the `.gitignore` entries).

## Slicing

Large repositories are worked in slices. `--solution` can point at a solution
filter (`.slnf`); `slice` generates filters for a project closure
(`commands/workspace.md#slice`). The model records which solution or filter
produced it so commands can refuse to operate across slices when a move would
cross the boundary (`OFR2010`).
