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
| Per-row arithmetic over many entities | a batch `[ForEach]` method (span parameters) or `world.ForEachBatch` | Runs of consecutive rows as spans: write SIMD with `Vector<T>` / `MemoryMarshal.Cast`. No structural changes. |
| Ad-hoc iteration over 1–9 components | `world.ForEach<…, TJob>(ref job, filter)` | Struct job, specialized and inlined by the JIT. Keep `Execute` small and branch-light. `ForEachEntity` passes the `Entity`. |
| Ad-hoc loop needing the `Entity` | `foreach (var item in world.Query<…>(filter))` | Zero allocation; `item.Entity`, `item.Component1..n` are refs. (`[ForEach]` methods can take an `Entity` parameter too.) |
| React to a state change (death, level up) | write the state (`Dead` tag or a field) and query it in later systems | One batched pass per consumer, in schedule order; keeps modules decoupled. |
| "Is there any …?" | `world.Any<…>(filter)` | Early exit, no enumeration. |
| "How many …?" | `world.Count<…>(filter)` | Sums table counts (counts presence bits in tables with rows lacking a component). |

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
- Pre-size with `WorldOptions.InitialEntityCapacity` to avoid growing the entity records at run time.
- Use tag components (empty structs) for states (`Dead`, `Stunned`, `Burning`) and filter on them: removing a tag and
  adding it back flips a presence bit, the entity keeps its row.
- Keep `QueryFilter`s in `static readonly` fields (the generator does this for `[ForEach]`).
- One world per thread on servers (for example one per map or zone).

## Don't

- Don't modify a copy of a component and forget to write it back.
- Don't keep `ref`s across `await` or in fields; keep the `Entity` handle.
- Don't store raw entity ids without their version: ids are recycled.
- Don't write lambdas that capture locals in methods that run every update: the closure is allocated at method entry
  even when the lambda's branch is not taken (this exact bug was caught in `World.Update` by `AllocationTests`).
- Don't use LINQ, `params object[]` or string formatting in per-update code.

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
- **`MultiWorldBenchmark.TickAllWorlds`** runs one world per core, each on its own long-lived thread. This is the
  recommended server layout.
- **`ParallelSystemsBenchmark.Tick`** is one update of four `[ForEach]` systems of one priority with disjoint access
  (movement, health and mana regeneration, cooldowns) over 100,000 entities, with `WorldOptions.Parallel` off and on.
  With it on, the four systems form one batch and run at the same time; it shows what a parallel batch costs and
  gains against a sequential update.
- **`MicroBenchmarks`** isolate single operations for diagnosis. `RawArrayWrite` is the no-ECS floor.
- **`EcsComparisonBenchmark`** (project `EasyEcs.Comparison`) ports the headline world to Arch, Friflo.Engine.ECS and
  DefaultEcs: same rules, random rolls, spawn mix and entity counts, single-threaded, each library through its
  recommended fast path. Setup warms every world up and checks that its counters (hits, deaths, spawns, AOI changes,
  entities) match an EasyEcs reference run of the same length within 5%, so every library does the same work. They
  cannot match exactly because of the peers' id recycling (ids feed the random rolls, and each library recycles them
  in its own order) and deferred structural changes (libraries with command buffers apply them at the end of a system,
  not immediately).

Measured with BenchmarkDotNet's default job on .NET 10, on a 4-core Xeon VM at 2.1 GHz.

**Realistic server tick** (the "Allocated" column is per tick):

| Benchmark | Entities | v4.0.1 | v4.1 | Allocated |
|---|---:|---:|---:|---:|
| WorldSimulationBenchmark.Tick | 10,000 | 322 μs | 198 μs | 0 B |
| WorldSimulationBenchmark.Tick | 100,000 | 4.33 ms | 2.29 ms | 0 B |
| MultiWorldBenchmark.TickAllWorlds (4 worlds × 25k) | 100,000 | 1.85 ms | 1.10 ms | 0 B |
| ParallelSystemsBenchmark.Tick, `Parallel` off / on | 100,000 | – | 458 / 333 μs | 0 B |

**Against other ECS libraries** (`EcsComparisonBenchmark`, the headline world ported to each library; ratio against
EasyEcs):

