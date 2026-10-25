# Owner review queue

Proposals an agent judged not worth doing (or not yet), kept for the owner to review. Each entry: what was proposed,
by whom or which round, why it was not done, and what would change the verdict. The owner moves an entry to the
roadmap (do it), to `docs/content/decisions.mdx` (settled, with the reason), or deletes it. Done or decided entries
leave this file; it is not published on the site.

## NUMA placement and hybrid cores (survey of OS / HPC / OpenMP ideas, `docs/plans/borrowed-ideas.md` section 3)

- **Proposed:** first-touch placement of columns by the thread that walks them, one world per NUMA node, helpers
  kept on their node (OpenMP `proc_bind`, HyPer's morsels); checks on P / E cores and SMT siblings.
- **Why not now:** the machine has one NUMA node, 4 cores, no SMT, so nothing can be measured; node placement needs
  dedicated threads, which D22 declined for the thread pool.
- **What would change it:** a two-socket (or hybrid-core) machine to run `ParallelForEachBenchmark` and
  `BattleServerBenchmark` on, showing a loss that placement removes.
