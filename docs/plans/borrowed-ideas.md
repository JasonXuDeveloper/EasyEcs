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

## 5. Results

### Item 2: timer wheel (bar not met: dropped)

`TimerWheelBenchmark` (`benchmarks/EasyEcs.Benchmark/Locality/Timers.cs`, checked in setup and in `LocalityTests`:
every version does the same work on the same ticks over 400 ticks). 100,000 entities, periods of 50 to 150 ticks
(1 % wake per tick), per tick, one thread. BenchmarkDotNet default job (2 launches x 8 iterations), two rounds;
Xeon 2.10 GHz VM, 4 cores, AVX-512, .NET 10.0.13.

| Version | Mean r1 | Ratio r1 | Mean r2 | Ratio r2 | Allocated |
|---|---|---|---|---|---|
| Scan: `[ForEach]` comparing `Until` with the clock (the generator's vector loop; baseline) | 21.50 us | 1.00 | 20.90 us | 1.00 | 0 B |
| The same method row by row (`SimdMode.Off`) | 73.94 us | 3.45 | 62.32 us | 2.98 | 0 B |
| Countdown: `[ForEach]` decrementing every row (vector loop) | 21.33 us | 0.99 | 21.25 us | 1.02 | 0 B |
| Wheel: 256 buckets of ids, the woken read and rescheduled through `GetEntity` / `Get` | 25.31 us | 1.18 | 26.37 us | 1.26 | 0 B |
| Wheel + tag: the woken tagged (a presence flip), a `[ForEach]` over the tag, `RemoveAll` | 77.43 us | 3.61 | 80.38 us | 3.85 | 0 B |

Load average (1 / 5 / 15 min) at the start of each run: r1 2.09 / 1.82 / 1.72, r2 3.62 / 2.51 / 1.99.

Verdict: the bar (3x against the scan) is not met; the wheel is 1.2x **slower**. The scan the generator writes reads
two ints per row in vectors of 8: 100,000 rows cost 21 us, 0.2 ns a row, while the wheel pays about 25 ns per woken
entity (three record lookups by id, a scattered row, the insert into the next bucket), so at 1,000 woken per tick the
two meet. The wheel wins only against the scalar scan (2.9x to 3.5x) or with far fewer wakes per tick (break-even near
0.8 % at this cost per woken entity, so it pays only for waits much longer than 100 ticks, or rows wider than the
vector loop can take). The tag form is worse than both: the presence flips and the pass over the tag's words cost more
than the scan. What would change the verdict: a wait the vector loop cannot take (EECS055 reasons in the waiting
check), or many more waiting entities per wake (1M waiting, 0.1 % waking), measured against the vector scan.
