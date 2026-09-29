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

## Alternatives considered

- The newest version with the same assembly version: as wrong as the lowest when the DLL is
  an older build, and a silent upgrade.
- Reading `FileVersionInfo`: on Linux and macOS .NET reads the managed attributes, on Windows
  the Win32 resource; the attributes read with System.Reflection.Metadata give the same answer
  everywhere, and the compiler writes the resource from them.
- Moving the conditioned `PackageReference` into a new conditioned item group: equivalent
  for a `Reference` condition, but loses a `Choose`; the in-place replacement keeps the
  author's structure.

## Consequences

A package version is proposed with the evidence that picked it, and "an upgrade" is visible
as such in the dry run. Resolving a package with many versions that all ship the referenced
assembly version inspects each of them once. A `PackageReference` conditioned on
`$(Configuration)` restores only when restore runs with that configuration (as `dotnet build
-c` does); the condition is the author's.
