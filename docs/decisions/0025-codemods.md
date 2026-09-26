# 0025. Drive codemods as analyzer + fixer pairs over recorded compilations, condition framework-specific packages, and defer the config shim

- Status: accepted
- Date: 2026-09-26
- Spec section: `docs/spec/commands/codemod.md`

## Context

The spec names the catalog and the rules for a codemod: idempotent, trivia-preserving,
semantic, one diagnostic per skipped site, and tested before/after and on a fixture. It
leaves these open:
- how the driver runs fixers without MSBuild
- where the fixers live, given that analyzers must load in the command-line compiler
- which target of a multi-target project is rewritten
- what "adds the package" means for packages only one kind of target needs
- how `--format-mode` reports sites
- two catalog entries that depend on commands that do not exist yet: `config-manager`'s
  `--shim` mode (`config convert`, M12), and `binaryformatter-clone`'s "only when
  `audit serialization` classified the site as transient"

## Decision

- **Two assemblies.** `Offramp.Analyzers` holds the analyzers. It targets netstandard2.0
  on Roslyn 4.8 and does not use Workspaces, so csc, the IDE, and `dotnet format` all load
  it. `Offramp.Analyzers.CodeFixes` holds the fixers, which need Workspaces (RS1038). It
  also packs both as the `Offramp.Analyzers` NuGet package. The two assemblies still
  reference nothing in Offramp. `Offramp.Refactoring` references both for the driver.
- **One fix path.** Every fixer implements `FixSitesAsync(document, diagnostics)`,
  which rewrites all of a document's sites in one pass; `FixDocumentAsync` runs it, then
  the code action cleanup, with the file's line ending. The IDE's single fix, fix-all,
  `dotnet format`, and `codemod run` all call `FixDocumentAsync`.
  - The cleanup is the fixer's own, not the code action's: the formatter's line ending
    is the platform's (or `end_of_line`), and fixers build code from strings with `\n`,
    so a CRLF file would get LF lines on Linux and an LF file CRLF lines on Windows.
    `FixDocumentAsync` formats with the file's line ending, removes the annotations (the
    code action's cleanup then has nothing to do), and rewrites the line endings of the
    changed text to the file's. The unit tests run with the opposite `end_of_line`, and
    one runs a CRLF file.
  The unit tests run each before/after pair through the testing library and through the
  driver's path, and require both to give the same text.
  - The two paths do differ: Roslyn's formatter leaves the separator before an appended
    argument alone on one of them. So fixers write separators with their spacing.
- **Skips are diagnostics.** An analyzer reports every site. A site that is not rewritten
  carries its reason in the diagnostic property `OfframpSkipReason`, and fixers ignore
  those sites. So the IDE shows the reason, and the driver turns it into OFR4501.
- **The driver** builds an `AdhocWorkspace` project from the recorded compilation:
  documents, references, and options, with per-tree options dropped and the chosen IDs
  forced to info. It runs the codemods in catalog order, each over the result of the
  previous one.
  - Only the project's compile items are written. A file whose text differs from the
    recording is skipped (OFR4504), because rewriting it would lose the newer edit.
  - It edits the first .NET Framework target's compilation. That is where the legacy APIs
    compile, and it matches every other analysis. Code active only in another target is
    not rewritten.
- **Packages** carry their targets: `all`, `framework`, or `modern`.
  - Framework-only packages (System.Text.Json, System.Text.Encoding.CodePages) and
    modern-only ones (System.ServiceProcess.ServiceController) are always written in an
    item group conditioned on `TargetFrameworkIdentifier`, even when every current target
    matches. A later retarget then neither loses the reference nor gets an unneeded one
    (NU1510).
  - A package already referenced anywhere in the project file, conditioned or not, is
    not added again. This keeps the second run empty.
  - packages.config projects are not edited (OFR4505). Converting them is `csproj
    modernize`'s job.
- **assemblyinfo** needs to know that the SDK generates the attributes. The package makes
  `UsingMicrosoftNETSdk` and `GenerateAssemblyInfo` compiler-visible. The driver derives
  them from the model and from the presence of the SDK's generated AssemblyInfo file in
  the recorded compilation. Removed values move to project properties unless the project
  sets them. The fixture test scans a project that does not build (CS0579); its compiler
  call is recorded all the same.
- **`--format-mode`** only reports what dotnet format's report says it changed. There is
  no journal and no verify: dotnet format writes the files itself. Projects without the
  package are skipped (OFR4506), and a failed run is OFR4508.
- **Deferred.**
  - `config-manager --shim`: it redirects call sites to the shim that `config convert`
    generates, and that does not exist before M12. Until then, sites in classes without
    constructor injection are skipped with the reason.
  - The `audit serialization` gate on `binaryformatter-clone` is replaced by the
    structural rule itself. A method that serializes to a `MemoryStream` and deserializes
    the same bytes before returning is the transient case by construction. The bytes never
    leave the method.
- **Experimental.** `thread-abort` is experimental. Replacing an abort with cooperative
  cancellation changes when the thread stops: at the next loop check, not at once. Every
  change needs review.
- **Scan fix.** The fixture's idempotency test showed that `scan` right after a build
  recorded no compiler calls. MSBuild skipped the compiler for up-to-date projects. `scan`
  now builds with `--no-incremental`.

## Alternatives considered

- Run the fixers through `dotnet format` only. It needs the package in every project and
  a restore, and it cannot report skipped sites or produce a diff and journal. It stays as
  `--format-mode` for teams that already reference the package.
- Rewrite every target of a multi-target project. The edits for different targets can
  conflict on shared files. The legacy target is where the codemods apply.
- Add framework-only packages without a condition to .NET Framework-only projects. The
  first retarget would then keep references that the SDK warns about.

## Consequences

- The IDE, `dotnet format`, and `codemod run` agree on every rewrite, and the tests
  enforce it.
- A codemod needs a rescan between runs: the driver works from the recorded text, and it
  refuses (OFR4504) rather than guesses when the files moved on.
