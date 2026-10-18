# Performance guide

[Docs index](README.md) · Related: [API reference](api.md) · [Architecture](architecture.md)

- [Choosing an iteration API](#choosing-an-iteration-api)
- [Regular code and the fastest code](#regular-code-and-the-fastest-code)
- The checklist: [Do](#do) / [Don't](#dont)
- [Benchmarks](#benchmarks)
- [Measuring](#measuring)

## Choosing an iteration API

[Which API](guide/queries.md#which-api) in the queries guide maps each need to its call: `[ForEach]` methods for the
logic of systems, struct jobs for hot loops outside them, `Query` for convenience, `Count` / `Any` instead of loops,
`[Changed<T>]` / `[Removed<T>]` and indexes instead of scans.

## Regular code and the fastest code

The regular way is right for most systems: it is short, and a `[ForEach]` method is already a generated, inlined
struct job. When a system shows up in `MeasureSystems` timings, these rewrites are what the benchmarks below measure.

| Regular | Fastest | Measured |
|---|---|---|
| A per-entity `[ForEach]` doing arithmetic with branches | A batch method vectorized with `Vector<T>`, branches as masks | 251 → 64 μs (100,000 entities, `OperationBenchmark`) |
| `foreach (var item in world.Query<…>())` in a hot ad-hoc loop | A struct job with `world.ForEach` | 28.9 → 12.6 μs (10,000 entities, micro benchmarks) |
| A `bool` flag tested in every row | A tag component with `[With<T>]` / `[Without<T>]` | flagged rows skipped 64 at a time; toggling flips a bit |
| Scanning every row for the few that changed | `ITrackChanges` + `[Changed<T>]` | 39-110 → 0.58 μs (10 of 100,000 rows written) |
| `CreateEntity` / `Destroy` in a loop | `CreateEntities` / `DestroyEntities` (`Instantiate(prefab, span)` for a kind defined as a [prefab](guide/prefabs.md)) | 382 → 158 μs (10 rounds of 10,000) |
| A marker tag added next to each removal, or a scan comparing with what was sent | `ITrackChanges` + `[Removed<T>]` | TBD (`RemovalComparison`) |
| Heavy per-entity work on one thread | `[ForEach(Parallel = true)]` | 1,601 → 765 μs (100,000 entities, 4 cores) |

### Arithmetic over many rows: vectorize a batch method

The JIT does not vectorize loops by itself, and data-dependent branches mispredict on every other row. The regular
system:

```csharp
public class BounceSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Move(ref Position p, ref Velocity v)
    {
        p = new Position(p.X + v.X, p.Y + v.Y);
        if (p.X < 0 || p.X > 4096) v = v with { X = -v.X };
        if (p.Y < 0 || p.Y > 4096) v = v with { Y = -v.Y };
    }
}
```

The fastest one takes spans (runs of consecutive rows; a table whose rows all match is one call), reads two-float
components as one float span (X, Y, X, Y, ...), so every lane of a `Vector<float>` is one coordinate, and turns the
branches into a mask:

```csharp
using System.Numerics;
using System.Runtime.InteropServices;

public class BounceSimdSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Move(Span<Position> positions, Span<Velocity> velocities)
    {
        var p = MemoryMarshal.Cast<Position, float>(positions);
        var v = MemoryMarshal.Cast<Velocity, float>(velocities);
        var max = new Vector<float>(4096f);
        int i = 0;
        for (; i <= p.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            var velocity = new Vector<float>(v[i..]);
            var position = new Vector<float>(p[i..]) + velocity;
            var outside = Vector.LessThan(position, Vector<float>.Zero) | Vector.GreaterThan(position, max);
            position.CopyTo(p[i..]);
            Vector.ConditionalSelect(outside, -velocity, velocity).CopyTo(v[i..]);
        }

        for (; i < p.Length; i++) // the rows left over
        {
            p[i] += v[i];
            if (p[i] < 0 || p[i] > 4096) v[i] = -v[i];
        }
    }
}
```

The cast is valid because the components hold only floats (no padding, no references). Unmanaged columns start on a
64-byte boundary. A batch method makes no structural change and reads entities through its `EntitySpan` only where it needs them; a scalar loop over spans gains
little over the per-entity method, so write one to vectorize it.

### Ad-hoc loops: a struct job

`world.Query` is the convenient loop for tools and request handlers. For a loop that runs every tick outside a system,
write a struct job: `world.ForEach` specializes and inlines it per table. Mark it `INoStructuralChanges` and declare
its `Writes` ([what a job declares](guide/queries.md#what-a-job-declares-writes)).

```csharp
public struct EnergyJob : IForEach<Velocity>, INoStructuralChanges   // no structural change: the lean loop
{
    public float Total;   // a value field stays in a register during the pass and is copied back once

    public static int Writes => 0;   // reads only: no row counts as changed

    public void Execute(ref Velocity v) => Total += v.X * v.X + v.Y * v.Y;
}
```

```csharp
var job = new EnergyJob();
world.ForEach<Velocity, EnergyJob>(ref job);
float energy = job.Total;
```

Declare the job a `ref struct` when it must hold a `ref` or a `Span` (an output buffer, a caller's array). Generated
jobs do this for singleton parameters. `ForEach`, `ForEachEntity` and `ForEachBatch` accept ref struct jobs. The
`*Parallel` calls do not, because each thread gets its own copy of the job. For a total, a value field is faster than
a `ref` field: a write through a `ref` goes to memory on every row.

```csharp
public ref struct CollectJob : IForEach<Position>, INoStructuralChanges
{
    public Span<float> Xs;   // the caller's buffer, at least as long as the matching rows
    public int Count;

    public static int Writes => 0;

    public void Execute(ref Position p) => Xs[Count++] = p.X;
}
```

### States: tags, not flags

A flag costs a load and a branch in every row of every system that checks it. A tag component is a filter: rows
without it are skipped 64 at a time, and adding or removing it flips a presence bit (the entity keeps its row).

```csharp
public record struct Unit(int Hp, bool Stunned) : IComponent;   // regular: every system tests the flag

public class StunnedFlagSystem : EcsSystem
{
    [ForEach]
    static void Act(ref Unit unit)
    {
        if (unit.Stunned) return;
        unit.Hp++;
    }
}

public class StunnedTagSystem : EcsSystem   // fastest: the filter does the test
{
    [ForEach, Without<Stunned>]
    static void Act(ref Health hp) => hp = hp with { Value = hp.Value + 1 };
}
```

### The few rows that changed: `[Changed<T>]`

A replication or persistence system that scans every row pays for the rows nobody wrote. Mark the component
`ITrackChanges` and filter with `[Changed<T>]`: unwritten tables, 4,096-row blocks and 64-row words are skipped.
Take the component by `in` so the visit does not count as a write.

It pays when few rows are written, or when the written rows are close together: at 1,000 of 100,000 rows
written per tick, 36 μs against 60-110 μs for a scan. When writes are spread over most 64-row words (a tenth of the
rows, scattered), nothing is skipped and testing the change ticks costs about what reading the column does: a cheap
check over such a column is as fast as a plain scan, and simpler (the battle server's death check takes 256 μs
either way, with a tenth of its 100,000 healths written per tick).

```csharp
public record struct Shield(int Value) : IComponent, ITrackChanges;
public record struct ShieldReplica(int Value) : IComponent;

public class ShieldReplicationSystem : EcsSystem
{
    [ForEach, Changed<Shield>]
    static void Send(in Shield shield, ref ShieldReplica replica) => replica = new ShieldReplica(shield.Value);
}
```

### What went away: `[Removed<T>]`

Replicating a removal (a fire that went out) with a marker tag costs a structural change per removal and a pass over
the tags; comparing every row with what clients last received costs a scan. A tracked component's removal already
stamps its row, so `[Removed<T>]` visits only the entities that lost it, with the same skipping as `[Changed<T>]`:

```csharp
public class ShieldLossSystem : EcsSystem
{
    [ForEach, Removed<Shield>]                       // Shield : ITrackChanges (above)
    static void Send(ref ShieldReplica replica) => replica = new ShieldReplica(0);
}
```

### The entity only where the row needs it

An `Entity` parameter reads the row's id for every row, even when only a rare branch uses it. When the entities a
system acts on can be told by a component, filter on it (`[With<Dead>]`) instead of testing every row. A cheap test
of a column that many rows write every tick (a death check on `Health`) is still fastest as a plain scan: see above
for when `[Changed<T>]` pays.

### Waves of entities: bulk calls

```csharp
var wave = new Entity[1_000];
world.CreateEntities(wave, new Position(0, 0), new Velocity(1, 0));   // one pass through the structural gate
world.Instantiate(Kinds.Goblin, wave);                                // a wave of a prefab (guide/prefabs.md), the same way
int removed = world.DestroyEntities<Dead>();                          // every dead entity, one pass
```

A [prefab](guide/prefabs.md) built once (`static readonly`) costs one row copy per instance; the world finds its
target table once and caches it.

Several components at once also take one transition: `entity.Set(a, b)`, `entity.Remove<A, B>()`.

## Do

The checklist for code that runs every tick. Find the system worth the effort first: `WorldOptions.MeasureSystems`
times every system run ([measuring systems](guide/systems.md#measuring-systems)).

- Mutate through refs: `ref var hp = ref item.Component1;`, `ref var hp = ref entity.Get<Health>();`.
- Spawn with all components at once: `world.CreateEntity(a, b, c)` (placed directly in its archetype), or
  `world.Instantiate(prefab)` for a kind defined once; change several with `entity.Set(a, b)` /
  `entity.Remove<A, B>()` (one transition).
- Read components with `in` parameters, `entity.Read<T>()` or the generated property, write through `ref` parameters
  or `entity.Get<T>()`: a `ref` access counts as a write of a tracked component. A `Query` loop counts every visited
  row of a tracked component it queries as written; a struct job that only reads declares `static int Writes => 0`.
- Mark struct jobs that never add, remove, create or destroy `INoStructuralChanges`: their passes skip the
  bookkeeping that keeps structural changes safe.
- Keep the handle of a singleton you touch outside systems in a `SingletonRef<T>` (no lookup per access); in systems,
  take it as a parameter (resolved once per pass).
- Give systems that need not run every update a cadence: `UpdateInterval`, or a system group updated when needed
  (`Update<TGroup>()`), instead of a counter checked in the method.
- Keep components small (a few fields, well under a 64-byte cache line) and split rarely-used data into its own
  component: iteration only loads the columns it reads.
- Keep `[ForEach]` methods that make no structural change `static`, without a `World` parameter and with read-only use
  of their `Entity` (`Id`, `Has`, `Get`, ...): the generator marks them `INoStructuralChanges`, and they iterate with
  no per-entity bookkeeping.
- Vectorize arithmetic in batch methods (span parameters, `Vector<T>`), create and destroy waves of entities in bulk
  (`CreateEntities`, `Instantiate(prefab, span)`, `DestroyEntities`), use tags for states, `[Changed<T>]` for the few
  rows that changed and `[Removed<T>]` for what went away: see
  [regular code and the fastest code](#regular-code-and-the-fastest-code).
- Ask `world.Count` / `world.Any` instead of looping to count or to find one match.
- Reserve room in a `DynamicBuffer<T>` whose size is known (`EnsureCapacity`): writes within the capacity allocate
  nothing.
- Pre-size with `WorldOptions.InitialEntityCapacity` (or `world.EnsureEntityCapacity(n)`) to avoid growing the
  entity records at run time.
- Keep `QueryFilter`s in `static readonly` fields (the generator does this for `[ForEach]`).
- Look entities up by value with an indexed component (`IIndexed<TKey>`, `world.Index<T, TKey>()`) instead of a
  dictionary you keep in sync by hand; keep the `ValueIndex` in a local or field.
- Write an indexed component with `Set` only when its key changes (`if (zone.Id != id) player.Set(new Zone(id))`):
  an unchanged key re-links nothing, but the write still costs a lookup in the index's nodes.
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
- Don't take an `Entity` or a singleton parameter a `[ForEach]` method does not need: the generated loop loads it
  for every row, even when only a rare branch uses it (the JIT does not move a load into a branch).
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
  feature: a parallel `[ForEach]` (NPC steering), two systems that run at once (movement, vectorized, and mana), statuses added and removed mid-pass, `WithAny`, deaths, respawns and projectiles, a value index (players
  per zone) and a relation (projectiles of a player), NPCs spawned from prefabs, an update interval, per-system timings,
  and replication every third tick in its own system group (changed health, fires lit and gone out with
  `[Changed<Burning>]` / `[Removed<Burning>]`, changed bags). `BattleServerTests` check the same
  world and the host's queries, struct jobs, snapshots and error handling.
- **`MultiWorldBenchmark.TickAllWorlds`** runs one world per core, each on its own long-lived thread. This is the
  recommended server layout.
- **`ParallelSystemsBenchmark.Tick`** is one update of four `[ForEach]` systems with no order and disjoint access
  (movement, health and mana regeneration, cooldowns) over 100,000 entities, with `WorldOptions.ParallelSystems` off
  and on. With it on, the four systems form one stage and run at the same time; it shows what a parallel stage costs
  and gains against a sequential update.
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
- **`ChangeTrackingComparison`**, **`RemovalComparison`** and **`IndexComparison`** (project `EasyEcs.Comparison`,
  `FeatureBenchmark.cs`) run the same jobs (10 or 1,000 of 100,000 entities written per tick; 10 or 1,000 buffs
  removed per tick; 1 or 100 trades, then 100 inventory counts) in each library its own way: the built-in feature
  where it has one (DefaultEcs `WhenChanged<T>()` / `WhenRemoved<T>()` sets and `EntityMultiMap<T>`, Friflo's
  `OnComponentRemoved` event and link components), else the usual workaround (a scan comparing values, or a tag added
  on write or removal and removed after the visit). Setup checks every variant's result (replicas match the source;
  every removal is sent once; each owner's count matches the items pointing at it), and `--check` runs those checks
  without measuring.
- **`OperationBenchmark`** (project `EasyEcs.Comparison`) isolates single operations on the headline world, EasyEcs
  against Friflo, including spawning NPCs from a prefab (`Instantiate`; Friflo `CloneEntity`).

Measured with BenchmarkDotNet and the repository's job (`benchmarks/EasyEcs.Benchmark/BenchmarkConfig.cs`: 2 launches, 6
warmups, 8 iterations of 250 ms, call counting from the start) on .NET 10, on a 4-core Xeon VM at 2.1 GHz.

**Realistic server tick** (the "Allocated" column is per tick):

| Benchmark | Entities | v4.0.1 | v4.1 | Allocated |
|---|---:|---:|---:|---:|
| WorldSimulationBenchmark.Tick | 10,000 | 322 μs | 163 μs | 0 B |
| WorldSimulationBenchmark.Tick | 100,000 | 4.33 ms | 1.63 ms | 0 B |
| MultiWorldBenchmark.TickAllWorlds (4 worlds × 25k) | 100,000 | 1.85 ms | 0.93 ms | 0 B |
| BattleServerBenchmark.Tick (every feature) | 100,000 | – | 2.21 ms | 0 B |
| ParallelSystemsBenchmark.Tick, `ParallelSystems` off / on | 100,000 | – | 383 / 264 μs | 0 B |
| ParallelForEachBenchmark.Tick, `[ForEach]` / `[ForEach(Parallel = true)]` | 100,000 | – | 1,595 / 740 μs | 0 B |
| ChangeTrackingBenchmark.Tick, 10 rows written: every row / `[Changed<Health>]` | 100,000 | – | 39-110 μs / 0.58 μs | 0 B |
| ChangeTrackingBenchmark.QueryTick, 10 rows written: every row / `Changed<Health>()` | 100,000 | – | 450 μs / 0.75 μs | 0 B |
| ChangeTrackingBenchmark.Tick, 1,000 rows written: every row / `[Changed<Health>]` | 100,000 | – | 62-110 μs / 36.0 μs | 0 B |
| ChangeTrackingBenchmark.QueryTick, 1,000 rows written: every row / `Changed<Health>()` | 100,000 | – | 531 μs / 51.0 μs | 0 B |
| IndexBenchmark.TradeAndCount (100 trades, 100 inventory counts) | 100,000 | – | 5.3 μs | 0 B |
| SnapshotBenchmark.Capture (a new snapshot) / Restore | 100,000 | – | 777 / 448 μs | 2.8 MB / 0 B |
| DynamicBufferBenchmark.Tick (1,000 of 100,000 bags written, then sent): `DynamicBuffer<int>` / `List<int>` in a tracked component | 100,000 | – | 55.1 / 60.9 μs | 0 B |

**Against other ECS libraries** (`EcsComparisonBenchmark`, the headline world ported to each library; ratio against
EasyEcs):

| Library | 10,000 entities | 100,000 entities | Allocated per tick (100k) |
|---|---:|---:|---:|
| EasyEcs 4.1 | 153 μs (1.00) | 1.81 ms (1.00) | 0 B |
| Friflo.Engine.ECS | 166 μs (1.08) | 1.84 ms (1.02) | 0 B |
| Arch | 442 μs (2.88) | 5.12 ms (2.84) | 535 KB |
| DefaultEcs | 291 μs (1.90) | 5.76 ms (3.19) | 61 KB |

EasyEcs and Friflo are within a few percent of each other on this tick (EasyEcs 2-8% ahead in this run; Friflo was
up to 5% ahead in earlier runs on this VM). The per-operation breakdown (`OperationBenchmark`,
100,000 entities) shows where: EasyEcs is ahead on one-component iteration (39 vs 58 μs), SIMD batches (63 vs 85 μs),
filtered iteration (11.3 vs 15.1 μs), singletons (210 vs 225 μs), toggling a status component (548 vs 980 μs) and
create/destroy (181 vs 202 μs); Friflo is ahead on iteration that passes the entity (36 vs 48 μs); two-component
(246 vs 251 μs) and three-component iteration (66 vs 68 μs) are even.
Unlike Friflo, EasyEcs applies structural changes immediately, also inside a pass, with no command buffer. Change
tracking and index results against each library are in [the comparison](comparison.md).

**Micro benchmarks** (N = 10,000; the v3.1.1 column is BenchmarkDotNet ShortRun on the same machine). "–" means
the operation has no v3.1.1 equivalent (struct jobs, `[ForEach]` systems and handles are new in v4):

| Benchmark | v3.1.1 | v4.0.1 | v4.1 | Allocated (v4.1) |
|---|---:|---:|---:|---:|
| CreateAddDestroy (fresh world) | 5,358 μs | 1,795 μs | 890 μs | 1.52 MB (new world, its growth and the benchmark's handle array) |
| Churn: 10 rounds of create (2 components) and destroy in one warm world | 5,247 μs / 3.19 MB | 1,023 μs | 283 μs | 0 B |
| ChurnBatch: the same rounds with `CreateEntities` / `DestroyEntities` | – | – | 158 μs | 0 B |
| ToggleComponent: remove + add | 1,185 μs | 338 μs | 149 μs | 0 B |
| QueryIterate (`Query<P,V>` + reduction) | 40.6 μs | 35.3 μs | 29.0 μs | 0 B |
| ForEachIterate (`ForEach` struct job + reduction) | – | 30.6 μs | 12.6 μs | 0 B |
| QueryWrite (`Query`, pure write) | – | 28.3 μs | 15.5 μs | 0 B |
| ForEachSystemWrite (`[ForEach]` system adding `Velocity` to `Position`) | – | 15.1 μs | 8.1 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 24.3 μs | 9.3 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 11.0 μs | 10.9 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 9.1 μs | 7.3 μs | 0 B |
| HandleAccess (`Entity.Get`, ×1000) | – | 3.56 μs | 3.22 μs | 0 B |
| TryGetEntity (`world.TryGetEntity`, then a read, ×1000) | – | – | 3.49 μs | 0 B |
| TrackedHandleWrite (`Entity.Get` of a tracked component, ×1000) | – | – | 9.10 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 0.90 μs | 0.89 μs | 0 B |
| CountAndAny (`Count` + `Any` with a filter, ×1000) | – | – | 48.7 μs | 0 B |

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
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*'               # everything (the numbers above)
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*World*'         # headline: realistic server tick
dotnet run -c Release --project benchmarks/EasyEcs.Comparison -- --filter '*'              # other ECS libraries: headline world, operations, features
dotnet run -c Release --project benchmarks/EasyEcs.Comparison -- --check                   # correctness only: counters and feature results match
```

`WorldSimulationBenchmark` / `MultiWorldBenchmark` model a busy MMO-style world and must stay at 0 B allocated per
tick. `MicroBenchmarks.RawArrayWrite` is the no-ECS floor. `AllocationTests` (unit tests) guard the zero-allocation
contract on every test run.
