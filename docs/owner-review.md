# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## Declarations (declaration surface audit, 4.8)

- **Infer `ITracked` from code that reads changes.** Not done: tracking costs a tick column per row and a stamp per
  write, so it must be a choice the user sees on the type; inferring it from a `[Changed<T>]` anywhere in the
  solution would make a component's cost depend on distant code. Would change if: tracking became free when unread.
- **Infer `Mut<T>` from a `ref` parameter written on some rows only.** Not done: the analyzer already reports it
  (EECS053) with a fix, and an inferred `Mut<T>` would change when rows are stamped without the code saying so.
- **Infer `[Consume]` from a method that destroys every row it visits.** Not done: EECS084 reports it with a fix; a
  silent switch to the bulk destroy after the pass changes when the entities disappear (after the pass, not per row).
- **Arity forms of `[With]` / `[Without]` (`[With<A, B>]`).** Not done: 17 of 358 methods combine two or more, and
  stacked single attributes read clearly. `[WithAny<..>]` does get the arity form (one attribute means "any of").
- **`[IndexKey(Unique = true)]` instead of `[UniqueKey]`.** Not done: a unique key changes the write path (claims before
  writes) and the lookup API (`Get` / `TryGet`), so it is a different declaration, not a flag.
- **Auto-assigned `Phase` for systems sharing an `[UpdateInterval]`.** Not done: spreading systems across phases
  changes which tick each runs on, which users rely on for ordering; a default that moves work silently is worse
  than the explicit `Phase`.
- **`ISystemGroupWithAny`.** Not done: no sample or user code needs a group condition beyond what exists.
- **Infer `ChunkRows`.** Deferred: needs the scheduler's autotuner to also try chunks under 1,024 rows, with
  benchmarks showing the inferred size is never worse than the hand-set one.
