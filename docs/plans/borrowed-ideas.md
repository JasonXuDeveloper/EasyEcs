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

### Item 1: prefetching lookup chases (built behind a last-level cache gate, feat-prefetch-gate)

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

#### Built with the size gate (feat-prefetch-gate, 2026-10-26)

The owner's rule (a faster case ships, a slower one never) with a gate that keeps a world in the cache on today's code:

- **The gate**: `CacheSizes.LastLevel` (src/EasyEcs.Core/CacheSizes.cs; a gate per form since, below), read once per process as the GC reads it for
  its gen0 budget: Windows `GetLogicalProcessorInformationEx(RelationCache)` (the largest cache record), Linux the largest
  `/sys/devices/system/cpu/cpu0/cache/index*/size`, then `sysconf(_SC_LEVEL3_CACHE_SIZE)`, macOS `hw.l3cachesize`, else
  `hw.perflevel0.l2cachesize`. Only where `Prefetching.IsSupported` (x86, ARM64 with SVE); any failure or 0 is
  unknown, which never prefetches. This machine reads 272,629,760 bytes (sysfs 266240K; sysconf agrees). A pass asks
  `Lookup<T>.Prefetches(world)`: live entities x (16-byte record + size of T) past that size.
- **Row form** (no vector loop, one thread): the method keeps today's job; a twin `Job{i}Chases` (a run job: records of
  rows i + 16, rows of T of rows i + 8, the first 16 records before the loop, runs of 16 rows or fewer unprefetched) runs
  when the pass's call site finds the gate open. A first version replaced the job for every pass: LookupGather's parallel
  heavy rows took 3x as long at 1k rows (ForEachBatchParallel instead of ForEachParallel) and the serial form a few
  percent more, so the job is chosen per pass. Parallel row methods and fused walks got their twins on feat-prefetch-all
  (below).
- **Vector form** (Kernel.Lookups.cs, `SimdMode.Require`): before each Resolve, `if (f{i}) lookup.PrefetchAhead(...)`: the
  records of the vector's rows 32 past it and the rows of T 16 past it; `f{i}` is set once per pass by the job's
  initializer. Only ids of an `in` component's lanes (not a wide component).
- What a chase is: `lookup[x.F]`, `Read(x.F)`, `Has(x.F)` (records only) at an EntityId reached through fields or
  auto-properties of an `in` component (src/EasyEcs.Generator/Chases.cs); a keyed lookup is not one.

Proof (frozen snapshots: base = claude/relaxed-ramanujan-j4j9k4 at 8fc246f, head = feat-prefetch-gate at e080b98; default
job, two rounds in alternating order; ms, 1k in us; the generated code ignores Distance, so each value is the mean of
the two Distance rows; all 0 B):

| LookupChaseBenchmark | base r1 / r2 | head r1 / r2 | head speed-up |
|---|---|---|---|
| ChaseSerial 1k | 3.35 / 3.20 us | 3.14 / 3.25 us | 1.07 / 0.98 (gate off) |
| ChaseGathered 1k | 2.78 / 2.83 us | 2.92 (one Distance row bimodal: 11.0 +- 8.7) / 2.94 us | in-class: LookupChaseGate Auto 2.77 / 2.69 against Off 3.32 / 2.70 |
| ChaseSerial 1M | 18.61 / 17.66 | 18.02 / 19.00 | 1.03 / 0.93 (gate off: the same job, errors +-1-2 ms) |
| ChaseGathered 1M | 16.02 / 15.72 | 15.69 / 15.27 | 1.02 / 1.03 (gate off) |
| ChaseSerial 16M | 945.2 / 962.5 | 695.9 / 672.8 | **1.36 / 1.43** |
| ChaseGathered 16M | 840.4 / 857.4 | 660.3 / 638.7 | **1.27 / 1.34** |
| ChaseParallel 16M | 288.3 / 314.0 | 298.0 / 308.4 | 0.97 / 1.02 (no prefetch in parallel) |

