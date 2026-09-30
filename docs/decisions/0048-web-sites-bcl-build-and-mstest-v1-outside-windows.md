# 0048. Build around Web Site projects, and skip Microsoft.Bcl.Build's redirects, outside Windows

- Status: accepted
- Date: 2026-09-29
- Spec section: `docs/spec/commands/workspace.md` (`scan`), `docs/compiling-on-macos.md`

## Context

Three more build steps stopped field-test builds outside Windows, and `scan` named
none of them:

- **ASP.NET Web Site projects.** A Web Site is a folder listed in the solution
  (type `{E24C65DC-7377-472B-9ABA-BC803B73C61A}`) that the solution build
  precompiles with `AspNetCompiler`. .NET's MSBuild does not have it, and stops
  the whole solution with MSB4249 before building any project: NHibernate 4.1's
  `NHibernate.Everything.sln` loaded 0 projects in 2 seconds, and every
  `OFR0101` reason was wrong ("check the solution configuration", "it
  references a project that failed", "unsupported project type ()").
- **`Microsoft.Bcl.Build`.** Its `EnsureBindingRedirects` task is built against
  .NET Framework's MSBuild 4.0 and fails to load on .NET's MSBuild (MSB4062):
  SmartStoreNET's FacebookAuth and Open Live Writer's PostEditor. The package's
  own switch, `SkipEnsureBindingRedirects=true`, avoids it.
- **MSTest v1.** `Microsoft.VisualStudio.QualityTools.UnitTestFramework` ships
  only with Visual Studio: Open Live Writer's two test projects failed with 363
  errors. `MSTest.TestFramework` has the same namespace.

## Decision

- **`scan` builds without the Web Site projects.** When the solution (or the
  filter scanned) lists a Web Site and `scan` builds with `dotnet build`, it
  writes a solution filter of every other project to `.offramp/scan.slnf`, which
  Offramp owns, and builds that. The model's solution stays the scanned one.
  Each Web Site is `OFR0101` with the project type named ("ASP.NET Web Site
  project") and a new Windows-only step, `web-site` (`OFR0126`). With
  `--msbuild`, Visual Studio's MSBuild builds the site, and the filter is not
  used. Any other solution entry without a project file names its type instead of
  "unsupported project type ()".
- **The compile-only block gets a fourth section** that sets
  `SkipEnsureBindingRedirects=true` outside Windows, marked by that element, so
  `doctor --fix` adds it to files with the first three sections. Compile-only
  builds need no binding redirects, and on Windows the task still runs, because
  there it writes the redirects the application needs. A project that imports
  `Microsoft.Bcl.Build.targets` without the switch set, or whose build fails with
  MSB4062 from the task, is a new step, `bcl-build` (`OFR0124`).
- **MSTest v1 is named, not fixed.** A `Reference` to
  `Microsoft.VisualStudio.QualityTools.*` without a `HintPath` is a new step,
  `mstest-v1` (`OFR0125`). `docs/compiling-on-macos.md` gives the compile-only
  replacement for a legacy project (the `lib/net45` DLLs of
  `MSTest.TestFramework` 1.4.0), and the real fix, MSTest v2, is a change to the
  project the user makes.

## Alternatives considered

- Building each project on its own instead of the solution: loses the solution's
  configuration mapping and build order, and multiplies restore time.
- Rewriting the solution without the Web Site in place: edits the user's file.
  A filter in Offramp's state folder edits nothing.
- Putting `SkipEnsureBindingRedirects` in the first section: a file with that
  section from an earlier Offramp would never gain it, since `doctor --fix` adds
  whole sections by marker.
- Replacing MSTest v1 references from the block: the block would have to remove a
  `Reference` item the project declares, which a props file cannot do cleanly, and
  the replacement changes what the tests compile against.

## Consequences

A solution with a Web Site builds and loads everything else outside Windows; the
site itself is reported and stays out of the model, as it would with `--msbuild`,
since it has no project file to evaluate. `doctor --fix` proposes the new section
for every repository that has the first three, which is one more diff line for
users who have no `Microsoft.Bcl.Build`. MSTest v1 projects still fail to compile
until the user adds the replacement, but the first scan now says why and how.
