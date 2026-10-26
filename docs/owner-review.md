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

## Batch probes of ValueIndex / UniqueIndex (locality phase 0, `locality.md` 4.4)

- **Proposed:** probing many keys at once, one per lane (Polychroniou SIGMOD 2015).
- **Why not:** `BatchProbeBenchmark`, two rounds: AVX2 gathers 1.28-1.55x slower than one key at a time at 1k and
  100k keys, AVX-512 lane refill 3.1-3.35x slower. Scalar probes already overlap on the out-of-order core, and a
  100k-key table (2 MB) stays in cache.
- **What would change it:** tables far past the last-level cache (16M keys), measured against scalar group
  prefetching (`borrowed-ideas.md` 1) rather than gathers.

## Timer wheel for waiting entities (`borrowed-ideas.md` 2)

- **Proposed:** buckets by wake tick instead of a pass over every countdown.
- **Why not:** `TimerWheelBenchmark`, 100k waiting, 1 % waking per tick, two rounds: the wheel is 1.18-1.26x slower
  than the vector scan the generator writes (21 us per tick, 0.2 ns a row against about 25 ns per woken entity);
  with a tag 3.6-3.9x slower. It only beats the scan row by row.
- **What would change it:** waits the vector loop cannot take (EECS055 reasons), or far fewer wakes per waiting
  entity (1M waiting, 0.1 % waking), measured against the vector scan.

## Nearest search on a spread map (`locality.md` 4.2)

- **Proposed:** the block search replacing the zombie's ring walk everywhere.
- **Why not yet:** with players spread and the sort paid every tick, 1.06-1.31x (bar 1.5x); it meets the bar for the
  packed start (1.53-1.65x) and for `NearestPlayer` (4.2x with a sort per 64 queries).
- **What would change it:** the sort on L1's cadence (sorted once: 1.25-1.37x spread) and a flat grid of blocks
  searched in rings instead of the prototype's box tree.