LookupBenchmark (10k / 100k, in the cache): ChaseSerial 32.9 / 334.4 us (base r1) against 32.8 / 334.8 (head r1) and
32.8 / 336.5 (base r2) against 33.0 / 331.4 (head r2). KernelBenchmark Steer / Glide (the 16-word locals fix in the same
branch): equal within noise (Steer 1k 928 / 960 ns base, 924 / 955 head).

LookupGatherBenchmark (216 cases, `--dev`, two rounds in alternating order, head / base): geomean PerRow 0.996, Vector
0.993, ParallelVector 1.007, ParallelPerRow 1.024; ParallelPerRow is byte-identical code (parallel row methods get no
prefetch), so its few cases 1.1-1.5x slower in both rounds are the noise floor of this VM, and the same holds for the
handful of ParallelVector / Vector cases at 1M. The 5-272 B at 1M rows appear in base and head alike.

LookupChaseGateBenchmark (head only, two rounds; Auto is the machine's gate, Off today's loop, On forced):

| | 1k Off / On | 1M Off / On | 16M Off / Auto |
|---|---|---|---|
| Hunting (rows) | 3.17-3.24 / 4.66-5.18 us | 17.65-17.76 / 14.10-14.18 | 931-967 / 683-686 |
| HuntingGathered | 2.70-3.32 / 5.12-5.20 us | 14.95-15.17 / 14.73-15.28 | 797-804 / 663-666 |

So in the cache a prefetch costs 1.4-1.9x (1k), which the gate avoids; at 1M (24 MiB, inside this machine's 260 MiB
L3) forcing it on pays 1.25x for rows and nothing for the vector loop: a machine whose last-level cache is smaller than
the world (the owner's 32 MiB x86, Apple M4: no prefetch on ARM64 in .NET 10) is where the gate decides differently.
For the owner (any x86 machine; on ARM64 the gate is off and only Off-equal numbers come out):

    dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*LookupChaseGateBenchmark*'

(it prints the cache size it read: `// last-level cache: N bytes`; Auto equal to Off below it and to On past it is the
gate working).

Not done here, with the reason (same gate, another form): **index probes** (`TryFind(key.F)`): the prefetch address
depends on the index's representation (key slots, the head table, the relation map by target), so it needs a probe
helper per representation in ValueIndex, not the chase form; scalar group prefetching measured 1.33x at 16M keys
(locality.md 6.3). **Relation targets** (a DynamicBuffer of links): a chase per element of a buffer, not per row, so the
row form's lookahead does not apply as is. Both are built on feat-prefetch-all (below).

#### Every form that can prefetch (feat-prefetch-all, 2026-10-27)

The same gate and the same rule, extended to every chase the generated code and the core can see ahead (backlog item
6). What is built:

- **Index probes** (`lookup.TryFind(x.F, ...)`, `Find`, `Count` at a key of an `in` component): the row form's twin
  prefetches the index's own lines, 16 rows ahead and 8 rows ahead (`ValueIndex.Prefetch(key, stage)`,
  src/EasyEcs.Core/IndexPrefetch.cs): a direct key's slot (stage 1); an `EntityId` key's slot, then its id list; a
  hashed key's bucket (stage 1), then the entry the bucket points at (stage 2; `Dictionary`'s `_buckets` /
  `_comparer` through `UnsafeAccessor`, `_entries` through a `FieldInfo` cached per thread against the buckets array).
  The gate (`LookupExtensions.Probes<T, TKey>`) is the index's own memory past the cache: slots, the target map, the
  heads at the entry's stride and, for a shared key, the list nodes.
- **Relation targets** (`foreach (ref readonly var link in links.AsReadOnlySpan()) lookup[link.Target]` over an `in`
  buffer): each element's record 16 rows ahead, its row 8 ahead.
- **Parallel row methods**: the twin is a batch job (`ForEachBatchParallel`, the same chunks `ForEachParallel` cuts);
  methods with `Maybe`, `Mut` or change stamps keep the per-row form, unprefetched (one row per call: nothing ahead).
- **Fused walks** (within a system and the pair of two systems) and **lambda passes**: a twin walk of the members'
  twins / the interceptor's twin, picked by the same gate before the size check.
- **The core's id lists**: `World.DestroyEntities(ReadOnlySpan<Entity>)` prefetches the record 16 ids ahead past the
  cache (`LookupLanes.PrefetchesIds`: live entities x (record + a row line) past the last-level cache).
