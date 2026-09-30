# 0051. Recompute the co-move closure after every exclusion

- Status: accepted
- Date: 2026-09-30
- Spec section: `docs/spec/commands/move.md#move-plan`

## Context

`move plan --co-move closure` adds the files a moving file needs to the move, transitively, and
reports them as co-moves (`coMoveOf`). The specification says how the closure grows; it says nothing
about what happens to it when a file is excluded later, by a trial compilation, a cycle, a
dependent, or the namespace rule. The planner removed the files co-moved directly for the excluded
file, silently, and kept their own co-moves. In SmartStoreNET 4.2.0, moving the 10 files of
`SmartStore.Core/Collections` into a new `netstandard2.0` project moved 1 requested file and 40
others, among them a `BinaryFormatter` helper; for 35 of the 41 moves the file named in `coMoveOf`
neither moved nor was excluded. The plan was valid (it compiled) but moved code nobody asked for,
for reasons that no longer held.

## Decision

After every round of exclusions the planner keeps only the candidates reachable from a requested
file through what candidates need: the files they use (unless the destination already depends on
the source, where moved code may use the source), their resource pair, and the other parts of their
partial types. A co-move that another moving file still needs stays, and its `coMoveOf` then names
that file. Every other co-move stays where it is, and the plan lists it in `excluded` with `OFR2113`
(info), whose message and details name the file it was co-moved for. A dropped file that becomes
needed again is added back, so the listing is decided at the end. The namespace rule with `block`
now excludes inside the same loop, so a file that needs a blocked file stays too (`OFR2101`).

## Alternatives considered

- Remove co-moves recursively along `coMoveOf`. `coMoveOf` records only the first file that needed
  a co-move; a file another moving file also needs would be dropped and added back on the next
  round, and a chain would still depend on the order files were visited.
- Drop co-moves without listing them. Agents read `excluded` to learn why a file did not move; a
  file that appeared in an earlier plan and silently vanished is the kind of surprise the field test
  reported.
- List them under their own key. The plan schema's `excluded` already carries a code per file;
  another list would be a contract change for the same information.

## Consequences

A plan's moves are the requested files that can move plus exactly what those need. `excluded`
grows by the dropped co-moves, at info severity, so `--fail-on warning` is unaffected. Each
exclusion round walks the candidates once more; the walk caches each file's needs.
