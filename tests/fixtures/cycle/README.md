# cycle

Two `net48` libraries that depend on each other: `Alpha` has a
`ProjectReference` to `Beta`, and `Beta` references `Alpha`'s build output
through a `Reference` with a `HintPath`. The build succeeds (Beta does not use
Alpha's types, so the unresolved reference is only warning MSB3245), which is
exactly how such cycles survive in legacy solutions.

Exercised by: `scan`/`graph` cycle detection (`OFR0120`), and later
`move plan` refusal (`OFR2001`).