- **ARM64**: `Prefetching.Line` issues SVE's `PRFB` (`Sve.Prefetch8Bit`, one-lane predicate, SYSLIB5003 suppressed at
  that one call) where SVE is hardware; elsewhere on ARM64 nothing is issued and the cache size is never read.

Proof (frozen snapshots: base = claude/relaxed-ramanujan-j4j9k4 at 63771f4, head = feat-prefetch-all before commit;
default job, two rounds; ms; Auto is the machine's gate (272,629,760 bytes); all 0 B unless noted):

| Benchmark | 16M base / head r1 | 16M base / head r2 | 1M base / head r1, r2 | 1k |
|---|---|---|---|---|
| ChaseForms Party (buffer chase) | 2090 / 1460 (**1.43x**) | 2095 / 1443 (**1.45x**) | 44.3 / 47.2, 44.3 / 48.7 | equal |
| ChaseForms Walk (fused) | 1071 / 606 (**1.77x**) | 1041 / 594 (**1.75x**) | 18.2 / 19.0, 18.2 / 19.6 | equal |
| LookupChaseGate HuntingParallel | 285 / 174 (**1.64x**) | 260 / 174 (**1.49x**) | 5.07 / 4.62, 5.12 / 4.50 | equal |
| ProbePrefetch FindLogin (unique key) | 979 / 700 (**1.40x**) | 1020 / 688 (**1.48x**) | 19.1 / 19.5, 21.8 / 20.3 | 3 us both |
| ProbePrefetch FindLoginLambda | 960 / 705 (**1.36x**) | 988 / 719 (**1.37x**) | 20.4 / 19.0, 19.7 / 21.1 | 3 us both |
| ProbePrefetch FindLoginParallel | 287 / 206 (**1.39x**) | 287 / 215 (**1.33x**) | 5.66 / 5.42, 5.63 / 5.67 | 3 us both |
| ProbePrefetch CountClan (shared key) | 592 / 570 (1.04x) | 665 / 561 (1.19x) | 12.6 / 12.8, 13.5 / 12.5 | 3 us both |
| IdListPrefetch DestroyAndCreate | 211 / 157 (**1.34x**) | 208 / 162 (**1.28x**) | 3.80 / 3.86, 3.95 / 4.04 | - |

The 1M worlds sit inside this machine's 260 MiB L3, so Auto runs today's code there; the gaps at 1M are the spread of
setup placement between processes, not the code: within one binary, Auto against Off at 1M: Party 42.1 / 41.6, Walk 18.4 / 18.1, FindLogin 20.8 / 20.1,
FindLoginLambda 22.9 / 22.8, FindLoginParallel 5.17 / 5.54, CountClan 12.7 / 12.4, DestroyAndCreate 4.11 / 3.96 (the
same code; 16M Auto against Off: Party 1392 / 2038, Walk 602 / 999, FindLogin 716 / 1012, CountClan 570 / 655,
DestroyAndCreate 158 / 206).

Forced on (head, Off / On): in the cache a prefetch costs 2-3x at 1k (Party 7 / 14 us, FindLogin 4 / 8 us, CountClan
3 / 7 us, FindLoginParallel 3 / 9 us), which the gate avoids. At 1M inside the L3, forcing it on still pays for the fused
walk (18.1 / 11.5 ms), DestroyEntities (3.96 / 3.02), the parallel probe (5.54 / 4.92) and a little for the buffer chase
(41.6 / 39.3), and costs for a probe (FindLogin 20.1 / 23.0, CountClan 12.4 / 14.4): hence a gate per form,
built next ("A gate per form" below).

Dropped, with numbers:

- **A third probe stage** for a key many entities share (the head's list of ids): finding the head is a second hash
  lookup per row; CountClan at 16M 1619 / 1528 ms against base 665 / 664 (2.4x slower) and forced on at 1M 35.7 against
  12.6 ms. Two stages (bucket, entry) keep CountClan 1.04-1.19x faster.
- **Loads issued ahead instead of a prefetch** (the ARM64 candidate without SVE; LookupChaseLoadAheadBenchmark,
  Locality/Chase.cs `LoadAhead`): on this x86 at 16M 1600-1643 ms against the plain rows' 930-984 and the prefetch's
  667-777 (672 B at Distance 4 in one run), at 1M 31-33 against 17.0-17.6, at 1k 13-15 us against 3: a load that misses
  holds retirement. The owner's M4 (2026-10-27, Apple M4, macOS) settles it, slower at every size and distance: 16M
  783-966 ms against Rows 451-504 (1.7-1.9x), 1M 31-36 against 12.6-16.5 ms (2.3-3.0x), 1k 8.9-13.6 against 1.9-2.2 us
  (4.4-7.2x). On the M4 `Prefetched` issues no instruction (no PRFM in .NET, SVE streaming-only), so it measures the
  stages alone: 1.4-1.7x at 1k, 1.1-1.4x at 16M; LookupChaseGate Auto equals Off there (1k 1.297 / 1.295 us, 16M
  339 / 357 ms, within the noise), forced On costs 1.32x (Hunting) to 1.59x (Gathered) at 1k. The gate's "unknown or
  no prefetch: off" is right on Apple silicon.

#### A gate per form (feat-prefetch-gate, 2026-10-27)

The owner's item 6 follow-up: forced on at 1M, inside this machine's 260 MiB L3, some forms already paid. Each form now
reads the cache it pays past (`CacheSizes`): the last-level cache as before, and one core's L2, read the same way
(Linux: the cpu0 `index*` whose `level` is 2 and `type` is not Instruction, then `sysconf(_SC_LEVEL2_CACHE_SIZE)`;
Windows: the largest level-2 data or unified record of `GetLogicalProcessorInformationEx(RelationCache)`; macOS
`hw.perflevel0.l2cachesize`, else `hw.l2cachesize`). An unknown L2 falls back to the last-level gate
(`CacheSizes.L2OrLastLevel`), so no machine is more eager than before unless its L2 is known; Apple silicon reads
neither (no prefetch). This machine: L2 2,097,152 bytes per core, LLC 272,629,760.

| Form | Gate | Generated / core call |
|---|---|---|
| Row form's entity chases: one thread, parallel (batch twin), fused walks, a buffer's elements, lambda passes | **L2** | `Lookup<T>.PrefetchesPastL2` |
| `World.DestroyEntities`' id list | **L2** | `LookupLanes.PrefetchesIds` |
| Index probes (one thread, parallel, lambda) | last level | `LookupExtensions.Probes` |
| Vector loops' chases (`f{i}`, D131 gathers) | last level | `Lookup<T>.Prefetches` |

Proof: `LookupChaseGateBenchmark`, `ChaseFormsPrefetchBenchmark`, `IdListPrefetchBenchmark`, `ProbePrefetchBenchmark`
(Auto / Off / On in one binary, `EASYECS_BENCH_ENTITIES=32768,262144,1000000`; IdList adds 8,192, Probe 524,288 in
round 2), default job, two rounds on frozen snapshots (r1: the first version, row chases and the vector loop on the
last level, parallel probes on the L2; r2: d9a6450, row chases on the L2). 32,768 entities are inside the L2 by every
form's estimate except DestroyEntities' (records + a 64-byte row line each: 2.6 MB), so Auto runs Off's code there.
Means; Auto / Off / On; 0 B everywhere.

| Benchmark (form, gate) | 32,768 r1; r2 | 262,144 r1; r2 | 1M r1; r2 |
|---|---|---|---|
| Hunting (row chase, L2) | 159 / 148 / 167; 163 / 155 / 181 us | 3.04 / 3.11 / 2.44 (Auto on the old gate); **2.28** / 2.89 / 2.31 ms | 17.2 / 18.6 / 13.8; **14.6** / 19.9 / 13.6 ms |
| HuntingParallel (parallel row chase, L2) | 73 / 66 / 74; 68 / 73 / 82 us | 1.30 / 1.30 / 0.90 (old gate); **0.84** / 1.05 / 0.98 ms | 6.51 / 6.65 / 3.98; **4.47** / 5.36 / 4.30 ms |
| Walk (fused walk, L2) | 172 / 188 / 178; 183 / 188 / 181 us | **1.77** / 3.52 / 1.90; **1.93** / 3.66 / 1.98 ms | **13.1** / 20.5 / 14.1; **14.3** / 20.6 / 13.7 ms |
| Party (buffer chase, L2) | 428 / 412 / 605; 455 / 473 / 606 us | **7.08** / 7.77 / 7.38; **6.77** / 7.28 / 7.11 ms | **48.1** / 49.8 / 42.3; **42.1** / 52.9 / 47.6 ms |
| DestroyAndCreate (id list, L2) | **59.7** / 66.7 / 64.0; **58.4** / 66.1 / 63.1 us (8,192: 16.7 / 15.9 / 16.5; 16.1 / 15.9 / 14.7) | **699** / 855 / 634; **656** / 836 / 660 us | **3.41** / 4.48 / 3.42; **3.33** / 4.49 / 3.33 ms |
| HuntingGathered (vector loop, last level) | 107 / 112 / 173; 111 / 115 / 183 us | 2.14 / 1.87 / 3.96; 1.92 / 1.84 / 2.00 ms | 15.8 / 23.1 / 14.1; 15.5 / 15.5 / 15.3 ms |
| FindLogin (probe, last level) | 206 / 200 / 329; 199 / 195 / 334 us | 3.67 / 3.90 / 4.72; 3.69 / 3.61 / 4.24 ms | 30.6 / 36.9 / 31.8; 23.0 / 27.4 / 37.8 ms |
| CountClan (shared-key probe, last level) | 137 / 145 / 284; 144 / 134 / 286 us | 2.12 / 2.33 / 3.75; 2.28 / 2.33 / 3.22 ms | 15.0 / 15.0 / 18.2; 14.4 / 14.3 / 18.5 ms |
| FindLoginLambda (probe, last level) | 215 / 223 / 347; 197 / 215 / 318 us | 3.75 / 3.64 / 4.32; 3.79 / 3.62 / 4.69 ms | 32.0 / 30.1 / 31.7; 28.8 / 40.1 / 30.3 ms |
| FindLoginParallel (parallel probe; Auto on the L2 in both rounds) | 81 / 89 / 130; 73 / 91 / 127 us | 1.49 / 1.54 / 1.71; 1.47 / 1.41 / 1.45 ms (524,288: 3.27 / 2.81 / 3.06) | 9.60 / 10.87 / 7.37; 6.93 / 8.25 / 8.19 ms |

Verdicts. **Shipped on the L2**: the row form's chases on one thread (1.27x at 262,144, 1.36x at 1M, r2) and in
parallel (1.26x, 1.20x), the fused walk (1.9-2.0x, 1.44-1.57x), the buffer chase (1.08-1.10x, 1.04-1.26x: Party is
the noisiest class, StdDev 5-7 ms at 1M), DestroyEntities (1.22-1.27x, 1.31-1.35x; 1.13x already at 32,768, the first
size its estimate puts past the L2). Inside the L2 Auto runs Off's code (equal within the noise), and forcing it on
costs (Hunting 1.12-1.17x, Party 1.28-1.47x, the vector loop 1.5-1.6x at 32,768). **Kept on the last level**: plain
probes and lambda probes (forced on 1.13-1.31x slower at 262,144, CountClan 1.38-1.61x), the vector loop (forced on
no faster at 262,144, 1.04-2.1x slower), and **parallel probes, reverted** after r2 (the owner had listed them for the
L2 from 5.54 / 4.92 at 1M): a wash at 262,144 (1.04x slower in r2), slower at 524,288 (Auto 1.16x, On 1.09x), faster
only at 1M (1.13-1.19x Auto, On 1.0-1.48x): a slower case never ships. Parallel rows on this VM move 1.1-1.5x on
identical code (`audit-perf`), so the owner's machines may say otherwise; the benchmark prints both sizes it read.

For the owner's machines (x86; any L2): `EASYECS_BENCH_ENTITIES=<sizes between the L2 and the LLC> dotnet run -c
Release --project benchmarks/EasyEcs.Benchmark -- --filter '*PrefetchBenchmark*' '*LookupChaseGateBenchmark*'`.

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
