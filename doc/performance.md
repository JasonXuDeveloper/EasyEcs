# Performance guide

[Docs index](README.md) · Related: [API reference](api.md) · [Architecture](architecture.md)

- [Choosing an iteration API](#choosing-an-iteration-api)
- [Do](#do) / [Don't](#dont)
- [Benchmarks](#benchmarks)
- [Measuring](#measuring)

## Choosing an iteration API

| Need | Use | Notes |
|---|---|---|
| Per-entity logic of a system | `[ForEach]` method | Generated struct job; same speed as a hand-written `ForEach` (the micro rows below differ because they run on different worlds). `[With<T>]` / `[Without<T>]` filters. |
| Heavy per-entity work over tens of thousands of entities | `[ForEach(Parallel = true)]` | Chunks of rows on several threads (2.1x on 4 cores in `ParallelForEachBenchmark`); no structural changes, singletons only `in`. |
| Per-row arithmetic over many entities | a batch `[ForEach]` method (span parameters) or `world.ForEachBatch` | Runs of consecutive rows as spans: write SIMD with `Vector<T>` / `MemoryMarshal.Cast`. No structural changes. |
| Ad-hoc iteration over 1–9 components | `world.ForEach<…, TJob>(ref job, filter)` | Struct job, specialized and inlined by the JIT. Keep `Execute` small and branch-light. `ForEachEntity` passes the `Entity`. |
| Ad-hoc loop needing the `Entity` | `foreach (var item in world.Query<…>(filter))` | Zero allocation; `item.Entity`, `item.Component1..n` are refs. (`[ForEach]` methods can take an `Entity` parameter too.) |
| React to a state change (death, level up) | write the state (`Dead` tag or a field) and query it in later systems | One batched pass per consumer, in schedule order; keeps modules decoupled. |
| "Is there any …?" | `world.Any<…>(filter)` | Early exit, no enumeration. |
| "How many …?" | `world.Count<…>(filter)` | Sums table counts (counts presence bits in tables with rows lacking a component). |
| Only the rows written since a system last ran | `[ForEach, Changed<T>]` (`T : ITrackChanges`) | Skips unwritten tables, 4,096-row blocks and 64-row words; the method runs for changed rows only. |
| Find entities by a value or by the entity they point at | `world.Index<T, TKey>()` (`T : IIndexed<TKey>`) | `Count` O(1), `Find` one step per match; catches up with written rows on lookup. |

## Do

- Mutate through refs: `ref var hp = ref item.Component1;`, `ref var hp = ref entity.Get<Health>();`.
- Spawn with all components at once: `world.CreateEntity(a, b, c)` (placed directly in its archetype); change several
  with `entity.Set(a, b)` / `entity.Remove<A, B>()` (one transition).
- Keep components small (a few fields, well under a 64-byte cache line) and split rarely-used data into its own
  component: iteration only loads the columns it reads.
- Keep `[ForEach]` methods that make no structural change `static`, without a `World` parameter and with read-only use
  of their `Entity` (`Id`, `Has`, `Get`, ...): the generator marks them `INoStructuralChanges`, and they iterate with
  no per-entity bookkeeping.
- Write arithmetic over many rows as a batch method (`Span<T>` / `ReadOnlySpan<T>` parameters) and vectorize it:
  a table whose rows all match is one call, and unmanaged columns start on a 64-byte boundary.
- Spawn and clear waves with `world.CreateEntities(span, a, b)` / `world.DestroyEntities<T>(filter)`: one pass
  through the structural gate for the batch (2.4x faster than one call per entity in `ChurnBatch`).
- Pre-size with `WorldOptions.InitialEntityCapacity` to avoid growing the entity records at run time.
- Use tag components (empty structs) for states (`Dead`, `Stunned`, `Burning`) and filter on them: removing a tag and
  adding it back flips a presence bit, the entity keeps its row.
- Keep `QueryFilter`s in `static readonly` fields (the generator does this for `[ForEach]`).
- Mark a component `ITrackChanges` when a system reacts to the few rows written since its last run (replication,
  persistence) and give that method `[Changed<T>]` instead of scanning every row.
- Look entities up by value with an indexed component (`IIndexed<TKey>`, `world.Index<T, TKey>()`) instead of a
  dictionary you keep in sync by hand; keep the `ValueIndex` in a local or field.
- Take an indexed component by `in` when you only read it: a `ref` counts as a write, so the row is re-indexed on
  the next lookup.
- One world per thread on servers (for example one per map or zone).

## Don't

- Don't modify a copy of a component and forget to write it back.
- Don't keep `ref`s across `await` or in fields; keep the `Entity` handle.
- Don't store raw entity ids without their version: ids are recycled.
- Don't write lambdas that capture locals in methods that run every update: the closure is allocated at method entry
  even when the lambda's branch is not taken (this exact bug was caught in `World.Update` by `AllocationTests`).
- Don't use LINQ, `params object[]` or string formatting in per-update code.
- Don't use a value index, or destroy entities / remove indexed components, from a thread other than the world's
  owner while the world has an index: the index is updated by those changes and is not synchronized.
- Don't take an `Entity` or a singleton parameter a `[ForEach]` method does not need: the JIT computes either for every
  row, even when only a rare branch uses it, which doubles the cost of a light loop (about 0.4 ns per row).
- Don't take a tracked component (`ITrackChanges`) by `ref` when the method only reads it: every visited row then
  counts as changed. Use `in`.

## Benchmarks

All numbers come from BenchmarkDotNet. The headline benchmarks simulate a real server tick, not a toy loop:

- **`WorldSimulationBenchmark.Tick`** is one tick of a world with 30% players, 70% NPCs, and projectiles on top. The
  tick runs:
  - movement, skipping entities that are `Stunned` or `Dead`
  - an AOI grid
  - combat that randomly adds `Burning` / `Stunned`
  - status expiry that removes those components
  - death, which adds `Dead` and updates the world statistics singleton
  - respawn: dead NPCs are replaced by fresh ones, dead players respawn in place
  - projectile spawn (about 5% of players per tick) and expiry

  Setup asserts that the world is busy: hits, deaths, spawns and AOI changes all happen.
- **`BattleServerBenchmark.Tick`** is one tick of the README's battle server (`BattleServer.cs`), which uses every
  feature: a parallel `[ForEach]` (NPC steering), two parallel systems of one priority (movement and mana, batch
  methods), statuses added and removed mid-pass, `WithAny`, deaths, respawns and projectiles, a value index (players
  per zone) and a relation (projectiles of a player), an update interval, per-system timings, and replication of
  changed health every third tick (`[Changed<Health>]` in its own system group). `BattleServerTests` check the same
  world and the host's queries, struct jobs, snapshots and error handling.
- **`MultiWorldBenchmark.TickAllWorlds`** runs one world per core, each on its own long-lived thread. This is the
  recommended server layout.
- **`ParallelSystemsBenchmark.Tick`** is one update of four `[ForEach]` systems of one priority with disjoint access
  (movement, health and mana regeneration, cooldowns) over 100,000 entities, with `WorldOptions.Parallel` off and on.
  With it on, the four systems form one batch and run at the same time; it shows what a parallel batch costs and
  gains against a sequential update.
- **`ParallelForEachBenchmark.Tick`** is one heavy `[ForEach]` method (steering towards the nearest of 16 waypoints)
  over 100,000 entities, plain and with `Parallel = true`, whose chunks of rows run on all cores.
- **`ChangeTrackingBenchmark.Tick`** replicates changed state: 10 (a quiet tick) or 1,000 of 100,000 entities get a
  new health per tick, then one system copies health into a replica, with `[Changed<Health>]` or by visiting every
  row. `QueryTick` does the same with an ad-hoc `Query` (a `Changed<Health>()` filter, or every row).
- **`IndexBenchmark.TradeAndCount`** uses a relation as an inventory: 100,000 items each point at one of 1,000 owners;
  100 items change hands, then 100 inventories are counted through the index.
- **`SnapshotBenchmark`** captures a world of 100,000 created entities (two tables, a third destroyed, a singleton)
  into a new snapshot and restores a snapshot in place.
- **`MicroBenchmarks`** isolate single operations for diagnosis. `RawArrayWrite` is the no-ECS floor.
- **`EcsComparisonBenchmark`** (project `EasyEcs.Comparison`) ports the headline world to Arch, Friflo.Engine.ECS and
  DefaultEcs: same rules, random rolls, spawn mix and entity counts, single-threaded, each library through its
  recommended fast path. Setup warms every world up and checks that its counters (hits, deaths, spawns, AOI changes,
  entities) match an EasyEcs reference run of the same length within 5%, so every library does the same work. They
  cannot match exactly because of the peers' id recycling (ids feed the random rolls, and each library recycles them
  in its own order) and deferred structural changes (libraries with command buffers apply them at the end of a system,
  not immediately).
- **`ChangeTrackingComparison`** and **`IndexComparison`** (project `EasyEcs.Comparison`, `FeatureBenchmark.cs`) run
  the same jobs (1,000 of 100,000 entities written per tick; 1 or 100 trades, then 100 inventory counts) in each
  library its own way: the built-in feature where it has one (DefaultEcs `WhenChanged<T>()` sets and
  `EntityMultiMap<T>`, Friflo link components), else the usual workaround (a scan comparing values, or a dirty tag
  added on write and removed after the copy). Setup checks every variant's result (replicas match the source; each
  owner's count matches the items pointing at it), and `--check` runs those checks without measuring.

Measured with BenchmarkDotNet and the repository's job (`EasyEcs.Benchmark/BenchmarkConfig.cs`: 2 launches, 6
warmups, 8 iterations of 250 ms, call counting from the start) on .NET 10, on a 4-core Xeon VM at 2.1 GHz.

**Realistic server tick** (the "Allocated" column is per tick):

| Benchmark | Entities | v4.0.1 | v4.1 | Allocated |
|---|---:|---:|---:|---:|
| WorldSimulationBenchmark.Tick | 10,000 | 322 μs | 184 μs | 0 B |
| WorldSimulationBenchmark.Tick | 100,000 | 4.33 ms | 2.01 ms | 0 B |
| MultiWorldBenchmark.TickAllWorlds (4 worlds × 25k) | 100,000 | 1.85 ms | 1.08 ms | 0 B |
| BattleServerBenchmark.Tick (every feature) | 100,000 | – | 2.36 ms | 0 B |
| ParallelSystemsBenchmark.Tick, `Parallel` off / on | 100,000 | – | 394 / 243 μs | 0 B |
| ParallelForEachBenchmark.Tick, `[ForEach]` / `[ForEach(Parallel = true)]` | 100,000 | – | 1,601 / 765 μs | 0 B |
| ChangeTrackingBenchmark.Tick, 10 rows written: every row / `[Changed<Health>]` | 100,000 | – | 40-110 μs / 0.55 μs | 0 B |
| ChangeTrackingBenchmark.QueryTick, 10 rows written: every row / `Changed<Health>()` | 100,000 | – | 470 μs / 0.79 μs | 0 B |
| ChangeTrackingBenchmark.Tick, 1,000 rows written: every row / `[Changed<Health>]` | 100,000 | – | 60-110 μs / 36.1 μs | 0 B |
| ChangeTrackingBenchmark.QueryTick, 1,000 rows written: every row / `Changed<Health>()` | 100,000 | – | 503 μs / 50.9 μs | 0 B |
| IndexBenchmark.TradeAndCount (100 trades, 100 inventory counts) | 100,000 | – | 11.5 μs | 0 B |
| SnapshotBenchmark.Capture (a new snapshot) / Restore | 100,000 | – | 724 / 426 μs | 2.8 MB / 0 B |
| BufferBenchmark.Tick (1,000 of 100,000 bags written, then sent): `Buffer<int>` / `List<int>` in a tracked component | 100,000 | – | 55.1 / 61.5 μs | 0 B |

**Against other ECS libraries** (`EcsComparisonBenchmark`, the headline world ported to each library; ratio against
EasyEcs):

| Library | 10,000 entities | 100,000 entities | Allocated per tick (100k) |
|---|---:|---:|---:|
| EasyEcs 4.1 | 194 μs (1.00) | 2.00 ms (1.00) | 0 B |
| Friflo.Engine.ECS | 159 μs (0.82) | 1.79 ms (0.89) | 0 B |
| DefaultEcs | 289 μs (1.49) | 5.31 ms (2.66) | 62 KB |
| Arch | 474 μs (2.45) | 5.45 ms (2.73) | 550 KB |

Friflo is about 10% faster at both sizes (5-18% across runs on this VM; the table is one run). The per-operation
breakdown (`OperationBenchmark`, 100,000 entities) shows where: EasyEcs is ahead on one-component iteration (38 vs
67 μs), SIMD batches (71 vs 81 μs), filtered iteration (11.5 vs 17.0 μs) and toggling a status component (509 vs
986 μs); Friflo is ahead on iteration that passes the entity (38 vs 54 μs), three-component iteration (68 vs 77 μs)
and create/destroy (203 vs 226 μs); two-component iteration (245 vs 249 μs) and singletons (215 vs 224 μs) are even.
Unlike Friflo, EasyEcs applies structural changes immediately, also inside a pass, with no command buffer. Change
tracking and index results against each library are in [the comparison](comparison.md).

**Micro benchmarks** (N = 10,000; the v3.1.1 column is BenchmarkDotNet ShortRun on the same machine). "–" means
the operation has no v3.1.1 equivalent (struct jobs, `[ForEach]` systems and handles are new in v4):

| Benchmark | v3.1.1 | v4.0.1 | v4.1 | Allocated (v4.1) |
|---|---:|---:|---:|---:|
| CreateAddDestroy (fresh world) | 5,358 μs | 1,795 μs | 1,156 μs | 1.52 MB (new world, its growth and the benchmark's handle array) |
| Churn: 10 rounds of create (2 components) and destroy in one warm world | 5,247 μs / 3.19 MB | 1,023 μs | 381 μs | 0 B |
| ChurnBatch: the same rounds with `CreateEntities` / `DestroyEntities` | – | – | 162 μs | 0 B |
| ToggleComponent: remove + add | 1,185 μs | 338 μs | 194 μs | 0 B |
| QueryIterate (`Query<P,V>` + reduction) | 40.6 μs | 35.3 μs | 28.7 μs | 0 B |
| ForEachIterate (`ForEach` struct job + reduction) | – | 30.6 μs | 12.8 μs | 0 B |
| QueryWrite (`Query`, pure write) | – | 28.3 μs | 18.3 μs | 0 B |
| ForEachSystemWrite (`[ForEach]` system adding `Velocity` to `Position`) | – | 15.1 μs | 8.2 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 24.3 μs | 9.7 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 11.0 μs | 10.8 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 9.1 μs | 9.4 μs | 0 B |
| HandleAccess (`Entity.Get`, ×1000) | – | 3.56 μs | 3.23 μs | 0 B |
| TryGetEntity (`Entity.TryGet`, ×1000) | – | – | 3.45 μs | 0 B |
| TrackedHandleWrite (`Entity.Get` of a tracked component, ×1000) | – | – | 8.66 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 0.90 μs | 0.97 μs | 0 B |
| CountAndAny (`Count` + `Any` with a filter, ×1000) | – | – | 49.9 μs | 0 B |

`ForEachSystemWrite` runs on a world with a single archetype (`Position`, `Velocity`); its columns are 64-byte
aligned, so the generated job beats two plain (unaligned) arrays. The other iteration rows (`QueryIterate`,
`ForEachIterate`, `QueryWrite`, `ForEachWrite`, `QueryIterate3`) run on a world where every other entity also has
`Health`, so the entities are split over two tables. Compare `[ForEach]` and `ForEach` within one world, not across
these rows.

The "every row" rows of `ChangeTrackingBenchmark.Tick` are given as ranges: the same code measures anywhere in them
from run to run on this VM (how the 100,000 rows land in physical memory), while the `[Changed]` rows are stable.

## Measuring

Performance numbers come from BenchmarkDotNet only:

```bash
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'               # everything (the numbers above)
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*World*'         # headline: realistic server tick
dotnet run -c Release --project EasyEcs.Comparison -- --filter '*'              # other ECS libraries: headline world, operations, features
dotnet run -c Release --project EasyEcs.Comparison -- --check                   # correctness only: counters and feature results match
```

`WorldSimulationBenchmark` / `MultiWorldBenchmark` model a busy MMO-style world and must stay at 0 B allocated per
tick. `MicroBenchmarks.RawArrayWrite` is the no-ECS floor. `AllocationTests` (unit tests) guard the zero-allocation
contract on every test run.
