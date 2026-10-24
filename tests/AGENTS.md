# Tests: file map

> Read when: adding or changing a test, or finding where a feature is tested.

NUnit unit tests and the Native AOT check. Every bug fix adds a regression test (`feature`).

When you add, rename or delete a file here, update its line (`verify` checks every file is listed).

```
tests/EasyEcs.UnitTest/        NUnit, one file per feature: Entity, Component, Presence, Query, System (and stats),
                               ForEachSystem (and Slices), Singleton, Concurrency, Parallel, Runner, Run (entity.Run),
                               ForEntity, Consume, Fusion (fused walks against FusionMode.Off twins, the exclusions), Form (EECS080 / EECS084),
                               Changed (and Mut, ChangedAny), Removed (and the destroy log), Instantiate, DynamicBuffer,
                               Index (and unique keys), Tile, OrderedIndex, Relation, Hierarchy, TargetPolicy, Group, Cleanup,
                               Disable, Bulk, Move, StructuralLoop (structural changes in vector loops against their scalar twins), Part (generated parts, EECS062-069), Snapshot, Serialization (and restart saves), Unload, Introspection
                               (and Trim, DescribeSchedule), Generic, Allocation (0 B budget), Emit (and EECS092), HotCold (EECS091), LambdaPass (lambda passes, EECS095-097), View ([View] parameters, Mut fields, AsX, EECS053 through views, EECS098-101), PrefabDeclaration ([Prefab], EECS102), ForParallel, EntityRandom,
                               Generator (diagnostics + caching), CodeFix, BattleServer (the world of
                               docs/content/example.mdx as a whole, linked from EasyEcs.Benchmark); Fixtures.cs holds
                               the shared test components
tests/EasyEcs.Aot/             the battle server (BattleServer.cs) published with Native AOT, warnings as errors, and
                               run by CI: no AOT warning and the native binary passes its checks
```
