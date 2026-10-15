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
| Ad-hoc iteration over 1–9 components | `world.ForEach<…, TJob>(ref job, filter)` | Struct job, specialized and inlined by the JIT. Keep `Execute` small and branch-light. |
| Ad-hoc loop needing the `Entity` | `foreach (var item in world.Query<…>(filter))` | Zero allocation; `item.Entity`, `item.Component1..n` are refs. (`[ForEach]` methods can take an `Entity` parameter too.) |
| React to a state change (death, level up) | write the state (`Dead` tag or a field) and query it in later systems | One batched pass per consumer, in schedule order; keeps modules decoupled. |
| "Is there any …?" | `world.Any<…>(filter)` | Early exit, no enumeration. |
| "How many …?" | `world.Count<…>(filter)` | Sums archetype counts. |

## Do

- Mutate through refs: `ref var hp = ref item.Component1;`, `ref var hp = ref entity.Get<Health>();`.
- Spawn with all components at once: `world.CreateEntity(a, b, c)` (placed directly in its archetype); change several
  with `entity.Set(a, b)` / `entity.Remove<A, B>()` (one transition).
- Keep components small (a few fields, well under a 64-byte cache line) and split rarely-used data into its own
  component: iteration only loads the columns it reads.
- Leave `WorldOptions.SortArchetypesOnUpdate` on: `Update` re-sorts the archetypes churn disordered, so iteration walks
  columns forward. `CompactArchetypes()` does the same for every archetype at once (after a mass despawn).
- Pre-size with `WorldOptions.InitialEntityCapacity` to avoid growth reallocations at runtime.
- Use tag components (empty structs) for states (`Dead`, `Stunned`, `Dirty`) and filter on them; add/remove follow
  cached transition edges.
- Keep `QueryFilter`s in `static readonly` fields (the generator does this for `[ForEach]`).
- Make `[ForEach]` methods that change the entity's archetype idempotent with filters (`Without<Dead>`).
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
  entities) match an EasyEcs reference run of the same length within 5% (ids feed the random rolls and each library
  recycles ids in its own order), so every library does the same work.

Measured with BenchmarkDotNet's default job on .NET 10, on a 4-core Xeon VM at 2.1 GHz.

**Realistic server tick** (the "Allocated" column is per tick):

| Benchmark | Entities | Mean | Allocated |
|---|---:|---:|---:|
| WorldSimulation.Tick | 10,000 | 252 μs | 0 B |
| WorldSimulation.Tick | 100,000 | 3.01 ms | 0 B |
| MultiWorld.TickAllWorlds (4 worlds × 25k) | 100,000 | 1.27 ms | 0 B |

**Micro benchmarks** (N = 10,000; the v3.1.1 column is BenchmarkDotNet ShortRun on the same machine). "–" means
the operation has no v3.1.1 equivalent (struct jobs, `[ForEach]` systems and handles are new in v4). Every
operation that exists in both versions is faster in v4 and allocates less or nothing:

| Benchmark | v3.1.1 | v4.1 | Allocated (v4.1) |
|---|---:|---:|---:|
| CreateAddDestroy (fresh world) | 5,358 μs | 1,600 μs | 1.57 MB (array growth of the new world) |
| Churn: 10 rounds of create (2 components) and destroy in one world | 5,247 μs / 3.19 MB | 617 μs | 72 KB (steady state reached in round 1) |
| ToggleComponent: remove + add | 1,185 μs | 332 μs | 0 B |
| QueryIterate (`Query<P,V>` + reduction) | 40.6 μs | 33.6 μs | 0 B |
| ForEachIterate (`ForEach` struct job + reduction) | – | 28.5 μs | 0 B |
| QueryWrite (`Query`, pure write) | – | 27.4 μs | 0 B |
| ForEachSystemWrite (`[ForEach]` system) | – | 15.2 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 22.9 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 10.6 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 8.8 μs | 0 B |
| HandleAccess (`Entity.Get`, ×1000) | – | 3.21 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 0.89 μs | 0 B |

`ForEachSystemWrite` runs on a world with a single archetype (`Position`, `Velocity`). The other iteration rows
(`QueryIterate`, `ForEachIterate`, `QueryWrite`, `ForEachWrite`, `QueryIterate3`) run on a world where every other
entity also has `Health`, so two archetypes interleave by id. Compare `[ForEach]` and `ForEach` within one world, not
across these rows.

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
