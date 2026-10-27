# Tests: file map

> Read when: adding or changing a test, or finding where a feature is tested.

NUnit unit tests and the Native AOT check. Every bug fix adds a regression test (`feature`).

When you add, rename or delete a file here, update its line (`verify` checks every file is listed).

An allocation check measures through `Allocations.Of(...)` or starts from `Allocations.Baseline()` (Fixtures.cs), never
from a raw `GC.GetAllocatedBytesForCurrentThread()`: a background collection ending in the window adds the unused
rest of the thread's allocation context to the count although nothing was allocated (D128).

```
tests/EasyEcs.UnitTest/        NUnit, one file per feature: Entity, Component, Presence, Query, System (and stats),
                               ForEachSystem (and Slices), Singleton, Concurrency, Parallel, Runner, Run (entity.Run),
                               ForEntity, Consume, Fusion (fused walks against FusionMode.Off twins, the exclusions), Form (EECS080 / EECS084),
                               Changed (and Mut, ChangedAny), Removed (and the destroy log), Instantiate, DynamicBuffer,
                               Index (and unique keys), Tile, OrderedIndex, Order (ordered tables: [OrderBy] and OrderRows,
                               the sort, blocks, pairs, churn, saves, parallel passes), Relation, Hierarchy, TargetPolicy, Group, Cleanup,
                               Disable, Bulk, Move, StructuralLoop (structural changes in vector loops against their scalar twins), KernelLookup (vector loops reading other entities through Lookup<T> against their twins row by row), HelperLoop (methods calling their own helpers, inlined into vector loops, against their twins row by row), Neighbor (pair methods: sums bit for bit against every pair, the vector loop against the method per pair, a cutoff per row and from a singleton, two kinds of entity, the current target kept through a hint, writes of the neighbor ([Neighbor] ref: a pickup against the loop over both tables, its stamps), parallel passes bit for bit against the serial ones (vector, per pair, cutoffs per row and from a singleton, stamps, 0 B), moving rows, tables, tombstones, NaN, 0 B), Prefetch (chases prefetched past the last-level cache: entity chases, index probes of every representation, buffer chases, parallel twins, fused walks and pairs, lambda passes, bit for bit with the gate on and off, 0 B, the gates, the generated text, the OS readers), Part (generated parts, EECS062-069), Snapshot, Serialization (and restart saves), Unload, Introspection
                               (and Trim, DescribeSchedule), Generic, Allocation (0 B budget), Emit (and EECS092), Record ([ForEachRecord] passes over Emitted<T> against the hand-written walk, EECS108-109), HotCold (EECS091), LambdaPass (lambda passes, EECS095-097), View ([View] parameters, Mut fields, AsX, EECS053 through views, EECS098-101), PrefabDeclaration ([Prefab], EECS102), ForParallel, EntityRandom,
                               Lanes (Lanes.Compact and Lanes.RowNumbers against a plain walk, every mask shape and length),
                               LaneMath (the math a vector loop lowers: integer division, a float divided by a power of two, Pow, the throwing
                               Abs / CopySign / Sign, FloatMode.Fast's forms; every 32-bit input under the explicit category Exhaustive),
                               Generator (generated code, the vector loops and EECS055 / EECS056's reasons, caching; the
                               harness: Compile, Driver, Analyze, Components), Analyzer (the rules, one file per analyzer
                               file: .Declarations, .Systems, .Usage, .Parallel, .Parts, .Forms (and EECS103's idioms and guards), .Pairs (EECS105-107), .Performance (costs,
                               lambda passes, view fields); what each rule reports, the edges it branches on and the code
                               it leaves alone; AnalyzerTests.cs: Diagnose / Costs / All, Extra, AssertReports /
                               AssertClean / Count), CodeFix (each
                               fix compiles, adds no diagnostic and is not offered again), BattleServer (the world of
                               docs/content/example.mdx as a whole, linked from EasyEcs.Benchmark), Locality (the phase 0
                               prototypes of docs/plans/locality.md and borrowed-ideas.md, benchmarks/EasyEcs.Benchmark/Locality
                               linked: each against its plain form, 0 B per tick); Fixtures.cs holds
                               the shared test components
tests/EasyEcs.Aot/             the battle server (BattleServer.cs), an ordered table, a pair method, the prefetching twins (probes, buffer
                               chases, a parallel probe; the gate forced on against off) and the generated forms of an
                               [Accumulate] total, a [ForEachRecord] pass and enum / bool lanes published with Native AOT, warnings as errors, and
                               run by CI: no AOT warning and the native binary passes its checks
```
