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

### Item 1: prefetching lookup chases (bar met at 16M only: not built; owner's call)

The prototype (`benchmarks/EasyEcs.Benchmark/Locality/Chase.cs`, checked by `LocalityTests.ChaseVersionsAgree`: bit
for bit against the plain method, dead and recycled targets, runs shorter than the distance, 0 B) is what the
generator would write for the plain method `Hunt(in Position, in Prey, ref Heading, Lookup<Position>)` with every
hunter chasing a random hunter: a batch job over each run of rows calling the method row by row, which at row i
prefetches the record of row i + 2k (`Lookup<T>.Prefetch`, stage 1) and the row of T of row i + k
(`Lookup<T>.PrefetchRow`: reads the record stage 1 brought in, then prefetches `column + slot * size`; stage 2, as
AMAC: the row's address is known only once the record is in cache); and D131's vector loop by hand (Resolve, two
gathers) prefetching the 8 rows 2k and k past its vector the same way. The helpers are in the core
(`LookupLanes.PrefetchRecord` / `PrefetchRow`, behind `Sse.IsSupported`: `PREFETCHT0`). **ARM64:** .NET 10 has no
prefetch intrinsic for it but SVE's (`Sve.Prefetch8Bit` ... and `GatherPrefetch*`, which the project does not use);
`ArmBase` and `AdvSimd` have none (checked in the 10.0.13 reference assembly). There the helpers compile to nothing
and the loops run as without them (a plain load as a "touch" would retire only when the miss returns; not measurable
here, left to the owner's ARM64 numbers).

Distance screen (`LookupChaseDistanceBenchmark`, one process, 3 warmups + 5 iterations of 150 ms; the vector loop's
distance then counted vectors of 8): the row job at k = 2 ... 64 rows, 1M / 16M entities, ratio to the job without
prefetch: 1.08 / 0.97 (k 2), 0.90 / 0.79 (4), 0.74 / 0.72 (8), 0.85 / 0.64 (16), 0.83 / 0.62 (32), 0.91 / 0.71
(64); records only (stage 1 alone) 0.85-0.94 / 0.81-0.87, so stage 2 is worth about as much again; the vector loop
best at 2-4 vectors (16-32 rows). k = 8 and 16 went to the bar check.

Bar check (`LookupChaseBenchmark`, default job, two rounds, one frozen snapshot holding both forms; load average at
the start / end: r1 1.39 2.28 2.20 / 1.20 1.20 1.45, r2 1.75 3.10 2.39 / 1.17 1.26 1.52). Means in ms (1k in us);
"gen" is the generated job of the plain method (`ChaseSerial` row by row, `ChaseGathered` with `SimdMode.Require`),
"hand" the same job written by hand without prefetch, "pf" with prefetch. All 0 B.

| Entities | k | gen rows r1 / r2 | hand rows | pf rows | gen gathers | hand gathers | pf gathers |
|---|---|---|---|---|---|---|---|
| 1k | 8 | 3.104 / 3.028 us | 3.180 / 3.144 | 5.778 / 5.648 | 2.455 / 2.435 | 2.912 / 3.034 | 5.739 / 5.657 |
| 1k | 16 | 2.956 / 3.075 us | 3.074 / 3.150 | 5.992 / 5.697 | 2.653 / 2.393 | 2.981 / 2.928 | 5.651 / 5.627 |
| 1M | 8 | 15.16 / 16.51 | 17.89 / 17.67 | 14.54 / 15.20 | 14.14 / 14.06 | 15.46 / 15.07 | 11.84 / 11.54 |
| 1M | 16 | 17.21 / 16.19 | 16.99 / 17.40 | 14.84 / 15.56 | 14.48 / 14.07 | 14.61 / 15.89 | 13.47 / 11.87 |
| 16M | 8 | 908.8 / 875.0 | 1003.4 / 1032.4 | 674.2 / 680.9 | 789.1 / 770.3 | 782.5 / 775.1 | 603.2 / 568.3 |
| 16M | 16 | 891.6 / 880.9 | 964.1 / 972.5 | 672.6 / 681.8 | 781.3 / 750.8 | 801.1 / 771.0 | 593.5 / 562.2 |

Speed-up of the prefetching form over the generated one (r1 / r2), k = 8: rows 1.04 / 1.09 at 1M, 1.35 / 1.29 at 16M;
gathers 1.19 / 1.22 at 1M, 1.31 / 1.36 at 16M. Over the same job by hand: rows 1.23 / 1.16 and 1.49 / 1.52, gathers
1.31 / 1.31 and 1.30 / 1.36. At 1k the prefetching forms take 1.86x (rows) and 2.3x (gathers) the generated time:
two prefetches per row (the bound tests, the record and table reads of stage 2) with nothing to hide.

Verdict: the bar (1.3x at 1M and 16M, nothing slower at 1k) is **not met**: met at 16M (the vector loop 1.31-1.36x,
rows 1.29-1.35x), short at 1M (1.04-1.22x against the generated jobs), and 1.9-2.3x slower at 1k. On this machine
the 1M world (16 MiB of records, 8 MiB per column) sits in the 260 MiB last-level cache, so its misses are L3 hits
with less latency to hide than at 16M; the generated row-by-row job is also 5-15 % faster than the hand-written batch
job it would become, which eats part of the gain. Nothing reaches the generated code; the core helpers
(`Lookup<T>.Prefetch` / `PrefetchRow`, hidden, documented as generated-code helpers) stay with the prototype so the
measurement can be repeated on the owner's machines (a 32 MiB last-level cache puts the 1M world past it).

What would change the verdict, and the generator change it would need (after WP0b; Kernel.Lookups.cs and
SystemGenerator.cs): a size gate read once per pass (prefetch only when the world's records are past the last-level
cache, e.g. `Meta.Length >= 4M` records, or the target table's column past it), so 1k and 1M cost nothing and 16M
gains 1.3x; then (1) a method reading `lookup[x.F]` / `Read` / `Has` where `x` is an `in` component (the id lanes
cannot change during the row) gets a run job instead of the per-row job: `for (i)`: `if (i + 2k < n)
lookup.Prefetch(col[i + 2k].F); if (i + k < n) lookup.PrefetchRow(col[i + k].F);` then the method, with k = 8 rows
(16 for the vector loop, which prefetches the 8 rows of the vector 2k and k ahead before its `Resolve`); (2) the
same for relation targets (a `DynamicBuffer` of links is a second chase, not measured). Measured again with the gate
on `LookupChaseBenchmark` (1k, 1M, 16M) on two machines.

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
