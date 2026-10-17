# Performance guide

[Docs index](README.md) · Related: [API reference](api.md) · [Architecture](architecture.md)

- [Choosing an iteration API](#choosing-an-iteration-api)
- [Regular code and the fastest code](#regular-code-and-the-fastest-code)
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

## Regular code and the fastest code

The regular way is right for most systems: it is short, and a `[ForEach]` method is already a generated, inlined
struct job. When a system shows up in `MeasureSystems` timings, these rewrites are what the benchmarks below measure.

| Regular | Fastest | Measured |
|---|---|---|
| A per-entity `[ForEach]` doing arithmetic with branches | A batch method vectorized with `Vector<T>`, branches as masks | 251 → 64 μs (100,000 entities, `OperationBenchmark`) |
| `foreach (var item in world.Query<…>())` in a hot ad-hoc loop | A struct job with `world.ForEach` | 28.9 → 12.6 μs (10,000 entities, micro benchmarks) |
| A `bool` flag tested in every row | A tag component with `[With<T>]` / `[Without<T>]` | flagged rows skipped 64 at a time; toggling flips a bit |
| Scanning every row for the few that changed | `ITrackChanges` + `[Changed<T>]` | 40-110 → 0.55 μs (10 of 100,000 rows written) |
| `CreateEntity` / `Destroy` in a loop | `CreateEntities` / `DestroyEntities` | 382 → 158 μs (10 rounds of 10,000) |
| Heavy per-entity work on one thread | `[ForEach(Parallel = true)]` | 1,601 → 765 μs (100,000 entities, 4 cores) |

### Arithmetic over many rows: vectorize a batch method

The JIT does not vectorize loops by itself, and data-dependent branches mispredict on every other row. The regular
system:

```csharp
public partial class BounceSystem : EcsSystem
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

public partial class BounceSimdSystem : EcsSystem
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
64-byte boundary. A batch method takes no `Entity` and makes no structural change; a scalar loop over spans gains
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

public partial class StunnedFlagSystem : EcsSystem
{
    [ForEach]
    static void Act(ref Unit unit)
    {
        if (unit.Stunned) return;
        unit.Hp++;
    }
}

public partial class StunnedTagSystem : EcsSystem   // fastest: the filter does the test
{
    [ForEach, Without<Stunned>]
    static void Act(ref Health hp) => hp = hp with { Value = hp.Value + 1 };
}
```

### The few rows that changed: `[Changed<T>]`

A replication or persistence system that scans every row pays for the rows nobody wrote. Mark the component
`ITrackChanges` and filter with `[Changed<T>]`: unwritten tables, 4,096-row blocks and 64-row words are skipped.
Take the component by `in` so the visit does not count as a write.

```csharp
public record struct Shield(int Value) : IComponent, ITrackChanges;
public record struct ShieldReplica(int Value) : IComponent;

public partial class ShieldReplicationSystem : EcsSystem
{
    [ForEach, Changed<Shield>]
    static void Send(in Shield shield, ref ShieldReplica replica) => replica = new ShieldReplica(shield.Value);
}
```

### The entity only where the row needs it

An `Entity` parameter reads the row's id for every row, even when only a rare branch uses it. A system that acts on
a few entities ("the ones that died") should find them with a filter, not a test in every row. Let the system that
lowers health add a `Dead` tag, or track `Health` and visit `[Changed<T>]` rows, so the `Entity` loop runs only for
the rows that matter.

### Waves of entities: batch calls

```csharp
var wave = new Entity[1_000];
world.CreateEntities(wave, new Position(0, 0), new Velocity(1, 0));   // one pass through the structural gate
int removed = world.DestroyEntities<Dead>();                          // every dead entity, one pass
```

Several components at once also take one transition: `entity.Set(a, b)`, `entity.Remove<A, B>()`.

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
  (movement, health and mana regeneration, cooldowns) over 100,000 entities, with `WorldOptions.ParallelSystems` off
  and on. With it on, the four systems form one batch and run at the same time; it shows what a parallel batch costs
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
| WorldSimulationBenchmark.Tick | 10,000 | 322 μs | 169 μs | 0 B |
| WorldSimulationBenchmark.Tick | 100,000 | 4.33 ms | 1.89 ms | 0 B |
| MultiWorldBenchmark.TickAllWorlds (4 worlds × 25k) | 100,000 | 1.85 ms | 1.08 ms | 0 B |
| BattleServerBenchmark.Tick (every feature) | 100,000 | – | 2.36 ms | 0 B |
| ParallelSystemsBenchmark.Tick, `ParallelSystems` off / on | 100,000 | – | 394 / 243 μs | 0 B |
| ParallelForEachBenchmark.Tick, `[ForEach]` / `[ForEach(Parallel = true)]` | 100,000 | – | 1,601 / 765 μs | 0 B |
| ChangeTrackingBenchmark.Tick, 10 rows written: every row / `[Changed<Health>]` | 100,000 | – | 40-110 μs / 0.55 μs | 0 B |
| ChangeTrackingBenchmark.QueryTick, 10 rows written: every row / `Changed<Health>()` | 100,000 | – | 470 μs / 0.79 μs | 0 B |
| ChangeTrackingBenchmark.Tick, 1,000 rows written: every row / `[Changed<Health>]` | 100,000 | – | 60-110 μs / 36.1 μs | 0 B |
| ChangeTrackingBenchmark.QueryTick, 1,000 rows written: every row / `Changed<Health>()` | 100,000 | – | 503 μs / 50.9 μs | 0 B |
| IndexBenchmark.TradeAndCount (100 trades, 100 inventory counts) | 100,000 | – | 11.5 μs | 0 B |
| SnapshotBenchmark.Capture (a new snapshot) / Restore | 100,000 | – | 724 / 426 μs | 2.8 MB / 0 B |
| DynamicBufferBenchmark.Tick (1,000 of 100,000 bags written, then sent): `DynamicBuffer<int>` / `List<int>` in a tracked component | 100,000 | – | 55.1 / 61.5 μs | 0 B |

**Against other ECS libraries** (`EcsComparisonBenchmark`, the headline world ported to each library; ratio against
EasyEcs):

| Library | 10,000 entities | 100,000 entities | Allocated per tick (100k) |
|---|---:|---:|---:|
| EasyEcs 4.1 | 167 μs (1.00) | 1.84 ms (1.00) | 0 B |
| Friflo.Engine.ECS | 155 μs (0.93) | 1.75 ms (0.95) | 0 B |
| Arch | 456 μs (2.74) | 5.46 ms (2.96) | 550 KB |
| DefaultEcs | 289 μs (1.73) | 5.96 ms (3.24) | 62 KB |

Friflo is 5-7% faster (5-8% across runs on this VM; the table is one run). The per-operation breakdown
(`OperationBenchmark`, 100,000 entities) shows where: EasyEcs is ahead on one-component iteration (39 vs 50 μs),
SIMD batches (64 vs 81 μs), filtered iteration (10.8 vs 14.6 μs), singletons (200 vs 221 μs) and toggling a status
component (481 vs 977 μs); Friflo is ahead on iteration that passes the entity (35 vs 46 μs) and create/destroy (198
vs 219 μs); two-component (251 vs 246 μs) and three-component iteration (61 vs 64 μs) are even.
Unlike Friflo, EasyEcs applies structural changes immediately, also inside a pass, with no command buffer. Change
tracking and index results against each library are in [the comparison](comparison.md).

**Micro benchmarks** (N = 10,000; the v3.1.1 column is BenchmarkDotNet ShortRun on the same machine). "–" means
the operation has no v3.1.1 equivalent (struct jobs, `[ForEach]` systems and handles are new in v4):

| Benchmark | v3.1.1 | v4.0.1 | v4.1 | Allocated (v4.1) |
|---|---:|---:|---:|---:|
| CreateAddDestroy (fresh world) | 5,358 μs | 1,795 μs | 1,092 μs | 1.52 MB (new world, its growth and the benchmark's handle array) |
| Churn: 10 rounds of create (2 components) and destroy in one warm world | 5,247 μs / 3.19 MB | 1,023 μs | 382 μs | 0 B |
| ChurnBatch: the same rounds with `CreateEntities` / `DestroyEntities` | – | – | 158 μs | 0 B |
| ToggleComponent: remove + add | 1,185 μs | 338 μs | 183 μs | 0 B |
| QueryIterate (`Query<P,V>` + reduction) | 40.6 μs | 35.3 μs | 28.9 μs | 0 B |
| ForEachIterate (`ForEach` struct job + reduction) | – | 30.6 μs | 12.6 μs | 0 B |
| QueryWrite (`Query`, pure write) | – | 28.3 μs | 15.7 μs | 0 B |
| ForEachSystemWrite (`[ForEach]` system adding `Velocity` to `Position`) | – | 15.1 μs | 8.2 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 24.3 μs | 9.5 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 11.0 μs | 10.7 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 9.1 μs | 6.7 μs | 0 B |
| HandleAccess (`Entity.Get`, ×1000) | – | 3.56 μs | 3.41 μs | 0 B |
| TryGetEntity (`Entity.TryGet`, ×1000) | – | – | 3.43 μs | 0 B |
| TrackedHandleWrite (`Entity.Get` of a tracked component, ×1000) | – | – | 8.65 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 0.90 μs | 0.80 μs | 0 B |
| CountAndAny (`Count` + `Any` with a filter, ×1000) | – | – | 50.5 μs | 0 B |

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
