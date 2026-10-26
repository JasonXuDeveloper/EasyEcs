# Benchmarks: file map

> Read when: adding, changing or running a benchmark.

BenchmarkDotNet only; the method (A/B, lock, quiet machine) is `audit-perf`, the jobs are
`EasyEcs.Benchmark/BenchmarkConfig.cs` (default for decisions and the site; `--dev` while developing).

When you add, rename or delete a file here, update its line (`verify` checks every file is listed).

```
benchmarks/EasyEcs.Benchmark/  BenchmarkDotNet: WorldSimulationBenchmark / MultiWorldBenchmark (headline, realistic
                               server tick; the world itself is WorldSimulation.cs), BattleServerBenchmark
                               (BattleServer.cs, every feature; docs/content/example.mdx includes its regions), one
                               benchmark per feature (Access: the analyzer's performance rules, Bulk, CallSite,
                               ChunkRows (chunk sizes inferred from the measured cost: light, heavy, uneven and few expensive rows), ChangeTracking (+ ChangeTrackingWrite: Mut<T>, ChangeTrackingRead: Read, ChangeTrackingSomeRows: EECS053), Cleanup, Consume ([Consume] in bulk and per row against a hand-written Destroy), Count, DestroyLog, Emit, EntityRandom, ForEntity (against the hand-written job), ForParallel, Fusion (fused walks against passes apart: in a system, parallel, across two systems),
                               DynamicBuffer (+ ChunkChurn: MaxPooledLength), BufferSharing (+ BufferSnapshot: lists shared until written), Generator (the generators per keystroke and the analyzer per
                               build, through
                               Roslyn), Group, Hierarchy, HotCold (a pass over wide rows against the cold fields split out: EECS091's threshold), Histogram (keyed reductions), Index (+ unique keys), Introspection (+ Schedule),
                               LambdaPass (a lambda pass against the [ForEach] method and the hand-written job, scalar and vector bodies), Lookup, LookupChase (random lookups across 1k, 1M and 16M entities: the generated jobs against the prefetching prototypes of borrowed-ideas.md item 1, Locality/Chase.cs; + LookupChaseDistance: the prefetch distance screen; + LookupChaseGate: the generated jobs with the last-level cache gate as the machine decides it, forced off and forced on), LookupOrder (LookupChase's and LookupGather's passes with
                               the readers' table in random order against ordered by target: L1), LookupGather (a pass reading other entities: the vector loop's gathers against the method row by row, targets in 1, 4, 64 or 1,000 tables, dead, or without the component (presence bits); light, bearing and heavy bodies), Maybe, Migration, OrderedIndex, Ordering, ParallelForEach, ParallelSystems (Measure),
                               Prefab, Relation, RowOrder (the core's re-sort: narrow and wide tables, 1k-1M rows,
                               reversed / 1 % churn / sorted), Runner, SingleEntity, Slices, Snapshot (+ SaveWithout / LoadKeeping),
                               Sparse, StructuralLoop (a tag set under a condition: vector loop + marked rows against the
                               scalar method, a command buffer and SetAll), SystemDispatch (what a system costs per update apart from its rows), Trim,
                               Unload, View (a pass taking a [View] against the refs it stands for, vector and scalar, and AsX against Get / Read)) + MicroBenchmarks.
                               Phase 0 of docs/plans/locality.md (hand-written prototypes that decide what gets built;
                               their code in Locality/, linked into the unit tests by LocalityTests): Pairs (sums over
                               neighbors within a cutoff: a grid read through lookups, a grid of copies, neighbor lists,
                               the cluster-pair kernel over Morton-sorted blocks with its sort every 1 / 10 / 20 ticks;
                               Locality/Crowd.cs; the same kernel on the core's order and blocks, 8, 4 or 16 wide:
                               Locality/CoreClusters.cs), NearestZombie / NearestPlayer (the samples' nearest searches against a
                               search of sorted blocks; Locality/Nearest.cs, Locality/SortedBlocks.cs: the Morton sort,
                               blocks of 8 with boxes, the box tree), Compress (a mask to a row list: branch, branchless,
                               AVX2 permutation table, 128-bit shuffle table (NEON TBL / PSHUFB), AVX-512 compress,
                               Lanes.Compact) and BatchProbe (one key at a time,
                               with group prefetching, against vertical probes with and without refill, 1k to 16M keys;
                               Locality/Operators.cs), TimerWheel
                               (borrowed-ideas.md item 2: scans of wake ticks against a timer wheel; Locality/Timers.cs).
                               Every kernel picks 8 lanes when Vector256.IsHardwareAccelerated, else 4 (ARM64's NEON),
                               and guards each intrinsic; Locality/Isa.cs names the ISA (printed by setup), IsaColumn.cs
                               shows it as a column ([IsaColumn]).
                               Always measure with BenchmarkDotNet.
benchmarks/EasyEcs.Benchmark.Plugin/  a collectible plugin (Scoring.cs) the Unload benchmark loads and unloads
benchmarks/EasyEcs.Comparison/ BenchmarkDotNet against Arch, Friflo.Engine.ECS and DefaultEcs, every result checked in
                               setup: the headline world (WorldSimulation.cs, compiled unchanged, *Simulation.cs ports);
                               OperationBenchmark (per operation); FeatureBenchmark (change tracking, removal detection,
                               indexes); QueryComparisons (value queries, parallel queries, saves); StructureComparisons
                               (hierarchy, relations, disable, structural changes); BulkComparisons (bulk changes,
                               spawns); SystemComparisons (parallel systems)
```
