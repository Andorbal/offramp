# Offramp for VS Code

Keeps new code in .NET Framework projects ready for .NET 8 and 10, so nobody has to migrate it
later.

When you write code in a project that still targets only .NET Framework, Offramp:

- **flags APIs modern .NET does not have** (`HttpContext.Current`, `BinaryFormatter`,
  `AppDomain.CreateDomain`, WCF service hosts, ...) on the lines you added or changed, with
  what to use instead;
- **notices a new class that needs nothing from .NET Framework** and offers to move it to the
  project's portable counterpart (a `netstandard2.0` or multi-targeted project it can
  reference), where it will never need migrating;
- puts a **Move to …** lens on existing classes that can move there as they are.

A move renames the file (a staged `git mv` once git tracks it): its contents, namespace
included, do not change, the project keeps using it through its reference, and the affected
projects are built before the move is kept. Nothing is committed, and
`offramp move rollback --journal …` undoes it.

It stays out of the way: only lines that differ from the branch's merge base with the default
branch count as new, and nothing is shown in modern .NET projects.

## When it is on

In repositories with an `.offramp` folder (a migration is under way) it is on by default;
elsewhere it is off. Change that with `offramp.enabled` (`auto`, `on`, `off`), per user or per
workspace, or with **Offramp: Enable in this workspace** / **Disable in this workspace**.

## Requirements

- The `offramp` .NET tool, 0.15.0 or newer: the repository's local tool
  (`.config/dotnet-tools.json`) if it has one, else `dotnet tool install -g offramp`, or set
  `offramp.server.path`.
- A workspace model: Offramp asks to run `offramp scan` (which builds the solution once) when
  there is none. Source edits never need another scan.
- The C# extension, for everything else about C#.

## Where code can move

Each .NET Framework project's counterparts come from the project map, else from the portable
projects it already references. Map projects in `offramp.yml` for the whole team:

```yaml
projectMap:
  - from: Foo            # a project name or path; * and ? match
    to: ModernF          # a name or path; {name} is the matched project's name
  - from: "Contoso.*"
    to: "{name}.Portable"
```

or in your settings (`offramp.projectMap`), which are consulted first.

## Settings

| Setting | Default | |
|---|---|---|
| `offramp.enabled` | `auto` | `auto`, `on`, or `off` |
| `offramp.projectMap` | `[]` | project map entries, before `offramp.yml`'s |
| `offramp.newCode.base` | from `offramp.yml` | the git ref new code is compared with, for example `origin/main` |
| `offramp.newCode.scope` | from `offramp.yml` | `lines`, `files`, or `all` |
| `offramp.implicitCounterparts` | from `offramp.yml` | suggest referenced portable projects without a map entry |
| `offramp.codeLens` | `true` | show move lenses |
| `offramp.server.path` | | the `offramp` executable or `offramp.dll` |

The same checks run on the command line with `offramp ide check`, which also works as a gate in
CI: `offramp ide check --base origin/main --fail-on error`.

See the [specification](https://github.com/Andorbal/offramp/blob/main/docs/spec/commands/ide.md).
