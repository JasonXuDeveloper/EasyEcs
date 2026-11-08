# Tests: file map

> Read when: adding or changing a test, or finding where a feature is tested.

NUnit unit tests and the Native AOT check. Every bug fix adds a regression test (`feature`).

When you add, rename or delete a file here, update its line
(`tools/agents.sh --check`, run by CI and `verify`, fails on a file missing from it).

An allocation check measures through `Allocations.Of(...)` or starts from `Allocations.Baseline()` (Fixtures.cs), never
from a raw `GC.GetAllocatedBytesForCurrentThread()`: a background collection ending in the window adds the unused
rest of the thread's allocation context to the count although nothing was allocated (D128). It counts this thread,
never the process (`GC.GetTotalAllocatedBytes`): the test host's own threads allocate now and then (168 B at 1
thread with every method of the walk compiled, ATileThatChangesLateDoesNotAllocate); a parallel pass's helpers are
covered because the calling thread steps chunks too (that test failed on this thread's bytes alone, 696-1016 B,
before its fix). A warm-up before a 0 B
check runs through `Allocations.WarmUp(step, least)`, which goes on until the JIT has settled: in the full suite a loop
can still run Tier0 code after a fixed warm-up, and its on-stack replacement compiles (and allocates) on the measuring
thread (fix-flaky-alloc). The fixed warm-ups left in KernelTests race the same way (moved once
feat-inner-loops merges). A 0 B check of a path that could rent from `ArrayPool` also asserts
`Allocations.PoolRents(run)` is 0: on a quiet machine a rent reuses the pool's array, but under high memory load the
pool empties at every gen2 collection (which `Allocations.Of` starts with), so the rent allocates (OrderTests,
NeighborTests; audit-core).

