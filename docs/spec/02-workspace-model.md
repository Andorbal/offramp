# 02. Workspace model

`offramp scan` writes `.offramp/workspace.json`. Every other command reads it.
This document is the schema and the rules for producing it.

## Inputs

Exactly one of:

1. **A solution** (`--solution`, or auto-detected when the repo has one). `scan`
   runs `dotnet build <sln> -bl:.offramp/msbuild.binlog -p:<verify.build.properties>`
   (restore included) and then ingests the binlog.
2. **`--binlog PATH`**: an existing MSBuild binary log, typically captured on a
   Windows agent with `dotnet build -bl` or `msbuild /bl`.
3. **`--complog PATH`**: a compiler log produced by `complog create` from a
   binlog. Self-contained (sources and references embedded). Preferred when the
   log travels between machines.

`scan --fast` (M1 investigation) records compiler arguments without compiling:
`-p:SkipCompilerExecution=true -p:ProvideCommandLineArgs=true`. Use it only if
compiler-log rehydration works from such a log; otherwise drop the flag.

## Ingest

From the binlog, via `Microsoft.Build.Logging.StructuredLogger`:

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
- Targets executed, to detect Windows-only build steps (`GenerateSerializationAssemblies`,
  `ResolveComReferences`, `EntityDeploy`, `TransformAll` for T4, `SqlBuild`).
- `project.assets.json` path per project → parsed with `NuGet.ProjectModel`
  for the resolved transitive package graph per target framework.

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
  "source": { "kind": "binlog|complog|build", "path": ".offramp/msbuild.binlog", "sha256": "..." },
  "sdk": { "version": "10.0.100", "os": "osx-arm64" },
  "projects": [
    {
      "id": "src/Foo/Foo.csproj",                 // repo-relative path is the id
      "name": "Foo",
      "assemblyName": "Foo",
      "rootNamespace": "Foo",
      "kind": "library",
      "kindEvidence": "OutputType=Library",
      "sdkStyle": true,
      "sdk": "Microsoft.NET.Sdk",
      "targetFrameworks": ["net48", "net10.0"],
      "frameworkClass": "dual",
      "outputType": "Library",
      "isTestProject": false,
      "properties": { "LangVersion": "latest", "Nullable": "disable", "GenerateSerializationAssemblies": "On" },
      "windowsOnlyBuildSteps": ["sgen"],
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
  "diagnostics": [ ... ]                           // OFR01xx from loading
}
```

## Staleness

Every command compares `source.sha256` and the mtimes of all `.csproj`,
`Directory.*.props/targets`, `.sln*`, and `packages.config` files against the
values recorded at scan time. A mismatch produces `OFR0002` (warning by
default; `--fail-on-stale` makes it an error). `scan --if-stale` rescans only
when needed.

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