| Library | 10,000 entities | 100,000 entities | Allocated per tick (100k) |
|---|---:|---:|---:|
| EasyEcs 4.1 | 202 μs (1.00) | 2.28 ms (1.00) | 0 B |
| Friflo.Engine.ECS | 169 μs (0.84) | 1.98 ms (0.88) | 0 B |
| DefaultEcs | 293 μs (1.46) | 5.32 ms (2.35) | 61 KB |
| Arch | 457 μs (2.27) | 5.65 ms (2.50) | 573 KB |

Friflo is about 12-16% faster on this world. The per-operation breakdown (`OperationBenchmark`, 100,000 entities)
shows where: EasyEcs is ahead on one-component iteration (41 vs 53 μs), SIMD batches (69 vs 85 μs), filtered
iteration (12 vs 16 μs) and toggling a status component (575 vs 1,018 μs); Friflo is ahead on iteration that passes
the entity (38 vs 76 μs), three-component iteration (70 vs 80 μs) and create/destroy (207 vs 232 μs). Unlike Friflo,
EasyEcs applies structural changes immediately, also inside a pass, with no command buffer.

**Micro benchmarks** (N = 10,000; the v3.1.1 column is BenchmarkDotNet ShortRun on the same machine). "–" means
the operation has no v3.1.1 equivalent (struct jobs, `[ForEach]` systems and handles are new in v4):

| Benchmark | v3.1.1 | v4.0.1 | v4.1 | Allocated (v4.1) |
|---|---:|---:|---:|---:|
| CreateAddDestroy (fresh world) | 5,358 μs | 1,795 μs | 1,396 μs | 1.52 MB (new world, its growth and the benchmark's handle array) |
| Churn: 10 rounds of create (2 components) and destroy in one world | 5,247 μs / 3.19 MB | 1,023 μs | 445 μs | 82 KB (steady state reached in round 1) |
| ToggleComponent: remove + add | 1,185 μs | 338 μs | 192 μs | 0 B |
| QueryIterate (`Query<P,V>` + reduction) | 40.6 μs | 35.3 μs | 31.1 μs | 0 B |
| ForEachIterate (`ForEach` struct job + reduction) | – | 30.6 μs | 30.0 μs | 0 B |
| QueryWrite (`Query`, pure write) | – | 28.3 μs | 22.6 μs | 0 B |
| ForEachSystemWrite (`[ForEach]` system adding `Velocity` to `Position`) | – | 15.1 μs | 9.1 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 24.3 μs | 12.6 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 11.0 μs | 10.9 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 9.1 μs | 10.1 μs | 0 B |
| HandleAccess (`Entity.Get`, ×1000) | – | 3.56 μs | 4.31 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 0.90 μs | 1.12 μs | 0 B |

`ForEachSystemWrite` runs on a world with a single archetype (`Position`, `Velocity`); its columns are 64-byte
aligned, so the generated job beats two plain (unaligned) arrays. The other iteration rows (`QueryIterate`,
`ForEachIterate`, `QueryWrite`, `ForEachWrite`, `QueryIterate3`) run on a world where every other entity also has
`Health`, so the entities are split over two tables. Compare `[ForEach]` and `ForEach` within one world, not across
these rows.

Two rows are slower in 4.1, the price of tables. `HandleAccess` reads a component through a handle: the entity's
record now leads to its table and row (one more dependent load than a column indexed by id), and swap-removes leave
table order unrelated to id order, so random handles touch more cache lines. `QueryIterate3` now checks two tables
instead of one column. `SingletonAccess` reads singletons through the same code as in 4.0.1; the difference is not traced to a change.

## Measuring

Performance numbers come from BenchmarkDotNet only:

```bash
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'               # everything (the numbers above)
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*World*'         # headline: realistic server tick
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*' --job short   # everything, quick
dotnet run -c Release --project EasyEcs.Comparison -- --filter '*'              # the headline world in other ECS libraries
```

`WorldSimulationBenchmark` / `MultiWorldBenchmark` model a busy MMO-style world and must stay at 0 B allocated per
tick. `MicroBenchmarks.RawArrayWrite` is the no-ECS floor. `AllocationTests` (unit tests) guard the zero-allocation
contract on every test run.
