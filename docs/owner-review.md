# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## VoxelServer plan (2026-10-26, `docs/plans/voxel-rewrite.md`)

- **G3, `[Prefab]` with buffer and absent fields**: not done: in VoxelServer it would add lines, not remove them.
  Would change: a sample or user code where prefabs with buffers are declared often enough to save lines.
- **G10, deferred consume for chunk load / save**: not done: loading and saving outside the pass is the lesson of the
  sample (ChunkRead / ChunkSave). Would change: the same shape repeated in user code that is not a lesson.
- **G11, a helper for the steady-state allocation check**: not done: it is test plumbing of one sample (Host/Steady.cs).
  Would change: a second sample or users asking for it.
- **G7, a keyed read of the found entity** (feat-generator-gaps): not built. The VoxelServer pattern is
  `chunks.TryFind(key, out var chunk)` then `viewers.Read(chunk.Id)`: a keyed form (`viewers.ReadByKey<ChunkKey>(key)` or
  `TryFind(key, out ref readonly Viewers v)`) still needs both lookups as parameters (the index lives on one component,
  the value on another) and saves no line; nothing general fits that is not the two calls. Would change: user code
  where the key's own component is the one read (then `Lookup<T>.TryFind(key, out entity)` plus `[entity]` could
  become one call), seen in two places.
- **EECS086 (a total kept by hand) and EECS109 (Emitted<T> walked and cleared by hand) are Info** (feat-generator-gaps):
  as Warnings they fire in the samples, BattleServer.cs and the benchmarks' baselines, which that branch could not edit;
  EECS086 is a Warning with its fix once the singleton already reduces. Would change: the samples moved to
  `[Accumulate]` and `[ForEachRecord]` (VoxelServer rewrite): then both become Warnings (one line each in the analyzer).
- **EECS055's "a bool among N fields" is inherent (Info)**, though the split it names would give a vector loop
  (feat-generator-gaps): fixable, it is a Warning in VoxelServer (Body.OnGround, Exposure.Sunlit). Would change: the
  rewrite splitting those bools out; then pass `true` (fixable) in Kernel.cs.

## Prefetch gates (2026-10-27, feat-prefetch-all, `docs/plans/borrowed-ideas.md` 5, item 1)

- **Loads issued ahead on ARM64 without SVE** (the M4): not emitted: 1.7x slower than plain rows on x86 at 16M.
  Would change: the owner's M4 run of `LookupChaseLoadAheadBenchmark` showing LoadAhead faster than Rows at 16M and
  not slower at 1k (command in `backlog.md` item 6).

## Streaming stores for write-only columns past the cache (2026-10-27, feat-microarch, `microarch.md` M3)

Measured, not built. Non-temporal stores of a write-only column win 1.47x past the last-level cache (64M floats a
column: 49.3 -> 33.4 ms) and tie inside it (`StreamingStoreBenchmark`, prototype `Locality/Streams.cs`). A generated
loop could use them only for a column the method writes without reading, behind a gate on the column's bytes against
`LastLevelCache`. Not done: few passes write a column without reading it, 64M-row tables are rare, and the next pass
reading that column then misses the cache. What would change the verdict: a real world (VoxelServer terrain, a
simulation's output buffers) with write-only columns past the cache.
