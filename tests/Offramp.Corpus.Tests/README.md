# Corpus tests

Corpus tests run the Offramp CLI, built from this repository, on real open-source .NET Framework
codebases, each pinned to one commit. They exist because fixtures are small and real codebases
are not: the first one (DotNetNuke) found a dozen wrong or dangerous results that every fixture
passed.

They are slow (tens of minutes per codebase), need the network, and use gigabytes of disk, so
they never run unless someone asks: a plain `dotnet test`, and every CI build, skips them. When
they are asked for, they must be reliable: a failure should mean Offramp regressed, never that
the network blinked or a previous run left files behind.

Each codebase gets there through a **field test** first (`docs/field-tests/README.md`): an open
exploration of how Offramp does on it, a written report, and fixes with their own fixture tests.
The corpus test comes last and pins what the field test found. This page is about that last step.

## Layout

| Path | What it is |
|---|---|
| `codebases.json` | One entry per codebase: repository, pinned commit, solution, time limit, field-test report. The CI workflow builds its job matrix from it. |
| `Codebases/<Name>Tests.cs` | One test class per codebase: harness adjustments, then assertions from its field test. |
| `Harness/CorpusRun.cs` | Checks a codebase out, runs commands, checks what every command must do, runs the standard sweep, writes the summary. |
| `Harness/CorpusCheckout.cs` | Fetches the pinned commit into `tests/.cache/corpus/<name>` and gives each run a fresh clone of it. |
| `Harness/CorpusCodebase.cs` | Reads `codebases.json`. |
| `ManifestTests.cs` | Checks `codebases.json` and the test classes on every CI build (not a corpus test). |

## Running them

Build first, in the configuration you test with; the harness runs
`src/Offramp.Cli/bin/<configuration>/net10.0/offramp.dll`.

```bash
dotnet build
OFFRAMP_CORPUS=dnn dotnet test tests/Offramp.Corpus.Tests --no-build --filter "Category=Corpus&Codebase=dnn"
```

| Variable | Effect |
|---|---|
| `OFFRAMP_CORPUS` | Unset: every corpus test skips. `1` or `all`: all run. `dnn,nhibernate`: those run. The `--filter` still decides which tests the runner loads, so pass both. |
| `OFFRAMP_CORPUS_KEEP=1` | Keep the checkout after the run (its path is in the test output) to reproduce a failure by hand. |

Each run writes to `artifacts/corpus-output/<name>/` (cleared at the start of the run):

- `NN-<command>.json`: the command's envelope, exactly as printed.
- `NN-<command>.stderr.txt`: its progress events and errors.
- `summary.md`: one row per command with exit code, time, and diagnostics by code.
- `model-scan.json` and `model-rescan.json`: only when a determinism check failed.

In CI: Actions, **Corpus**, *Run workflow*, with `all` or a comma-separated list of names. Each
codebase is its own job, with its own time limit, so one failure or timeout never hides another.
The job summary shows `summary.md`; the artifact `corpus-<name>` has everything above plus the
test results. The pinned commit is cached by commit, so only the first run fetches it.

Budget per codebase: the time in `codebases.json`, a few GB of disk (the checkout, its build, and
the restored packages), and network access to the repository and the codebase's NuGet feeds.

## What every codebase gets: the standard sweep

`CorpusRun.SweepAsync` runs these, in order, on a fresh checkout:

| Command | Notes |
|---|---|
| `doctor --fix --apply --yes` | Adds the compile-only block, as a user following `doctor` would. |
| `scan` | Builds the solution. A failed build is fine (exit 1); a crash is not. |
| `scan --no-build` | Its model must match `scan`'s apart from `createdAt`: Offramp's determinism rule, on real code. |
| `scan`, again | Its model must match too: a second build finishes its compilations in another order, which `scan --no-build` cannot show. |
| `graph --format json`, `plan`, `report --format json` | |
| `deps audit`, `deps resolve-dlls`, `redirects sync --prune` | All dry runs. |
| `audit api`, `audit behavior`, `audit dead-code`, `csproj modernize --all` | Optional: `SweepAsync("csproj modernize")` leaves that one out. `csproj modernize` is a dry run that builds each conversion. |

