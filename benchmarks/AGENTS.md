# Benchmarks: file map

> Read when: adding, changing or running a benchmark.

BenchmarkDotNet only; the method (A/B, lock, quiet machine) is `audit-perf`, the job is
`EasyEcs.Benchmark/BenchmarkConfig.cs`.

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
                               LambdaPass (a lambda pass against the [ForEach] method and the hand-written job, scalar and vector bodies), Lookup, LookupChase (random lookups across 1M entities), Maybe, Migration, OrderedIndex, Ordering, ParallelForEach, ParallelSystems (Measure),
                               Prefab, Relation, Runner, SingleEntity, Slices, Snapshot (+ SaveWithout / LoadKeeping),
                               Sparse, StructuralLoop (a tag set under a condition: vector loop + marked rows against the
                               scalar method, a command buffer and SetAll), SystemDispatch (what a system costs per update apart from its rows), Trim,
                               Unload, View (a pass taking a [View] against the refs it stands for, vector and scalar, and AsX against Get / Read)) + MicroBenchmarks.
                               Always measure with BenchmarkDotNet.
benchmarks/EasyEcs.Benchmark.Plugin/  a collectible plugin (Scoring.cs) the Unload benchmark loads and unloads
benchmarks/EasyEcs.Comparison/ BenchmarkDotNet against Arch, Friflo.Engine.ECS and DefaultEcs, every result checked in
                               setup: the headline world (WorldSimulation.cs, compiled unchanged, *Simulation.cs ports);
                               OperationBenchmark (per operation); FeatureBenchmark (change tracking, removal detection,
                               indexes); QueryComparisons (value queries, parallel queries, saves); StructureComparisons
                               (hierarchy, relations, disable, structural changes); BulkComparisons (bulk changes,
                               spawns); SystemComparisons (parallel systems)
```
