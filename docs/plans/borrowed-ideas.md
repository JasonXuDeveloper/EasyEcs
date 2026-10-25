# Ideas from OS, HPC, CUDA, MPI, OpenMP and databases (internal, not published)

The owner asked what else these fields do that EasyEcs could borrow. This is the survey, checked against the code and
the decision records, so a later session does not re-propose what exists or was measured and dropped. The layout ideas
(GROMACS cluster pairs, Polychroniou's operators) are their own plan: `locality.md`.

## 1. Already in EasyEcs (do not redo)

| Field | Idea | Here |
|---|---|---|
| CUDA | coalesced access: structure of arrays | columns per table, pinned, 64-byte aligned rows |
| CUDA | wavefront regrouping (sort work by branch so a warp does not diverge) | data instead of flags: a tag splits tables, a pass walks one kind of row |
| CUDA / Halide | kernel fusion | pass fusion (`FusionMode`, `ScheduleInfo.FusedWith`) |
| CUDA | graphs (record launches once, replay) | one switch per system set (D103) |
| CUDA | persistent threads | helpers that stay while they pay (D22) |
| CUDA | warp-aggregated atomics, stream compaction by prefix sum | `Emit<T>` per thread then merged; compaction: `locality.md` 3.3 |
| CUDA | shared-memory tiles with halos | `Tile` / `Cell<,>`, halos (push halos on the roadmap) |
| OpenMP | `reduction`, `simd`, `threadprivate`, `task depend` / `nowait` | `Reduce<T>` (D69), vector loops, `Scratch` (D74), dependency scheduling |
| OpenMP / TBB | `schedule(guided)`, `auto_partitioner`, affinity partitioner | measured and dropped (D125, D130); chunks cut by measured cost (D130) |
| MPI | deterministic `Allreduce` order | chunks combined in order, boundaries by row count (D69, D125) |
| MPI | one-sided reads (RMA) | `Lookup<T>` |
| MPI | derived datatypes (send some fields of some rows) | `SaveEntitiesWith`, `ComponentSet` |
| OS | RCU (readers on immutable snapshots, writers copy) | archetype lists copy-on-write, published with `Volatile.Write` |
| OS | per-CPU data | per-thread scratch warmed per runner slot |
| OS | slab allocators, size classes | `BufferStore` pools (D32, D65), `ValueIndex` spares by log2 length |
| OS | huge pages | #164 (NativeMemory 2 MiB) |

## 2. New candidates, each with the bar it must meet (BenchmarkDotNet, two rounds, 0 B)

1. **Hiding the latency of chases (DB: group prefetching, Chen et al. 2004; AMAC, Kocberber et al. VLDB 2015; what a
   GPU does with many warps in flight).** A `Lookup` or relation read whose target is not in cache stalls each row
   for a full memory latency, one after another. Prefetching the targets of row i + k (`Sse.Prefetch0` on the record,
   then on the field) keeps several misses in flight. Scalar and D131's gathers alike. Bar: random targets at 1M
   and 16M rows (`LookupChaseBenchmark`) 1.3x, nothing slower at 1k. After gather-always merges (same files).
2. **Timer wheel for entities that wait (Varghese & Lauck 1987, the Linux timer wheel).** Cooldowns, respawns,
   buffs that end, items that despawn: today a pass reads every entity's countdown every tick. A wheel of buckets
   by wake tick holds the ids, and only the bucket of this tick is visited (a tag added or a method run on them).
   Bar: 100k waiting entities, 1 % waking per tick, 3x against the scan; then the form (a component the generator
   recognizes, or a world API) goes to the naming audit.
3. **Spatially coherent chunks (MPI partitioning by space-filling curve, Zoltan).** Once rows are ordered
   (`locality.md` L1), each thread's chunks are neighbors in space and their lookups stay in its cache. Comes with L1;
   measured there.
4. **Ghost rows between worlds (MPI halo exchange).** Zone servers split a map into worlds; an entity near a border
   must be seen by the neighbor zone. First write it with the current API (`MoveEntities`, a copy of the border
   rows into a read-only ghost table each tick) in the MultiWorld sample; it becomes a feature only if the example
   shows code users would write twice or a cost the core can remove.
5. **Non-temporal stores for large copies (HPC streaming stores).** `Capture` / `Restore`, unmanaged saves and column
   resizes write more than the cache holds; stores that skip the cache save the read-for-ownership. Bar 1.2x at the
   sizes past the last-level cache, nothing slower below; low priority.
6. **Roofline as a measuring tool (Williams et al. 2009).** Benchmarks state bytes and operations per row so a result
   says whether a pass is bound by memory or by compute, which says whether fusion, narrower lanes or the vector loop
   is the next step. A method for `audit-perf`, not a feature.

## 3. Cannot be measured on this machine (4 cores, one NUMA node, no SMT, a VM)

- **NUMA:** first-touch placement of columns by the thread that will walk them, one world per node, helpers kept on
  their node (OpenMP `proc_bind` / `OMP_PLACES`, morsel-driven parallelism of HyPer, Leis et al. SIGMOD 2014). Needs
  dedicated threads, which D22 declined for the thread pool; worth a look only on a two-socket machine.
- **Hybrid cores (P / E) and SMT siblings:** D130's measured chunks already give slower threads fewer chunks; a test
  on such a machine would say whether anything is left.

Both go to `docs/owner-review.md` until such a machine is available.

## 4. Order

1 after gather-always; 2 and 4 any time an agent is free (benchmark first); 3 with `locality.md` L1; 5 and 6 low.