Every command, in the sweep or run with `RunAsync`, must:

- finish within the command time limit (the codebase's time less 20 minutes);
- print an envelope that matches its schema in `schemas/v1/`;
- exit 0 or 1 (2 is a usage error, 3 an environment failure or crash);
- report no internal error (`OFR0099`).

Only read-only and dry-run commands belong in the sweep, because it runs on every codebase. A
command that applies changes (`move apply`, `codemod run --apply`) goes in the codebase's own
test, after the sweep, when its field test exercised it.

## Adding a codebase

Do this after the field test's fixes are merged or on the same branch, so the test passes on the
branch that adds it.

1. **Pin the commit.** Use the full 40-character commit, never a branch or a tag (tags can be
   moved). For a tag:

   ```bash
   git ls-remote https://github.com/owner/repo.git 'refs/tags/v1.2.3*'
   ```

   For an annotated tag, take the line ending in `^{}`; that is the commit. Use the commit the
   field test ran on.

2. **Add an entry to `codebases.json`.**

   | Field | Meaning |
   |---|---|
   | `name` | Short, lower case (`dnn`, `nhibernate`): the `Codebase` trait, the `OFFRAMP_CORPUS` selector, the CI job name. |
   | `title` | For messages: "NHibernate 5.3.0". |
   | `repository` | The `https://` git URL. |
   | `commit` | The 40-character commit from step 1. |
   | `ref` | The tag it came from, for people; never used to fetch. |
   | `solution` | The solution `offramp.yml` names, relative to the repository root. |
   | `timeoutMinutes` | The CI job's limit, including building Offramp (about 5 minutes). Time a local run of the whole test and double it. |
   | `fieldTest` | Path of the field-test report, `docs/field-tests/...md`. |

3. **Write the test class** in `Codebases/<Name>Tests.cs`:

   ```csharp
   using Offramp.Corpus.Tests.Harness;

   namespace Offramp.Corpus.Tests.Codebases;

   /// <summary>NHibernate 5.3.0 (docs/field-tests/2026-10-nhibernate-5.3.0.md): what it is, in one sentence.</summary>
   [Trait("Category", "Corpus")]
   [Trait("Codebase", "nhibernate")]
   public sealed class NHibernateTests
   {
       // At most the codebase's timeoutMinutes less 15 (ManifestTests checks).
       [Fact(Timeout = 60 * 60 * 1000)]
       public async Task The_field_test_findings_stay_fixed()
       {
           await using var corpus = await CorpusRun.OpenAsync("nhibernate");

           // Harness adjustments go here, each with its reason (see below).

           var sweep = await corpus.SweepAsync();

           // Assertions from the field test, each naming its finding (see below).
       }
   }
   ```

   `OpenAsync` skips the test unless it was asked for, checks the codebase out, and writes an
   `offramp.yml` naming the solution. `corpus.Repository` is the checkout (`Write`, `Read`,
   `Path`); `corpus.RunAsync(schema, args...)` runs any other command with the same checks.

4. **Keep harness adjustments to what a user would have to do anyway,** and say why in a comment
   and in the field-test report. Examples: letting `global.json` roll forward to the SDK the tests
   run on, adding a feed the codebase's own build instructions add. Never work around an Offramp
   bug in the harness: that hides exactly what the corpus is for. If a finding is still open,
   leave it out of the assertions and list it under "Still open" in the report.

   When the codebase's own problems stop most of its build (on DotNetNuke, `XCOPY` build targets
   and paths in the wrong letter case leave 55 of 65 projects partial on Linux), the sweep still
   tests the fresh-checkout experience, but the audits see little. Applying the fixes Offramp's
   diagnostics prescribe is then a fair adjustment, since a user would do the same: run
   `corpus.RunAsync("scan", "scan")`, apply the fixes it names, then call `SweepAsync()`. Assert on
   the first scan's diagnostics too, so the test still proves Offramp named the problems.

