# dual-target

- `Shared`: `net48;net10.0` library with `#if NETFRAMEWORK` blocks, a
  `System.Web` reference on the net48 side only, and Newtonsoft.Json.
- `Contracts`: `netstandard2.0` library.
- `Tool`: `net10.0` console referencing both.

Exercised by: `scan`, including the cross-OS check that a model produced from a
compiler log captured on Windows equals the one produced natively; later
`graph`, `move plan` (destination compatibility), and `ifdef`.
