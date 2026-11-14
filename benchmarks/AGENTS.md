# Benchmarks: file map

> Read when: adding, changing or running a benchmark.

BenchmarkDotNet only; the method (A/B, lock, quiet machine) is `audit-perf`, the jobs are
`EasyEcs.Benchmark/BenchmarkConfig.cs` (default for decisions and the site; `--dev` while developing).
The projects target `net10.0;net11.0`: `dotnet run -f net11.0` measures .NET 11 (the host's runtime), and
`--frameworks net10.0,net11.0` runs each case on both in one process, to compare the runtimes (`tools/perf/bench.sh`
takes `--framework`; the site's numbers come from one runtime, net10.0).

When you add, rename or delete a file here, update its line
(`tools/agents.sh --check`, run by CI and `verify`, fails on a file missing from it).

`EasyEcs.Benchmark` sits in folders named as Core's (`Grids/`, `OrderedTables/`, ...) plus `Generator/` (the generators' own
cost); `Locality/` is the linked phase 0 prototypes. What other projects reach by path stays at the root (BattleServer.cs,
GeneratorCorpus.cs, WorldSimulation.cs, BenchmarkConfig.cs: the unit tests, the AOT check, `EasyEcs.Comparison` and the
VoxelServer benchmark link them), with the harness (Program.cs, IsaColumn.cs, PageFaultDiagnoser.cs) and the benchmarks of
the machine (RooflineBenchmark.cs). Every file keeps the namespace `EasyEcs.Benchmark` (`EasyEcs.Benchmark.csproj.DotSettings`
turns Rider's folder-vs-namespace hint off).

A benchmark nests its components in its class (or a static class of its file), so names never collide across
benchmarks; only the headline worlds (WorldSimulation.cs, MicroBenchmarks.cs) declare theirs at namespace level.

`benchmarks/EasyEcs.Benchmark/` BenchmarkDotNet: the headline worlds, one benchmark per feature, the phase 0
prototypes of docs/plans/designs/locality.md (Locality/). Always measure with
BenchmarkDotNet.

## Root

```
Program.cs                   the switcher, with BenchmarkConfig's job
BenchmarkConfig.cs           the jobs of both benchmark projects (default; --dev; --frameworks net10.0,net11.0)
IsaColumn.cs                 the ISA as a column ([IsaColumn]; Locality/Isa.cs names it)
WorldSimulation.cs           the headline world, a realistic server tick (compiled unchanged by
                             EasyEcs.Comparison)
WorldSimulationBenchmark.cs  WorldSimulationBenchmark, MultiWorldBenchmark: the headline
BattleServer.cs              the battle server, every feature; docs/content/example.mdx includes its regions
                             (linked into the unit tests and the AOT check)
BattleServerBenchmark.cs     BattleServerBenchmark
MicroBenchmarks.cs           single operations isolated (diagnostics, not the headline)
GeneratorCorpus.cs           the battle server and 40 modules, shared with the unit tests that check it clean
                             (tools/Shared/GeneratorHost.cs: the generators, parse options, references)
PageFaultDiagnoser.cs        minflt, majflt, stime and the THP counters per op, read by the host around an extra
                             launch
RooflineBenchmark.cs         Roofline (+ RooflinePeak): the roofs of audit-perf's roofline, one thread's
                             bandwidth from the L2 and from memory and its peak of fused multiply-adds
```

## Components/

```
Components/BufferSharingBenchmark.cs  BufferSharing (+ BufferSnapshot: a ring of captures and rollbacks of buffers,
                                      RollbackSpawning: one that rewrites a table)
Components/CleanupBenchmark.cs        sessions holding an outside resource, with a cleanup component
Components/DynamicBufferBenchmark.cs  DynamicBuffer (+ BufferChurn, ChunkChurn: MaxPooledLength, PrefabBufferChurn,
                                      ParallelBufferChurn, ChunkReload)
Components/HotColdBenchmark.cs        a pass over wide rows against the cold fields split out: EECS091's threshold
Components/SparseBenchmark.cs         a rare state in a large table (presence bits)
```

## Diagnostics/

```
Diagnostics/IntrospectionBenchmark.cs  Introspection (+ Schedule, Entities: the walk of world.Entities)
```

## Entities/

```
Entities/DestroyLogBenchmark.cs    the destroy log's cost on the destroys
Entities/PrefabBenchmark.cs        one Instantiate per entity against the bulk Instantiate
Entities/SingleEntityBenchmark.cs  work on one entity at a time (a request handler, an actor's message)
```

## Generator/

```
Generator/GeneratorBenchmark.cs  the generators per keystroke and the analyzer per build, through Roslyn, over
                                 GeneratorCorpus.cs; the generated text's size as a column (GeneratedBytesColumn)
```

## Grids/

```
Grids/TileBenchmark.cs          TileStencil: a 9-point blur and a Laplacian wave over tiles, read through the tile,
                                its neighbors directly, by hand, stepped by ref: ping-pong, stepped by ref walked by
                                c.Columns with col.Store: Columns (the blur by Columns(16) with Load512 where
                                Vector512 is accelerated: Blur9_Columns512), and written as one cell
                                (Cell<TCell, TTile>): Cell; TileActive: a field where few tiles change, every tile
                                stepping against sleeping ones by hand, stepped by ref, and Life as one cell;
                                TileNeighbor: the tiles around one found per pass or cached
Grids/TileExchangeBenchmark.cs  Tile.ExchangeHalo alone: a halo of 2 with 8-byte side runs and a halo of 1 with its
                                corners, TileLayout.Copy's short runs
Grids/TileHaloBenchmark.cs      item 12a's prototype: the halo copied into a texture per step against tiles stored
                                padded with only the ring copied
Grids/TileInPlaceBenchmark.cs   the hand-written Laplacian with its halo copied into a texture against its rows read
                                in place from the neighbors' buffers, put beside it in a window (InPlace) or shifted
                                in registers (InPlaceShift), and the four neighbor lookups alone
Grids/TileLayersBenchmark.cs    a seven-point cell method over tall tiles whose warmth sits in a band of layers:
                                every layer against the band an ITileLayers component gives
Grids/TileColdStartBenchmark.cs a scent whose springs move, read once settled: [GaussSeidel] settled from nothing
                                each step (ColdStart), the same in sweeps of turning direction (Turn: tile.Sweep) and
                                from its last values (Linger), ColdStart walked by c.Columns(1) (ColdStartColumns),
                                springs stepping or hopping, against the sample's sweep by hand from a cleared copy,
                                a step and a commit per color
Grids/TileSettleBenchmark.cs    a field read once settled, sources moving: [GaussSeidel] against the in-place sweep
                                by hand, a step and a commit per color
Grids/TileTallyBenchmark.cs     a byte cell method with a result per tile, the voxel water's shape: the cell method
                                with ref Reduce<T> against the pass by hand with Load128 and popcounts
Grids/TileWokenBenchmark.cs     a sleeping ping-pong pass with one plate in 64 to 2 awake: the walk of the woken
                                against the walk of every tile, forced and as the gate picks, against sleeping by
                                hand: TileWalk.Share
```

## Indexes/

```
Indexes/BatchProbeBenchmark.cs    one key at a time, with group prefetching, against vertical probes with and without
                                  refill, 1k to 16M keys (Locality/Operators.cs)
Indexes/IndexBenchmark.cs         Index (+ unique keys)
Indexes/OrderedIndexBenchmark.cs  a leaderboard through the ordered index
```

## Locality/

```
Locality/                   phase 0 of docs/plans/designs/locality.md: hand-written prototypes that decided what got
                            built, and the worlds the shipped forms are measured on (linked into the unit tests
                            by LocalityTests; why each file stays: locality.md's status). Every kernel picks 8
                            lanes when Vector256.IsHardwareAccelerated, else 4 (ARM64's NEON), and guards each
                            intrinsic
Locality/Chase.cs           the chase world and the hand-prefetched chase jobs
Locality/CoreClusters.cs    the cluster-pair kernel on the core's order and clusters
Locality/Crowd.cs           the crowd world, its grids, lists and Morton-sorted cluster pairs
Locality/CrowdPairs.cs      the generated pair methods over the crowd
Locality/Isa.cs             the ISA the kernels run (printed by setup)
Locality/Nearest.cs         the samples' nearest searches and their sorted-cluster forms
Locality/Operators.cs       compress (Selection) and batch probes (ProbeTable)
Locality/SortedClusters.cs  the Morton sort, clusters of 8 with boxes, the box tree
Locality/Streams.cs         non-temporal stores and copies
Locality/Timers.cs          the timer wheel
```

## Lookups/

```
Lookups/LookupBenchmark.cs         Lookup<T> in parallel and on one thread against a World parameter's reads
Lookups/LookupChaseBenchmark.cs    random lookups across 1k, 1M and 16M entities: the generated jobs against the
                                   prefetching prototypes of borrowed-ideas.md item 1 (Locality/Chase.cs);
                                   + LookupChaseDistance: the prefetch distance screen; + LookupChaseGate: the
                                   generated jobs with the last-level cache gate as the machine decides it, forced off
                                   and forced on; + LookupChaseLoadAhead: loads issued ahead against prefetches, for
                                   ARM64 without a prefetch; + LookupChaseHugePages: the chases, a plain pass, a world
                                   made and grown, with the large arrays on 4 KiB against 2 MiB pages
Lookups/LookupGatherBenchmark.cs   a pass reading other entities: the vector loop's gathers against the method row by
                                   row, targets in 1, 4, 64 or 1,000 tables, dead, or without the component (presence
                                   bits); light, bearing and heavy bodies
Lookups/LookupOrderBenchmark.cs    LookupChase's and LookupGather's passes with the readers' table in random order
                                   against ordered by target: L1
Lookups/PrefetchFormsBenchmark.cs  ProbePrefetch: index probes, unique and shared keys, parallel, lambda;
                                   ChaseFormsPrefetch: buffer chases and a fused walk; IdListPrefetch:
                                   DestroyEntities of random ids; each with the gate Auto / Off / On
                                   (EASYECS_BENCH_GATES=auto for a base / head run; PrefetchGate)
```

## OrderedTables/

```
OrderedTables/NearestPlayerBenchmark.cs  the sample's nearest search against a search of sorted clusters
                                         (Locality/Nearest.cs, Locality/SortedClusters.cs)
OrderedTables/NearestZombieBenchmark.cs  the same for the zombies' search
OrderedTables/PairHalvesBenchmark.cs     the method writing both sides on 1 and 4 threads: serial, its parallel pass of
                                         colored chunks, and the parallel method writing its row only over every ordered
                                         pair
OrderedTables/PairListsBenchmark.cs      the generated pair method over a moving crowd, lists of each row's neighbors picked
                                         by cost, never or always
OrderedTables/PairsBenchmark.cs          sums over neighbors within a cutoff: a grid read through lookups, a grid of copies,
                                         neighbor lists, the cluster-pair kernel over Morton-sorted clusters with its sort
                                         every 1 / 10 / 20 ticks (Locality/Crowd.cs); the same kernel on the core's order and
                                         clusters, 8, 4 or 16 wide (Locality/CoreClusters.cs); the generated pair method,
                                         vector loop and one pair at a time, and writing both sides of each unordered pair
                                         once (Locality/CrowdPairs.cs); + PairGroups: the generated pass over 1 or 64 groups
                                         of the crowd, what it pays per table and pair of tables
OrderedTables/RowOrderBenchmark.cs       the core's re-sort: narrow and wide tables, 1k-1M rows, reversed / 1 % churn /
                                         sorted
```

## Parallel/

```
Parallel/ChunkSizeBenchmark.cs        chunk sizes inferred from the measured cost: light, heavy, uneven and few expensive
                                      rows
Parallel/ParallelForBenchmark.cs      World.ParallelFor over an Emitted<T> list: light and heavy steps (+ LightLambda / HeavyLambda: the lambda form against the hand-written jobs)
Parallel/ParallelPassBenchmark.cs     one heavy system as [ForEach] and [ForEach(Parallel = true)]
Parallel/ParallelRunnerBenchmark.cs   the runner of a parallel [ForEach] against Parallel.For as the generator could emit
                                      it
Parallel/ParallelSystemsBenchmark.cs  ParallelSystems (Measure)
```

## Parameters/

```
Parameters/AccumulateBenchmark.cs    an [Accumulate] total through Reduce<T>, serial and parallel, against the singleton
                                     added to row by row
Parameters/DrainBenchmark.cs         a [Drain] pass against the hand-written walk and Clear of Emitted<T>
Parameters/EmitBenchmark.cs          Emit<T> in parallel and on one thread against a component scanned after, a
                                     ConcurrentQueue and an Interlocked cursor
Parameters/EntityRandomBenchmark.cs  EntityRandom against Philox and the generators it was chosen over
Parameters/HistogramBenchmark.cs     keyed reductions
Parameters/MaybeBenchmark.cs         ReadOnlyMaybe<T> against two methods
Parameters/ReduceBenchmark.cs        Reduce<T> (an integer sum, a float sum, a bounding box) on several threads and on
                                     one against a ref value, Interlocked, a lock and
                                     Parallel.For<TLocal>
Parameters/ScratchBenchmark.cs       a per-row working list in a parallel [ForEach]: Scratch<T> against a new list, a
                                     rented array, [ThreadStatic] and stackalloc
Parameters/ViewBenchmark.cs          a pass taking a [View] against the refs it stands for, vector and scalar, as a lambda pass, and AsX
                                     against Get / Read
```

## Queries/

```
Queries/AccessBenchmark.cs          the analyzer's performance rules (EECS025-EECS028, EECS052)
Queries/CallSiteBenchmark.cs        the cost of a query call itself: filters built at the call, once, none, past the
                                    memo's ways
Queries/ChangeTrackingBenchmark.cs  ChangeTracking (+ ChangeTrackingWrite: Mut<T>, ChangeTrackingRead: Read,
                                    ChangeTrackingSomeRows: EECS053)
Queries/CountBenchmark.cs           counting rows tested one by one (disabled components)
Queries/SlicesBenchmark.cs          [ForEach] against [ForEach(Slices = 4)]
Queries/TimerWheelBenchmark.cs      borrowed-ideas.md item 2: scans of wake ticks against a timer wheel
                                    (Locality/Timers.cs)
```

## Relations/

```
Relations/HierarchyBenchmark.cs  destroying linked entities: Cascade against RemoveLinks
Relations/RelationBenchmark.cs   friend lists as a relation: Link / Unlink and the index
```

## Saves/

```
Saves/FullCopyBenchmark.cs       the whole-column copies snapshots still make: Capture / Restore of untracked
                                 components, in and past the cache
Saves/CopyEntitiesBenchmark.cs      a halo exchange between two worlds: the CreateEntity loop over the border band
                                 against SaveEntitiesWith + LoadEntities and CopyEntities<Ghost>, 1 or 3 components,
                                 rows sorted or scattered
Saves/MigrationBenchmark.cs      players changing worlds: through bytes against MoveEntities; a record saved and
                                 loaded onto a live entity
Saves/SnapshotBenchmark.cs       Snapshot (+ SaveWithout / LoadKeeping; + Rollback: rollback netcode, 100k
                                 entities, a few writes per tick, a ring of captures; + BulkCapture: a SetAll reset
                                 then a capture into the same snapshot each tick)
Saves/StreamingCopyBenchmark.cs  a large copy: memmove against loops of non-temporal stores, in and past the
                                 last-level cache (Locality/Streams.cs)
Saves/UnloadBenchmark.cs         unloading a plugin (EasyEcs.Benchmark.Plugin) from a world
```

## Storage/

```
Storage/BulkBenchmark.cs           a round reset in bulk (SetAll / RemoveAll) against per entity
Storage/GroupBenchmark.cs          players by zone: GroupBy against a value index and a scan
Storage/HugePageTouchBenchmark.cs  a large pinned array allocated and touched page by page: plain, collapsed,
                                   advised, dropped, and kept as HugePages.AllocatePinned does
Storage/NativeColumnsBenchmark.cs  #159: a world's unmanaged arrays on the pinned heap against
                                   NativeMemory.AlignedAlloc, with and without GC.AddMemoryPressure, and the world
                                   created whole
Storage/PageFaultBenchmark.cs      the ops that allocate a large world's arrays at 1M and 16M: create, grow, reserve,
                                   load, capture, restore, a wave of spawns into a reservation, one Instantiate per
                                   entity; pages 4 KiB / 2 MiB and prefault off / on in one binary
Storage/PrefaultTouchBenchmark.cs  a large array faulted in one call, MADV_POPULATE_WRITE, against its first touch, on
                                   2 MiB and 4 KiB pages, memory reused at once or freed 4 s earlier
Storage/TrimBenchmark.cs           TrimExcess after a peak
```

## Systems/

```
Systems/ConsumeBenchmark.cs         [Consume] in bulk and per row against a hand-written Destroy
Systems/EntityMethodBenchmark.cs    [EntityMethod] against the hand-written job
Systems/FusionBenchmark.cs          fused walks against passes apart: in a system, parallel, across two systems
Systems/LambdaFusionBenchmark.cs    two lambda passes written one after the other as one walk, against the same calls
                                    apart, on one thread and in parallel
Systems/LambdaPassBenchmark.cs      a lambda pass against the [ForEach] method and the hand-written job, scalar and
                                    vector bodies, plus the EECS096 delegate fallback (a body naming a private member)
Systems/MaxRowsBenchmark.cs         a budget of requests served per tick, [ForEach(MaxRows = N), Consume] against the
                                    OnUpdate by hand: CollectIds, a copy, DestroyEntities, a loop
Systems/OrderingBenchmark.cs        the cost of [UpdateAfter<T>] ordering per update
Systems/RunnerBenchmark.cs          a [ForEach] entry point outside systems against the hand-written job and a Query
Systems/SystemDispatchBenchmark.cs  what a system costs per update apart from its rows
```

## VectorLoops/

```
VectorLoops/CompactBenchmark.cs             stream compaction: the ids under a limit (1 %, 50 %, every row) into a singleton's
                                            array
VectorLoops/CompressBenchmark.cs            a mask to a row list: branch, branchless, AVX2 permutation table, 128-bit shuffle
                                            table (NEON TBL / PSHUFB), AVX-512 compress, Lanes.Compact (Locality/Operators.cs)
VectorLoops/ElementLoopBenchmark.cs         a loop over a row's buffer: as written, with the vector form the generator writes,
                                            and by hand; Decay in place, Widen across two buffers, SumInts and SumFloats into a
                                            variable; 4, 64 and 1,024 elements; HeightMapLoopBenchmark: VoxelServer's height
                                            map, the plain loop against the Vector128 one it replaced; FlatTailBenchmark: a flat loop over 4 to 64
                                            elements of two floats, as written against its vector form; BatchLoopBenchmark: a
                                            batch method's loop over its rows against the method for one row
VectorLoops/FlagLanesBenchmark.cs           enum and bool components: the vector loop against the same method with
                                            SimdMode.Off
VectorLoops/InlineArrayBenchmark.cs         loops over a row's inline arrays unrolled into the vector loop: Spectrum each element
                                            alike against the loop by hand, Mix elements used apart, Nearest an argmin over a
                                            value's waypoints; each against SimdMode.Off
VectorLoops/KernelBenchmark.cs              a [ForEach] method for one row as the per-row loop, the batch method and the vector
                                            loop the generator writes
VectorLoops/LateHolesBenchmark.cs           vector loops whose runner reached Tier1 before their table had holes: the masked
                                            words' tables and lane masks
VectorLoops/StreamingStoreBenchmark.cs      borrowed-ideas.md item 5: a write-only column with plain against non-temporal
                                            stores, in and past the last-level cache (Locality/Streams.cs)
VectorLoops/StreamingStoreGateBenchmark.cs  the generated loops' streaming stores in two worlds, a write-only pass with and
                                            without the pass reading its column, in and past the last-level cache, the gate
                                            Auto / Off / On
VectorLoops/StructuralLoopBenchmark.cs      a tag set under a condition: vector loop + marked rows (a [ForEach] method and a lambda pass) against the scalar method, a
                                            command buffer and SetAll
VectorLoops/VectorFormsBenchmark.cs         masked words in parallel chunks, Maybe<T> in parallel and of byte lanes, masked words
                                            of byte lanes, Pow of floats under FloatMode.Fast: each method against SimdMode.Off
```

## The other benchmark projects

```
benchmarks/EasyEcs.Benchmark.Plugin/   a collectible plugin the Unload benchmark loads and unloads
  Scoring.cs                           its components, singleton and system
benchmarks/EasyEcs.Comparison/         BenchmarkDotNet against Arch, Friflo.Engine.ECS and DefaultEcs, every result checked
                                       in setup
  Program.cs                           BenchmarkDotNet; --check runs every library and compares their counters,
                                       --features the feature comparisons' checks
  Shared.cs                            the rules, counters and checks every port shares
  ComparisonBenchmark.cs               the headline world (WorldSimulation.cs, compiled unchanged) in each library
  EasyEcsSimulation.cs                 the EasyEcs side of the headline world
  ArchSimulation.cs                    its port to Arch
  DefaultEcsSimulation.cs              its port to DefaultEcs
  FrifloSimulation.cs                  its port to Friflo.Engine.ECS
  OperationBenchmark.cs                per operation
  FeatureBenchmark.cs                  change tracking, removal detection, indexes
  QueryComparisons.cs                  value queries, parallel queries, saves
  StructureComparisons.cs              hierarchy, relations, disable, structural changes
  BulkComparisons.cs                   bulk changes, spawns
  SystemComparisons.cs                 parallel systems
```
