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
