# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## EECS116's code fix (feat-tiles-pingpong, 2026-10-28)

Item 11 asked for a rule and a code fix for a hand-written commit that is a plain copy. The rule shipped (EECS116,
Warning, reported at the end of the compilation on the commit method, naming the step that writes the second buffer);
the fix did not. The rewrite is not local: it changes the step method (its by-value `Tile<TCells, ...>` becomes `ref`,
its `Mut<DynamicBuffer<TNext>>` / `ref DynamicBuffer<TNext>` parameter goes and each write into it becomes
`Write(row)`), deletes the commit method (and its system when that empties it, with every `AddSystem<T>`,
`[UpdateAfter<T>]` / `[UpdateBefore<T>]` naming it), and removes the `TNext` buffer type with every
`GetOrAdd<DynamicBuffer<TNext>>` / `SetCount` that sizes it, in other files and projects. A step that also reads TNext
(a second step, a debug view) or writes it on some rows only cannot follow at all. A fix that is offered only when all
of that is mechanical would almost never be offered; one that is offered otherwise breaks code.

What would change the verdict: users porting many hand-written step + commit pairs (VoxelServer's water and the grids
page's heat are ported by hand in the VoxelServer rewrite and the docs pass), or a fix limited to the step method with
the commit left for the user to delete (the compiler then reports the stale writes of TNext).

## The generated ping-pong step is not yet faster than step + commit by hand (feat-tiles-pingpong, 2026-10-28)

Item 11 shipped the `ref Tile` step (Jacobi ping-pong, the second buffer and the swap owned by the pass, sleeping tiles
owned by the pass). It removes the commit system, its copy and the hand-written wake loops, but on the shared VM it
ties step + commit at 1 thread (Blur9 788 against 781 us) and does not beat it at 4 (431 against 373 us in one round,
421 against 439 in another), and in a sparse field (TileActiveBenchmark) it costs 1.7x the hand-written sleeping form
at 1 thread (22.3 against 13.1 us; 15.7 against 15.0 at 4). What was tried and measured is in docs/plans/docs-todos.md
(feat-tiles-pingpong). Left: the fixed cost per pass (the per-color walks over every tile, the serial Next over the
changed tiles) against a hand-written form that visits only the woken tiles through `[Changed<...>]`.

What would change the verdict: a pass that walks only the awake tiles (a woken set per color, filled by Next, in
place of the walk with a sleep test per tile), measured on the reference machine. Until then the VoxelServer rewrite
can port Water to the ref form for its lines, but the docs should not promise a speedup.
