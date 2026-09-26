# 0026. Convert projects without try-convert, prove it by compile sets, let the compiler choose what a web scaffold ports, and plan extracts against an in-memory project

- Status: accepted
- Date: 2026-09-26
- Spec sections: `docs/spec/commands/scaffold.md` (`web inventory`, `web scaffold`,
  `csproj modernize`, `config convert`), `docs/spec/commands/move.md#move-extract`

## Context

M12's commands generate or rewrite project files and whole projects. The spec describes
their outputs and leaves open:
- whether `csproj modernize` depends on `try-convert`, and what "identical compile sets"
  compares
- `--hoist` and `--cpm` for `csproj modernize`
- the shape of `config convert`'s options classes, and what "the codemod redirects call
  sites to the shim" means next to M11's `config-manager` codemod
- which actions `web scaffold` ports ("no OFR3001-level findings"), how it proves the new
  project compiles, what happens to handlers, and how the proxy and adapters are set up
- how `move extract` plans into a project that does not exist yet, and how its apply
  and rollback relate to `move apply`
- how the `legacy-csproj` and `mvc5` fixtures build on Linux and macOS runners

## Decision

**csproj modernize.** Offramp converts legacy projects itself. `try-convert` is archived,
changes output between versions, and needs a network install; a converter that is a pure
function of the project file, the model's items, the files on disk, and packages.config
makes the dry run exactly what `--apply` writes and keeps the tool deterministic. The
proof is the real toolchain: the change set is applied in a scratch copy, the projects
are built with a binary log, and the compiler inputs per target (source files relative to
the project, references by file name, embedded resources by manifest name) are compared
with the scan's build. References that arrive only because a package's dependency now
flows transitively through `PackageReference` are reported and allowed; anything else, or
a failed build, is `OFR4303` and blocks `--apply` unless `--accept-diff`. Verification
runs on the dry run too, since it is the command's main output. AssemblyInfo attributes
are removed by M11's `assemblyinfo` codemod rather than a second implementation. ASP.NET
web application projects are not converted (`OFR4304`): the SDK has no System.Web project
support, and `web scaffold` is the path for them. `--hoist` and `--cpm` are deferred:
`deps consolidate --cpm` already centralizes versions with its hazard checks, and hoisting
properties is a separate, cross-project edit with its own review.

Legacy projects had no compiler calls in the workspace model when the recorded
compilation's project path could not be matched by target framework (legacy projects
have no `TargetFramework` property); the model builder now falls back to the project's
single call. Without it, verification compared against nothing.

**config convert.** Options classes are classes with setters in block namespaces, marked
auto-generated, so they compile as C# 7.3 on .NET Framework and bind with
`ConfigurationBinder` on every target; records would not bind on .NET Framework's older
binder. The spec's "codemod that redirects call sites to the shim" is a second codemod,
`config-manager-shim` (OFRM014), not a mode of `config-manager`: the two rewrite
differently (an injected `IConfiguration` versus a static shim) and a codemod has one
rewrite. It targets exactly the sites `config-manager` skips (static members, classes
created with `new`), so running both gives one rewrite per site. The shim codemod is
opt-in and only rewrites when the project declares `ConfigurationManagerShim`.

**web scaffold.** "No OFR3001-level findings" is decided by the compiler: the scaffolded
project, with the legacy files its code needs compiled as links, is compiled in memory
against `Microsoft.AspNetCore.App` and its packages, and an action with an error stays
with the legacy application with the error as its reason; the loop repeats (at most eight
rounds) until the rest compiles. Before that, a semantic check leaves out actions that
render views (views are not ported) or use System.Web members with no counterpart of the
same shape, with a reason a person can act on. Ported code is copied as text with mapped
names replaced, so its formatting survives. Handlers become endpoint stubs that are not
mapped: a stub answering 501 would shadow a working legacy handler, so the proxy keeps
serving the path until someone ports and maps it (`OFR4202`). YARP's catch-all route has
the lowest priority, so every endpoint the new application maps wins and nothing else
changes. The adapters' legacy-side setup is a next step, not an edit: `web scaffold`
never edits the legacy project. `--legacy-url` defaults to the project's IIS URL.

**move extract.** The new project is a `MovePlanner` destination that exists only in
memory: a model entry, its template file, and one compilation per target, from the
source's recorded compilation (framework references only) for a target the source has,
else from the target's reference assemblies (`TargetReferenceResolver`). Every rule of
`move plan` then applies unchanged. The plan's `projectEdits` begin with `createProject`
and `addToSolution`, and `move apply` takes the template's bytes, so one journal creates
the project with the move's edits, edits the solution and the source, and renames the
files; a failed verification or `move rollback` deletes the project file with the rest.
The template takes the source's root namespace, since moved files keep their namespaces
and a different root namespace would change resource manifest names and trip
`move.namespaceMismatch`.

**Fixtures.** `legacy-csproj` and `mvc5` build on every OS: `Directory.Build.props` gives
non-SDK projects `Microsoft.NETFramework.ReferenceAssemblies` as a package, the tests fill
`packages/` from packages.config (copied from the NuGet global packages folder after a
restore), and `WebApplication.targets` is imported only when present. The scaffolded web
application is built and run in the test against a stand-in legacy server on a loopback
port, and requests are checked for which side served them.

## Alternatives considered

- `try-convert` for legacy projects: archived, nondeterministic across versions, and a
  network dependency for a verification-heavy command.
- Comparing project files instead of compile sets: two project files that look alike can
  compile different inputs (globs, imports), which is the failure that matters.
- A `--shim` mode of `config-manager`: two different rewrites behind one codemod ID would
  make "idempotent" and "one diagnostic per skipped site" ambiguous.
- Porting only actions with no `audit api` findings: the audit knows APIs, not ASP.NET
  Core's controller surface; the compiler knows both.
- Mapping handler stubs: they would answer 501 in place of the legacy handler.
- Creating the extracted project, scanning it, then planning: a dry run would have to
  write files and build.

## Consequences

- `csproj modernize` needs a successful build of the original (the scan) and builds the
  converted projects, so it is as slow as a build per project.
- Web scaffolds port conservatively: anything view-based or HttpContext-based stays behind
  the proxy until a person ports it. The reasons are in the result and the controller
  file's header.
- With `--adapters`, every request authenticates against the legacy application (the
  adapters' remote authentication is the default scheme), which needs the legacy side's
  adapter setup before the new application answers.
- `move extract`'s journal is a `move apply` journal; `move apply --resume` does not know
  the template, so an interrupted extract is rolled back rather than resumed.
