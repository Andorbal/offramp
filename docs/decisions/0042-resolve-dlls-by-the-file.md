# 0042. Resolve a loose DLL by the file, not by its assembly version

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/deps.md` (`deps resolve-dlls`)

## Context

`deps resolve-dlls` proposed "the lowest package version shipping the assembly at the
referenced version or higher". Packages that keep one assembly version across releases are
the norm (Iesi.Collections 4.0.0.4000 and 4.0.1.4000 both ship 4.0.0.0; Newtonsoft.Json
13.0.1 to 13.0.3 ship 13.0.0.0), so on NHibernate 4.1 it proposed Iesi.Collections
4.0.0.4000 for a DLL byte-identical to 4.0.1.4000, which `--apply` would have written as a
downgrade in three projects. Its message said "is" for a package that was only an upgrade
(FirebirdSql.Data.FirebirdClient 2.5.2 → 2.6.5), it skipped the exact but unlisted log4net
1.2.10, it matched an unsigned DLL by the package id alone, and `--apply` dropped the
`Condition` of two Debug-only references. The spec did not say how to choose among
versions that ship the same assembly version, nor what to do with conditions.

## Decision

- **Rank by the file.** The DLL is read from disk (identity, `AssemblyFileVersion`,
  `AssemblyInformationalVersion`, SHA-256), and package assets are inspected for the same
  facts. Candidates rank `identical` (same SHA-256), `fileVersion`, `informationalVersion`,
  `assemblyVersion` (today's rule, worded "the closest build"), then `newer` ("an
  upgrade"). A listed version beats an unlisted one within a rank, then the lower version.
  The result's `match` and `OFR1402`'s wording carry the rank.
- **Unlisted versions** are candidates when they ship the referenced assembly version, never
  as an upgrade: a checked-in DLL is often of a version its authors unlisted later, and
  restore accepts an exact unlisted version.
- **An unsigned DLL is matched only by its file** (the first three ranks) and only by an
  unsigned asset. Without a public key the name is all that links it to a package.
- **Conditions are kept** by replacing a conditioned `Reference` in place: the new item goes
  in the same item group (so a group condition or a `Choose` still applies) with the
  `Reference`'s own condition. Unconditioned references are replaced as before.
- **The search stops early**: at the first identical file, and after the last version that
  ships the referenced assembly version, so a package needs few more inspections than
  before; inspections are cached.
- **Packages named otherwise.** A data table, `rules/assembly-packages.yml` (embedded, and
  extended by `deps.assemblyPackages` in `offramp.yml`, which comes first), names the packages
  that ship an assembly under another id (NUnit: `nunit.framework`; Microsoft.SqlServer.Compact:
  `System.Data.SqlServerCe`). Each is searched as well as the id that equals the assembly name,
  and the strongest match wins across them. Feeds cannot be searched by assembly name, so a
  table of the common cases is the deterministic answer.
- **Old .NET Framework DLLs.** Without a `TargetFrameworkAttribute` (compilers wrote it from
  .NET 4.0 on), a DLL that references the .NET Framework's `mscorlib` (token
  `b77a5c561934e089`) is .NET Framework by that `mscorlib`'s version, so `OFR1404` can block
  on it. A COM interop assembly (`ImportedFromTypeLibAttribute`) is reported as such
  (`OFR1405`), not as unmatched or a blocker: modern .NET on Windows can use it.

- **HintPaths outside the repository** (Open Live Writer). The model kept only the file name of
  a HintPath outside the repository and read no metadata, so the version was guessed (the
  lowest package with the name). Now the model reads the metadata where the build found the
  file, and writes a path under the NuGet global packages folder as
  `$(NuGetPackageRoot)<id>/<version>/...`, which is the same on every machine; resolve-dlls
  takes the package and version from that path (`match: path`), as it does for
  `packages/<Id>.<Version>/`. A DLL whose version is still unknown matches no package.
- **References declared in an import are not edited.** Only a `Reference` the project file
  declares is replaced; one from a Directory.Build.props or another import is reported once per
  assembly and declaring file (`OFR1406`), with the projects it reaches. The declaring file is
  found without evaluation: Directory.Build.props/.targets from the project's folder up, and
  imports with literal paths.

- **`--apply` is verified, and legacy projects outside Windows get no `PackageReference`.** On
  NHibernate, `--apply` exited 0 and the solution then failed with 2,505 errors on Linux: the
  .NET SDK restores a legacy project's `PackageReference` (the compile-only block's legacy
  section sets `RestoreProjectStyle`) but never turns its `lib/` assets into compiler
  references; that is `ResolveNuGetPackageAssets` in Visual Studio's `Microsoft.NuGet.targets`,
  which the SDK does not ship. So outside Windows a legacy project keeps its References
  (`OFR1407`, pointing to `csproj modernize`), and `codemod run` leaves the sites of a codemod
  that needs a package in such a project alone (`OFR4512`). Every `--apply` then runs the
  configured verification of the edited projects and their direct dependents and rolls back from
  the journal on failure (`OFR1408`), as `move apply` and `codemod run` do; on Windows, where
  `dotnet build` has no `Microsoft.NuGet.targets` either, the verification is what catches it.

## Alternatives considered

- The newest version with the same assembly version: as wrong as the lowest when the DLL is
  an older build, and a silent upgrade.
- Reading `FileVersionInfo`: on Linux and macOS .NET reads the managed attributes, on Windows
  the Win32 resource; the attributes read with System.Reflection.Metadata give the same answer
  everywhere, and the compiler writes the resource from them.
- Adding a target to the compile-only block that maps the assets file to references for legacy
  projects: the SDK's `ResolvePackageAssets` needs SDK properties a legacy project does not
  have, and a hand-written resolver is the NuGet resolver CLAUDE.md rules out. Converting the
  project is the supported route.
- Moving the conditioned `PackageReference` into a new conditioned item group: equivalent
  for a `Reference` condition, but loses a `Choose`; the in-place replacement keeps the
  author's structure.

## Consequences

A package version is proposed with the evidence that picked it, and "an upgrade" is visible
as such in the dry run. Resolving a package with many versions that all ship the referenced
assembly version inspects each of them once. A `PackageReference` conditioned on
`$(Configuration)` restores only when restore runs with that configuration (as `dotnet build
-c` does); the condition is the author's.