```
tests/EasyEcs.UnitTest/          NUnit, one file per feature
  AllocationTests.cs             the 0 B budget of steady-state ticks, structural changes, saves and loads
  AnalyzerTests.cs               the rules' harness: Diagnose / Costs / All, Extra, AssertReports / AssertClean / Count;
                                 one file per analyzer file below: what each rule reports, the edges it branches on and
                                 the code it leaves alone
  AnalyzerTests.Declarations.cs  declaration rules
  AnalyzerTests.Forms.cs         form rules, and EECS103's idioms and guards
  AnalyzerTests.Pairs.cs         EECS105-107
  AnalyzerTests.Parallel.cs      parallel rules
  AnalyzerTests.Parts.cs         generated parts' rules
  AnalyzerTests.Performance.cs   costs, lambda passes, view fields
  AnalyzerTests.Systems.cs       system rules
  AnalyzerTests.Tiles.cs         EECS115-117, EECS120's reasons, cell methods' EECS055 / EECS056 / EECS059 / EECS073,
                                 EECS123 and the layers' parameter left out of EECS026, a cell step's EECS115 reasons and
                                 EECS116 advice, lanes of 1 and 4 bytes, EECS115 for cells of two tiles, EECS002 for a
                                 result per tile outside a cell method
  AnalyzerTests.Usage.cs         usage rules
  BattleServerTests.cs           the world of docs/content/example.mdx as a whole (BattleServer.cs linked from
                                 EasyEcs.Benchmark)
  BulkTests.cs                   bulk changes
  ChangedTests.cs                [Changed<T>], Mut<T>, ChangedAny
  CleanupTests.cs                cleanup components
  CodeFixTests.cs                each fix compiles, adds no diagnostic and is not offered again
  ComponentTests.cs              components
  ConcurrencyTests.cs            structural changes from other threads throw; the gate a contended thread waits on
  ConsumeTests.cs                [Consume]
  CopyEntitiesTests.cs           World.CopyEntities<TTag>: rows copied from another world under a tag (a ghost of a
                                 neighbor's border), tracked tags stamped, gathered columns of any size
  DisableTests.cs                disabled entities
  DrainTests.cs                  [Drain] passes over Emitted<T> against the hand-written walk, EECS108-109
  DynamicBufferTests.cs          DynamicBuffer<T>
  ElementLoopTests.cs            loops over a row's spans and buffers inside methods run row by row, with their vector
                                 forms, against the same bodies under SimdMode.Off: for and foreach, struct elements, two
                                 spans, empty and short buffers, a target shorter than the bound throwing where the loop
                                 does; sums, Mins, Maxes and counts into variables (int, a long sum of ints, float sums
                                 under FloatMode.Fast); the index as a value (floats, longs); a batch method's loop; a
                                 lambda pass's loop; private helpers and fields reached through accessors; the schedule's
                                 loop lines; EECS055's reasons at loops
  EmitTests.cs                   Emit<T>, and EECS092
  EntityMethodTests.cs           [EntityMethod]
  EntityRandomTests.cs           EntityRandom
  EntityTests.cs                 entities
  Fixtures.cs                    the shared test components; Allocations (Of, Baseline, WarmUp, PoolRents)
  ForEachSystemTests.cs          [ForEach] systems, and Slices
  ForeignBodyTests.cs            constructors and helpers of EasyEcs.Aot.Library, inlined from what its generator
                                 recorded, against their twins row by row, 0 B
  FormTests.cs                   EECS080 / EECS084
  FusionTests.cs                 fused walks against FusionMode.Off twins, the exclusions
  GeneratedTextTests.cs          the generated text's dead lines counted per kind over the corpora of every vector form
                                 (global:: outside the alias block, needless parentheses, identity copies, unread locals,
                                 marks zeroed then OR-ed once, empty methods, constants made in a method), each against a
                                 budget that may only fall (audit-generator, "The generated text")
  GeneratorTests.cs              generated code, the vector loops and EECS055 / EECS056's reasons, caching;
                                 GeneratorBenchmark's corpus clean (GeneratorCorpus.cs, linked); the samples, the
                                 template and the demo clean (SamplesAreClean, with its tracked exceptions); the harness: Compile,
                                 Driver, Analyze, Components (over tools/Shared/GeneratorHost.cs, linked)
  GenericTests.cs                generic components and systems
  GroupTests.cs                  [GroupBy] groups
  HelperLoopTests.cs             methods calling their own helpers, inlined into vector loops, against their twins row by
                                 row
  HierarchyTests.cs              hierarchies
  HotColdTests.cs                EECS091
  HugePagesTests.cs              arrays zeroed over memory used before, on and off, prefaulted or not, small and large,
                                 head and tail; populated arrays resident and keeping their values; a reservation's
                                 columns, entity rows and records resident; reserved worlds alike with and without
                                 prefault; the kernel backing a large array with huge pages; worlds, lookups and
                                 snapshots alike with and without, grown by doubling, reserved and trimmed too
  IndexTests.cs                  value indexes, and unique keys
  InlineArrayTests.cs            a component holding an [InlineArray] beside a plain field: its rows against the method
                                 row by row; loops over inline arrays unrolled into the vector loop against the method
                                 row by row bit for bit: 4, 8 and 16 elements (flat), elements used apart (interleaved),
                                 foreach by ref and by value over a component that is an inline array, an argmin carried
                                 from step to step over a value's array, parallel at 1 and 4 threads, the schedule's
                                 lines, 0 B (EECS055's reasons in GeneratorTests)
  InstantiateTests.cs            Instantiate
  IntrospectionTests.cs          introspection, and Trim, DescribeSchedule
  KernelLookupTests.cs           vector loops reading other entities through Lookup<T> against their twins row by row
  KernelTests.cs                 methods written for one row run as vector loops (Kernel.cs) against their twins:
                                 positional record structs, components of several fields (Interleaved)
  LambdaPassTests.cs             lambda passes, EECS095-097
  LaneMathTests.cs               the math a vector loop lowers: integer division, a float divided by a power of two,
                                 Pow, the throwing Abs / CopySign / Sign, FloatMode.Fast's forms; the Vector128 forms
                                 against the scalar operations; every 32-bit input under the explicit category
                                 Exhaustive
  LanesTests.cs                  Lanes.Compact and Lanes.Selection against a plain walk, every mask shape and length
  LocalityTests.cs               the phase 0 prototypes of docs/plans/locality.md and borrowed-ideas.md
                                 (benchmarks/EasyEcs.Benchmark/Locality linked): each against its plain form, 0 B per
                                 tick
  LookupTests.cs                 Lookup<T>: reads of any entity's component, scheduled as a read, indexes built before
                                 the pass
  MaxRowsTests.cs                [ForEach(MaxRows = N)]: the budget, the plain form against the OnUpdate by hand, rows
                                 destroyed or changed by an earlier row, write-back, a runner past the stack, 0 B; the
                                 generated text, EECS118, EECS119, EECS041's hint
  MoveTests.cs                   moving entities between worlds
  NeighborTests.cs               pair methods: sums bit for bit against every pair, the vector loop against the method
                                 per pair, a cutoff per row and from a singleton, two kinds of entity, the current target
                                 kept through a hint, writes of the neighbor ([Neighbor] ref: a pickup against the loop
                                 over both tables, its stamps), parallel passes bit for bit against the serial ones
                                 (vector, per pair, cutoffs per row and from a singleton, stamps, 0 B), results of the
                                 pass (Reduce<T> against every pair, the chunks combined in order, an [Accumulate]
                                 total), a Mut<T> of the row stamping only the rows it writes, lists of each row's
                                 neighbors against the ranges, moving rows, tables, tombstones, NaN, 0 B
  OrderTests.cs                  ordered tables: [OrderBy] and OrderRows, the sort, clusters, pairs, churn, saves,
                                 parallel passes
  OrderedIndexTests.cs           ordered indexes
  ParallelForTests.cs            ParallelFor (and its lambda form over a list's elements, EECS095/097)
  ParallelTests.cs               parallel systems (WorldOptions.ParallelSystems)
  PartTests.cs                   generated parts, EECS062-069
  PrefabDeclarationTests.cs      [Prefab], EECS102
  PrefetchTests.cs               chases prefetched past the last-level cache: entity chases, index probes of every
                                 representation, buffer chases, parallel twins, fused walks and pairs, lambda passes, bit
                                 for bit with the gate on and off, 0 B, the gates, the generated text, the OS readers
  PresenceTests.cs               presence bits: batch and no-structural-change iteration while rows flip their bits
  QueryTests.cs                  queries
  ReduceTests.cs                 Reduce<T>: partial results per chunk combined in chunk order, replacing the singleton or
                                 added to it for an [Accumulate] total
  RelationTests.cs               relations
  RemovedTests.cs                [Removed<T>], and the destroy log
  RunTests.cs                    entity.Run
  RunnerTests.cs                 [ForEach] methods outside systems: their entry points
  ScratchTests.cs                Scratch<T>: one value per method and thread, created on the thread's first row and kept,
                                 never reset, never shared by two threads at once
  SerializationTests.cs          saves and loads, and restart saves
  SingletonTests.cs              singletons
  SnapshotTests.cs               snapshots
  StreamingStoreTests.cs         write-only columns streamed past the last-level cache: the generated text, the schedule
                                 line, the gate, every form bit for bit against the method with the gate on and off,
                                 parallel chunks after a head of rows, Head and Store at any address, 0 B; large copies:
                                 Copy against memmove at every length and offset, Capture / Restore with the gate forced
                                 on
  StructuralLoopTests.cs         structural changes in vector loops against their scalar twins
  SwitchLoopTests.cs             switch expressions, switch statements and patterns as lane masks against their twins row
                                 by row: NaN, first match, guards, a default section first, narrow lanes, structs,
                                 helpers, a switch that is not exhaustive, parallel, 0 B
  SymmetricPairParallelTests.cs  the parallel pass writing both sides: one result on 1 and 4 threads, sums near the
                                 serial pass's and integer sums equal to the half walk's, moving rows, tombstones, absent
                                 rows, NaN, small tables on the serial pass, a Reduce<T> counting each pair once, stamps
                                 of both sides, item stacks merged alike, 0 B
  SymmetricPairTests.cs          pair methods writing both sides: each unordered pair once against the walk of the half
                                 pairs in the tables' order, bit for bit under SimdMode Auto and Off, moving rows,
                                 tombstones, disabled rows, two tables, NaN, lists on and off, a cutoff per row, counts of
                                 both sides, a Reduce<T> of the pairs, item stacks merged into the lower entity, stamps of
                                 both sides, 0 B
  SystemTests.cs                 systems, and their stats
  TargetPolicyTests.cs           [WhenTargetDestroyed] policies
  TileTests.cs                   tiles: column loads checked once per loop against the indexer, and past the row still
                                 throwing
  TileTests.Steps.cs             Jacobi and [GaussSeidel] steps against the whole grid, sleeping tiles, Restart and
                                 turning sweeps (tile.Sweep), 0 B
  TileTests.Cells.cs             cell methods (Cell<TCell, TTile>) against the whole grid and the tile methods they
                                 replace: a blur per boundary, a halo of 2, Life in bytes (and every neighborhood of 0 to
                                 2 against the rule as written), a 3D seven-point stencil, two cells (one sampled) and two
                                 out cells, blank rows of the driver proved and skipped (and what is not proved: a float
                                 times a sample, a sampled choice), a result per tile (ref Reduce<T> of the tile's
                                 component: sums, Mins and Maxes, proved and skipped too), X in byte lanes and a result of
                                 two-byte fields (a row past 255 cells running cell by cell), rows of 16 bytes at
                                 Vector128 (division, LaneMath), X / Y / Z and values, the skipped rows' fill, a cell
                                 stepping its own tile (out of its type: no next buffer, no commit, blank rows zero or
                                 written), a pass bounded to the layers of an ITileLayers component against the whole
                                 pass (empty, clamped, 1 and 4 threads, 0 B), SimdMode.Off, an offset past the halo, the
                                 schedule, 1 and 4 threads, 0 B; the halo read in place by cross stencils bit for bit
                                 against the copy (box stencils copying it), CellRow's loads at every i and dx of each
                                 width
  TileTests.Halos.cs             kept neighbors (TileNeighborCache) of a by-value Life on a torus of quiet plates against
                                 the whole torus, on 1 and 4 threads, through cells written from outside, removed and
                                 given back, a plate destroyed and made again, every plate's kept neighbors read, 0 B
  UnloadTests.cs                 game logic as a plugin assembly, compiled with the generators and unloaded
  UnusedTests.cs                 methods called through UnsafeAccessor are not reported unused (the analyzer's
                                 suppression, [MeansImplicitUse])
  ViewTests.cs                   [View] parameters, Mut fields, AsX, EECS053 through views, EECS098-101
  VoxelServerTests.cs            the sample's zombies: around a wall and a pit by the scent, stuck walking the straight
                                 line; the field's values
  WorkersTests.cs                the helper threads of parallel work: a fixed set per process, the core's per-thread
                                 state set up on all of them at once
tests/EasyEcs.Aot/               published with Native AOT, warnings as errors, and run by CI: no AOT warning and the
                                 native binary passes its checks
  Program.cs                     the battle server (BattleServer.cs), an ordered table, a pair method (and a parallel one
                                 into a Reduce<T>, and one writing both sides, serial and parallel), the prefetching
                                 twins (probes, buffer chases, a parallel probe; the gate forced on against off) and the
                                 generated forms of an [Accumulate] total, a [Drain] pass, a [ForEach(MaxRows = N)]
                                 budget (consumed, and a write following its row) and enum / bool lanes
tests/EasyEcs.Aot.Library/       a library whose [ForEach] callers live in other assemblies (ForeignBodyTests, EasyEcs.Aot)
  Foreign.cs                     components and helpers whose constructors and bodies its generator records
  Regen.cs                       a generic system of the library over an interface the using assembly implements
```
