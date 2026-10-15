# 0075. Generate probes for what can be generated, and scan once for it

- Status: accepted
- Date: 2026-10-15
- Spec section: `docs/spec/commands/newtonsoft.md#newtonsoft-tests`

## Context

`newtonsoft tests` on a solution of 82 projects and 2.6 million lines (issues #50, #51, #53, #54)
failed where the specification had not said what should happen:

- **A probe that does not parse** (#50). The trial compilation drops a probe whose method has an
  error (`OFR3735`). A payload with a property declared `@event` was written `{ event = ... }`:
  the parser's recovery put later errors outside every probe method, and an error no probe
  accounts for threw, so the whole command stopped with `OFR0099` and nothing was generated for
  any project.
- **Earlier generated files the trial compilation keeps** (#51). The trial compilation removes the
  recorded files under the generated folder before adding the new ones, matching by the folder's
  absolute path in letter case. On Windows, after a first run, every probe class was defined twice
  (`CS0101`), again `OFR0099`. The compiler log there had been recorded on another machine (the
  repository, `.offramp/` included, was synchronized from a Mac), so its paths matched nothing.
- **A production project without tests** (#53). `OFR3733` was an error, so a run that generated
  probes for every other project exited 1, as a run that generated nothing does.
- **Time** (#54). Each run spent nine minutes before doing anything of its own: the scan loaded
  every project's compilation for the call graph and again for its sites, judged every project
  against the target although `tests` discards the findings, and scanned every project's sites
  under `--project`.

## Decision

- **Write names as C# spells them.** A member, enum member, or method whose name is a keyword is
  written with `@`; one C# cannot spell at all (another language's) is not set, called, or
  chosen. Each probe's code is parsed on its own before the trial compilation: a syntax error drops
  that probe with `OFR3735` ("its code does not parse", with the code before the error).
- **An error no probe accounts for stops that test project, not the run.** `OFR3748` (error)
  names the error, its place in the generated files, and the other file that declares the same
  type when that is the error; nothing is generated or deleted for that test project, its sites are
  uncovered as `doesNotCompile`, and the other test projects are generated as usual.
- **Earlier generated files are found by path, wherever the log was recorded.** A recorded file is
  replaced when it is under the generated folder here (letter case ignored where the file system
  ignores it) or when its folder's path ends with the generated folder's repository-relative path
  (a log recorded in another checkout, or on another OS). Files the user took over are compiled as
  they are on disk.
- **A project nobody asked for is only uncovered.** `OFR3733` is a warning, as the uncovered sites'
  `OFR3731` and `OFR3732` are, and an error for a project named with `--project`. A run exits 1
  only when something went wrong.
- **Scan once, and only what is asked.** One command loads each compilation once and shares it
  (the call graph, each project's sites, the planned probes, the trial compilation). `tests` asks
  the scan for sites and entry points only, not the behavior detectors or the target trial
  compilation, and with `--project` for those projects' sites only; the call graph still spans
  every project, since a caller anywhere can be an entry point. A sample file then matches the
  payload types of the projects asked for (`OFR3737` says so).

## Alternatives considered

- **Keep throwing on an error no probe accounts for.** It is a bug in the generator or a state
  of the test project the user can fix; either way the other test projects should not pay for it.
- **Match earlier generated files by Offramp's header.** It finds them anywhere, including a
  folder an older configuration named, whose namespace differs and which the real build compiles
  next to the new one; the trial compilation should see what the build sees.
- **Reject a compiler log recorded elsewhere.** `scan --binlog` and `--complog` read logs from
  other machines on purpose (ADR 0010); a log's paths are not a reason to fail.
- **A distinct exit code for "generated with gaps".** Every command maps errors to exit 1; gaps are
  what warnings are for, and `--fail-on warning` makes them fail CI where that is wanted.
- **Cache scan results in `.offramp/cache/`.** The key would have to cover the compiler log, the
  model, the configuration, the rule packs, and Offramp's version, and `tests` needs live symbols,
  which no cache can hold. Sharing compilations and doing less work keeps every run computed from
  its inputs. A cache remains possible for `newtonsoft scan`'s result alone.

## Consequences

A second `--format` of the same scan still scans again (`--out FILE` writes the JSON envelope
while the terminal shows the human view, from one scan). The scan's peak memory is lower: it no
longer holds a second copy of each compilation while the call graph holds the first. `newtonsoft
upgrade` still scans for its findings and again for its probes.
