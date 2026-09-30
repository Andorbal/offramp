# 0057. A target is a framework, and a .NET major version is its shorthand

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/00-architecture.md` (target framework handling), `docs/spec/03-configuration.md`
  (`target`), `docs/spec/01-cli-conventions.md` (`--target`), `docs/spec/commands/audit.md` (`audit api`),
  `docs/spec/commands/guide.md` (`port`), `docs/spec/commands/workspace.md` (`report`, `doctor`)

## Context

`--target`, `target:`, and `OFFRAMP_TARGET` took a .NET major version only (`10` meant `net10.0`). A library
that must keep serving .NET Framework users targets .NET Standard 2.0, alone or next to its .NET Framework
target, and that was not expressible. On NHibernate 4.1.2 (field test P1 #7) the library converted to
`net48;netstandard2.0` fails for `netstandard2.0` with 71 errors, about 50 of them Reflection.Emit
(`ILGenerator`, `TypeBuilder`, ...), which `net10.0` has: `audit api` against `net10.0` reported none of them.
The guide's port step proposed `net40;net10.0` for it, and `report` said "applications 0" and nothing about
the library. The spec had to say how a framework and the integer relate, what `-windows` means, what code that
has to run does under .NET Standard, and what the defaults are.

## Decision

A target is one of:

| Written | Moniker | Meaning |
|---|---|---|
| `N` (an integer, 5 or more) | `netN.0` | shorthand for `netN.0` |
| `netN.0` | `netN.0` | a modern .NET, on any operating system |
| `netN.0-windows` | `netN.0-windows` | a modern .NET on Windows only |
| `netstandard2.0`, `netstandard2.1` | the same | .NET Standard, for libraries that serve .NET Framework and modern .NET from one build |

Monikers are lowercase, as NuGet writes them. Nothing else is a target: `net48` is where the migration starts,
`netcoreapp3.1` and `netstandard1.x` are older than anything worth aiming at, and a platform other than
Windows has no .NET Framework code to come from. The default stays `10`. The value is written back as given
(`effectiveConfig.target` is `10` for 10 and `"netstandard2.0"` for that), and the envelope's `target` is always
the moniker.

What a project moves to under the target (`audit api` compiles it against that, `seams` decides from it whether
Windows interop taints, and the guide's port step adds it):

- `netN.0`: `netN.0-windows` for a project that uses Windows Forms or WPF (they run nowhere else), `netN.0` for
  the others. This is what the integer always meant.
- `netN.0-windows`: `netN.0-windows` for every project. Windows-only APIs are not findings (`OFR3002` does not
  run; `OFR3003` does), and COM and Windows P/Invoke do not taint in `seams`.
- `netstandard2.x`: the standard for a library. Code that runs (an application or a test project) cannot run on
  .NET Standard, so it moves to `net10.0`, the default, or `net10.0-windows` with Windows Forms or WPF; `audit
  api` says so with `OFR3017` (info). .NET Standard has no `-windows` form and no shared frameworks, so no
  project gets ASP.NET Core or the Windows desktop framework under it. Rule severities that depend on the .NET
  version (`OFR3201` below .NET 9) use .NET 10, because a .NET Standard library runs on every .NET, the newest
  included. `service` and `web scaffold` create .NET 10 projects, and `doctor` checks that the SDK builds
  `net10.0`. .NET Standard's reference assemblies come from the SDK's usual reference (the
  `NETStandard.Library` package for 2.0, the `NETStandard.Library.Ref` pack for 2.1), resolved by the same
  scratch project as any target.

Independently of the target, the guide's port step gives a library that other code uses (ADR 0041's shipped
rule: listed in `deadCode.externalConsumers`, packable, packed by a `.nuspec`, or used by no application in
the solution) `netstandard2.0` next to its .NET Framework target (`net40;netstandard2.0`), with a note that
names the audit to run (`audit api --target netstandard2.0`), unless it uses Windows Forms or WPF or the target
is `-windows` or already .NET Standard. `report` counts these libraries (`libraries`, `librariesDone`) and lists
them with what is left in their closure; when the workspace has no application, every rendering talks about
them instead of "0 of 0 applications".

## Alternatives considered

- A separate library target (`targets: { applications: 10, libraries: netstandard2.0 }`): two settings where
  one covers every case above, and every command would have to say which one it used.
- Treating `netstandard2.0` as the target of every project, applications included: an application cannot run
  on it, the ASP.NET Core and Windows desktop frameworks cannot be referenced from it, and every web and desktop
  project would fail reference resolution (`OFR3010`) instead of being audited.
- Rejecting `-windows`: a Windows desktop project already moves there by itself, but a server application that
  will keep running on Windows then gets `OFR3002` for every registry or event-log call, which is noise for it.
- Proposing `netstandard2.0` for shipped libraries only when the target is .NET Standard: the default target is
  `10`, and a library with .NET Framework consumers is exactly the project that needs `netstandard2.0` whether
  or not the user thought to change the target. `audit api` stays on the configured target, so the note names
  the audit to run.
- Case-insensitive monikers: `NetStandard2.0` is valid in a project file, but one spelling keeps the schema, the
  flag, and the environment variable the same, and the error says what to write.

## Consequences

A library is audited against what it will target, and the Reflection.Emit and SqlClient uses that stop
NHibernate's `netstandard2.0` build are findings. Code that reads `config.Target` gets a `ModernTarget`
(`Moniker`, `Major`, `IsStandard`, `Windows`, `RuntimeMajor`, `For(project)`) instead of an integer. A
repository with applications and a shipped library now sees `netstandard2.0` proposed for that library while
`audit api` runs against `net10.0`; the per-project note bridges the two. .NET Standard 2.1 is accepted but
never proposed: .NET Framework cannot use it.
