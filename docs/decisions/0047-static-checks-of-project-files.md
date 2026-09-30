# 0047. Check the projects' files for build problems in one pass

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/workspace.md` (`scan`), `docs/compiling-on-macos.md`

## Context

ADR 0037 had `scan` name the repository's own obstacles outside Windows from the
failed build's errors: a path in another letter case (`OFR0117`) and non-string
resources (`OFR0119`). A build shows them one project at a time. On SmartStoreNET
4.2 it took four scans to find 14 paths in the wrong letter case, and which
projects failed varied between builds: MSBuild's cache of loaded project files
ignores case, so an import fails only on a build node that has not loaded the
file yet. On Open Live Writer, 15 projects have images in `.resx` files and each
scan named the first one; 58 `ResXFileRef` paths in one `.resx` file (MSB3554)
and a folder of source files in the wrong case were not named at all. On
NHibernate 4.1, a `SharedAssemblyInfo.cs` that the NAnt build generates and
`.gitignore` ignores showed up as one `CS2001` inside `OFR0130`, with an absolute
path, and nothing said that the repository's build writes it.

## Decision

After the build, when the log was built in this checkout, `scan` reads what each
project refers to and checks it against the file system:

- **Which paths.** The project file's own `Import`, `Compile`, and
  `EmbeddedResource` paths, and `None` and `Content` paths copied to the output
  (publishing alone reads the others), that have no condition (or, for an
  import, only an `Exists('...')` of a file that exists as spelled), are
  not inside a target, and use no property but `MSBuildProjectDirectory`,
  `MSBuildThisFileDirectory`, `ProjectDir`, `SolutionDir` (the scanned
  solution's folder), and `MSBuildProjectName`; and, when MSBuild evaluated the
  project, its evaluated items of those types and its imports. Only paths inside
  the repository count. The project file alone covers projects whose evaluation
  failed; the evaluation covers items an imported file adds.
- **Letter case** (`OFR0117`): every path that exists only in another letter
  case, and every `ResXFileRef` in the project's `.resx` files that does, in one
  diagnostic per project: imports first, then items by type, then file
  references, each by path. The message names the first; `data.paths` lists all.
  Mismatches only the errors show (a path a target copies, MSB3030) join them.
  `.` and `..` are resolved in the evidence.
- **Non-string resources** (`OFR0119`): the project's `.resx` files with
  resources that .NET's MSBuild embeds only preserialized, by MSBuild's own
  reader rules, checked against the .NET 10 SDK: strings (untyped,
  `System.String`, or its `mscorlib`-qualified name), byte arrays, `ResXNullRef`,
  and file references to text, byte arrays, and memory streams embed as they
  are; anything with a `mimetype`, a type converter, or a file reference to
  another type does not. Not reported when every target framework of the
  project already sets `GenerateResourceUsePreserializedResources` (the SDK's
  default for .NET Core 3.0 and later). The build's MSB3822/MSB3823 is still the
  evidence when no file shows it.
- **Missing sources** (`OFR0123`, new): a `Compile` item whose file exists in no
  letter case, outside the project's `bin/` and `obj/`, one diagnostic per
  project. A file that an MSBuild file of the repository the project imports
  names, or a target or property of the project file, is left out: the build
  writes it itself and missed it only because it stopped earlier (Open Live
  Writer's `writer.build.targets` writes `GlobalAssemblyVersionInfo.cs`). `git check-ignore` tells which are ignored; for those, the message
  says the repository's own build most likely generates them (NAnt, psake,
  Cake, FAKE, GitVersion) and must run first.

The model's `windowsOnlyBuildSteps` of each project include these steps, as the
diagnostics do.

**The compile-only block does not supply `System.Resources.Extensions`.** The
block is static text in `Directory.Build.props` and cannot tell which projects
have non-string resources, so it would restore a package and add a compile
reference for every legacy project of every codebase. .NET Framework 4.6 and
older have no version of the package, and a `PackageReference` there fails the
restore (NU1202) unless conditioned. `docs/compiling-on-macos.md` gives the
form that works for legacy projects instead: the property, a `PackageReference`
that restores the package, and a target that adds the DLL as a reference (the
.NET SDK gives legacy projects no compile references from packages), with the
version per target framework. A reference added by a target stays out of the
workspace model, so `deps resolve-dlls` does not act on it.

## Alternatives considered

- **Before the build.** No evaluation to cover projects that import their items,
  and a file that a target of the build itself generates would be reported as
  missing. After the build, in the same scan, gives the same one-pass answer.
- **Evaluating each project with the MSBuild API.** Loads MSBuild into Offramp's
  process, costs a second evaluation of the whole solution, and fails on the same
  misspelled imports the build fails on.
- **Checking logs from another machine or checkout.** The build's generated files
  may not exist here, which would read as missing sources; those scans keep the
  error-based detection only.
- **One diagnostic per missing file.** A folder of generated files would give
  dozens; one per project with `data.files` names them all.

## Consequences

One scan names every letter-case mismatch, every project with non-string
resources, and every generated source file the repository's build must write
first, whatever order the build ran in. Paths that need other properties
(`$(RepoRoot)`, `$(OutDir)`) are checked only through the evaluation, so a
project whose evaluation failed can still hide such a path until its imports are
fixed. A `Content` item that only publishing copies is not checked, although
publishing from Linux would miss it.