5. **Choose assertions that stay true until Offramp regresses.**

   Do:
   - Pin each finding the field test fixed, with its number in a comment (`// P0 #2`). The
     assertion should fail if the fix were reverted: check that it does, by reverting the fix
     locally once.
   - Assert invariants with margins: "fewer than 20 blockers", "at least 70 packages",
     "`X` is `blocked`", "no `OFR3001` finding for `System.Convert`".
   - Assert what Offramp names about the codebase's own problems (a diagnostic code on a project),
     since that is the fix for a problem Offramp cannot solve.
   - Guard OS-dependent expectations: letter case matters on Linux only; Windows has the .NET
     Framework targeting packs; `cmd.exe` commands fail everywhere else.

   Do not:
   - Assert exact counts of findings, lines, or diagnostics: every new rule or SDK changes them.
   - Snapshot whole outputs with Verify: they are megabytes and change with every release.
   - Assert anything a feed decides later, such as the latest version of a package or an
     "upgrade" count. The commit is pinned; nuget.org is not.
   - Assert on times.

6. **Check it is reliable before committing.**

   ```bash
   rm -rf tests/.cache/corpus/<name>              # from an empty cache: the fetch path
   OFFRAMP_CORPUS=<name> dotnet test tests/Offramp.Corpus.Tests --no-build --filter "Category=Corpus&Codebase=<name>"
   OFFRAMP_CORPUS=<name> dotnet test tests/Offramp.Corpus.Tests --no-build --filter "Category=Corpus&Codebase=<name>"   # from the cache
   dotnet test tests/Offramp.Corpus.Tests         # without OFFRAMP_CORPUS: ManifestTests pass, the corpus test skips
   ```

   Both corpus runs must pass. Look at `summary.md`: every command should be there with the exit
   code you expect.

7. **Update the documents:** a `CHANGELOG.md` entry under *Added*, the corpus list in
   `docs/spec/04-testing-and-fixtures.md`, and the field-test report's status section, noting that
   the codebase is in the corpus.

## How the harness keeps runs reliable

| Risk | What the harness does |
|---|---|
| The code under test changes | A pinned commit, checked with `git rev-parse HEAD` after every checkout. |
| The network fails while fetching | The fetch is retried after 10, 30, and 90 seconds, and the message says it was the fetch. |
| An interrupted fetch leaves a broken cache | The fetch goes to `<name>.partial` and replaces the cache only when complete; a marker in `.git` records the commit. |
| One run's leftovers change the next | Each run clones the cache afresh: no `bin`, `obj`, `packages`, or `.offramp` from before. |
| Two corpus tests compete for the machine | Tests in this assembly never run in parallel. |
| A command hangs | Each command has a limit below the test's, which is below the job's, so the failure names the command instead of the job being killed. |
| A failure cannot be understood from CI | Every command's output is saved and uploaded; `summary.md` goes into the job summary. |
| A broken entry is found an hour in | `ManifestTests` run on every CI build: commit format, field-test file, one test class per entry, timeouts. |

## When a corpus test fails

1. Read `summary.md`, then the failing command's `.json` (the diagnostics) and `.stderr.txt`.
2. Reproduce: run the test with `OFFRAMP_CORPUS_KEEP=1`, go to the kept checkout, and run the
   command again: `dotnet <repo>/src/Offramp.Cli/bin/Debug/net10.0/offramp.dll <command> --json`.
3. A message about fetching or a NuGet feed is the network; run once more. Anything else is a
   regression or a new finding: fix it with a fixture test in the regular suite, as in a field
   test, and keep the corpus assertion.
4. If Offramp's output changed on purpose (a better message, a new diagnostic), update the
   assertion in the same change and say why in the commit.
