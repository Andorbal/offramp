# seams

A net48 library with one part that will never port (Active Directory through
`System.DirectoryServices`) and code around it that should.

- `Accounts/Directory/DirectoryLookup`: the unportable class (`DirectorySearcher`),
  with `FindUser` and `GroupsOf` (data in, data out), `Watch(Action<string>)` (a
  callback: not wire-friendly, OFR4002), and a static `IsAvailable()` (OFR4003).
- `DirectoryCache` (internal, holds a `DirectoryEntry`) and `CachedDirectoryLookup`
  (derives from `DirectoryLookup`): tainted by structure, not by calls.
- `DirectoryEntryInfo`: a POCO the boundary returns; it stays clean.
- `Users/UserService`: takes `DirectoryLookup` through its constructor, so
  `extract interface` retypes it to `IDirectoryLookup`.
- `Auth/LoginHandler`: creates `new DirectoryLookup()` itself (OFR4010) and calls
  the static member.
- `Reports/ReportService`: uses only `DirectoryEntryInfo`.
- `Portal`: an executable that composes the pieces.

Expected: `seams` taints `DirectoryLookup`, `DirectoryCache`, and
`CachedDirectoryLookup`, and finds one seam, `IDirectoryLookup` on
`DirectoryLookup`, an articulation point with callers `UserService` and
`LoginHandler`. After `extract interface --apply` and a new scan, `remote
--skip-member Watch` generates contracts, a client, and a net10.0-windows host
(the implementation compiles for it with Microsoft.Windows.Compatibility) that
build on every OS.
