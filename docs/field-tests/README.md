# Field tests

A field test runs Offramp on a real, large .NET Framework codebase the way a new user would, to
find what the fixtures do not show. The first one, on DotNetNuke 9.13.10
(`2026-09-dnn-platform-9.13.10.md`), found a dozen wrong or dangerous results that every fixture
passed. Almost all of them were general, not DotNetNuke's: `packages.config`, legacy projects,
Web Forms, and Linux file systems are the norm in real codebases and rare in fixtures.

A field test has three parts: explore and write the report, fix what is general, then add the
codebase to the corpus tests so the fixes stay fixed. The exploration is open; the report and the
corpus test follow the shape below, so the next person can compare codebases and rerun them.

## 1. Explore

Pick a codebase that is large, real, open source, and still on .NET Framework (or mostly), at a
tag. Record the repository, the tag, and the commit it points to: that commit is what the corpus
test will pin.

Work outside this repository (a scratch folder, not `tests/.cache/corpus/`, which the corpus
harness owns), and never commit the codebase or its build output.

Then use Offramp as a new user would: follow `README.md` and `docs/compiling-on-macos.md`, run
`doctor`, `init`, `scan`, and then every command that applies to the codebase. You are free to go
where the results lead. Some habits made the DotNetNuke test useful:

- **Check results against the source.** A plausible number is not a correct one. Open the files
  a finding points at; ask whether a developer on that codebase would agree.
- **Trace surprising results to Offramp's code** before calling them bugs, and name the class or
  rule at fault in the report.
- **Time every command** and note its exit code and headline numbers: the command log.
- **Record every workaround exactly** (the property, the file, the symlink). The list of
  workarounds is the specification of what to automate.
- **Ask "would another codebase hit this?"** for every finding. Most will. Say which are
  specific to this codebase and why.
- **Measure before and after.** Keep the numbers a finding is about (findings, blockers, partial
  projects) so the fix can be measured on the same codebase.

## 2. Write the report

Name it `docs/field-tests/YYYY-MM-<codebase>-<version>.md` and follow the DotNetNuke report's
sections:

1. **Header:** codebase and tag (with commit), its size and shape (projects, frameworks,
   `packages.config`, legacy projects, languages), the machine and SDK, the Offramp commit, and
   the method.
2. **Summary:** what happened out of the box, and the most serious findings in a few lines.
3. **What worked well:** so it is not broken later.
4. **Findings, ranked:** P0 for wrong or dangerous results, P1 for gaps that block real use, P2
   for polish. Each finding has what happened, the evidence (files, numbers, root cause), and a
   proposed fix. Number them: commits, tests, and the corpus test refer to the numbers.
5. **Command log:** every command, its time, and its result.
6. **Appendix:** exactly what it took to get the codebase through `scan`, as a reproducible list.

Commit the report on its own (`docs: field-test report ...`) before the fixes, so the fixes can be
compared against it.

## 3. Fix what is general

For each general finding, follow `CLAUDE.md`: one commit per fix group, a test in the regular
suite that fails without the fix (a small fixture reproducing the pattern, or a unit test; the
corpus test is a backstop, never the only test), spec and diagnostic docs, an ADR for any
ambiguity resolved, and `CHANGELOG.md`. Then run the command on the codebase again and record the
new numbers.

When the fixes are done, add a **Status after the fixes** section near the top of the report: one
row per finding with the commit that fixed it and the numbers on the codebase now, and a "Still
open" list for what was not fixed. Be exact: measure the claim on a fresh checkout, the way the
corpus test will.

## 4. Add the codebase to the corpus

Follow `tests/Offramp.Corpus.Tests/README.md`: pin the commit in `codebases.json`, write the test
class with assertions for the fixed findings, and run it twice, once from an empty cache. From
then on, anyone can run it with the **Corpus** workflow.

## Field tests so far

| Report | Codebase | Corpus name |
|---|---|---|
| [2026-09-dnn-platform-9.13.10.md](2026-09-dnn-platform-9.13.10.md) | DotNetNuke Platform 9.13.10: Web Forms CMS, 71 projects, 64 legacy on `packages.config` | `dnn` |
| [2026-09-nhibernate-4.1.2.md](2026-09-nhibernate-4.1.2.md) | NHibernate 4.1.2: an ORM library shipped on NuGet, 5 legacy net40 projects with DLLs checked in, one in Visual Basic, built by NAnt | `nhibernate` |
| [2026-09-smartstorenet-4.2.0.md](2026-09-smartstorenet-4.2.0.md) | SmartStoreNET 4.2.0: an MVC 5 e-commerce site with 12 plugins that build into it, 25 legacy projects on `packages.config` | `smartstore` |
| [2026-09-open-live-writer-0.6.3.md](2026-09-open-live-writer-0.6.3.md) | Open Live Writer 0.6.3 (master): a WinForms desktop application with COM and P/Invoke interop, 28 legacy projects and a native one | `olw` |
