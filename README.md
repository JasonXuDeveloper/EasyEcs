# EasyEcs

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/EasyEcs/.github/workflows/dotnet.yml?branch=master)
![license](https://img.shields.io/github/license/JasonXuDeveloper/EasyEcs)
![nuget](https://img.shields.io/nuget/v/EasyEcs)

A fast, allocation-free Entity-Component-System for .NET 10 servers and other long-running processes.

- **Write the per-entity logic, get the system.** `[Each] void Move(ref Position p, in Velocity v)` is compiled by
  the bundled source generator into a zero-allocation, JIT-inlined iteration.
- **Zero GC in steady state**: a full tick of a 100k-entity world (movement, AOI, combat, buff add/remove,
  projectile spawn/despawn) allocates 0 bytes; a unit test guards the budget.
- **Bounded memory**: entity ids are recycled; storage tracks the peak live entity count.
- **Cheap structural changes**: cached archetype transitions, 16-byte per-entity metadata.
- **Queries with filters** (`With<T>` / `Without<T>`) served from a lock-free copy-on-write cache.
- **Standard ECS vocabulary**: `World`, `Entity`, component, singleton, `SystemBase` with
  `OnCreate`/`OnUpdate`/`OnDestroy`, `Query`.

```
dotnet add package EasyEcs        # net10.0, source generator included
```

## Quick start

```csharp
using EasyEcs.Core;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;

public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Frozen   : IComponent { }                      // tag component
public struct Clock    : ISingletonComponent { public long Tick; }

public partial class MovementSystem : SystemBase
{
    protected override void OnUpdate() => World.GetSingleton<Clock>().Tick++;   // once per update

    [Each, Without<Frozen>]                                                     // then once per matching entity
    static void Move(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

using var world = new World(new WorldOptions { InitialEntityCapacity = 100_000 });
world.AddSingleton<Clock>();
world.AddSystem<MovementSystem>();

var e = world.CreateEntity();
var (pos, vel) = e.Value.AddComponents<Position, Velocity>();   // one archetype transition
vel.Value.X = 1;                                                // always write through .Value (a ref)

while (running) world.Update();
```

## Concepts

| Concept | API | Meaning |
|---|---|---|
| World | `World` | Owns entities, component storage, singletons and systems. Worlds are fully isolated. |
| Entity | `EntityRef` (handle) / `Entity` (value) | An id + version. Keep `EntityRef`: it is validated on access. |
| Component | `struct : IComponent` | Plain data. Tag components are empty structs. |
| Singleton | `struct : ISingletonComponent` | One value per world (clock, config, world state). |
| System | `SystemBase` | Logic. `OnCreate` when added, `OnUpdate` + `[Each]` methods every update, `OnDestroy` when removed. |
| Query | `world.Query<…>()`, `ForEach`, `Count`, `Any`, `QueryFilter` | Entities having a set of components (plus filters). |

### System update order

`world.Update()` runs systems in ascending `Priority` (registration order breaks ties). For each enabled system whose
`UpdateInterval` is due:

1. `OnUpdate()`
2. its `[Each]` methods, in declaration order.

That is the whole rule: no generated `OnUpdate`, nothing implicit beyond running the `[Each]` methods after it.

### `[Each]` methods

Declared in a `partial` class deriving from `SystemBase`; static or instance. Parameters:

| Parameter | Meaning |
|---|---|
| `ref T` / `in T` (`T : IComponent`) | component data, 1–9 of them (`ref` to write, `in` to read) |
| `int entityId` | id of the current entity |
| `EntityRef entity` | version-checked handle to the current entity |
| `World world` | the world (structural changes, singletons) |

Filters: `[With<T>]` (require without reading) and `[Without<T>]` (exclude), repeatable. Structural changes inside
an `[Each]` method are allowed. Generator diagnostics: `EECS001` not partial, `EECS002` invalid parameter, `EECS003`
component count not 1–9, `EECS004` generic, `EECS005` class does not derive from `SystemBase`.

## API and complexity

*n* = components in the call (1–9), *K* = component types used by the world, *M* = archetypes matching a query,
*S* = slots (alive + tombstones) in those archetypes. "Amortized" = storage occasionally doubles.

### World and entities

| API | Complexity | Notes |
|---|---|---|
| `new World(WorldOptions)` | O(capacity) | `InitialEntityCapacity`, `Parallel`, `MaxDegreeOfParallelism` |
| `world.CreateEntity()` → `EntityRef` | O(1) amortized | reuses recycled ids |
| `world.DestroyEntity(entityRef / entity)` | O(K) | clears component slots, recycles the id; stale handles are ignored |
| `world.TryGetEntityById(id, out e)` / `GetEntityById(id)` | O(1) | |
| `entityRef.Value` → `ref Entity` | O(1) | version check, throws if destroyed |
| `world.EntityCount` | O(1) | |
| `world.AllEntities` | O(capacity) | diagnostics |
| `world.Dispose()` | O(storage) | `OnDestroy` of every system (reverse order), then releases storage |

### Components

| API | Complexity | Notes |
|---|---|---|
| `entity.AddComponent<T>()` → `ComponentRef<T>` | O(1) amortized | cached transition edge; the first transition into a new archetype also updates cached queries |
| `entity.AddComponents<T1..Tn>()` → tuple | O(n) | single archetype transition |
| `entity.RemoveComponent<T>()` / `RemoveComponents<T1..Tn>()` | O(1) / O(n) | missing components are ignored |
| `entity.GetComponent<T>()` / `TryGetComponent<T>(out r)` / `HasComponent<T>()` | O(1) | |
| `componentRef.Value` → `ref T` | O(1) | version check + one indexed load, inlined |

### Queries

| API | Complexity | Notes |
|---|---|---|
| `[Each]` method / `world.ForEach<T1..Tn, TJob>(ref job, filter)` | O(M + S) | fastest: struct job, no delegate |
| `foreach (var item in world.Query<T1..Tn>(filter))` | O(M + S) | `item.Entity`, `item.EntityId`, `item.Component1..n` (refs) |
| `world.Count<T1..Tn>(filter)` | O(M) | sums archetype counts |
| `world.Any<T1..Tn>(filter)` | O(M) worst | early exit |
| `QueryFilter.Create().With<T>().Without<U>()` | O(1) | build once, keep it in a `static readonly` field |

A new query shape costs O(all archetypes) once to build its cache entry; afterwards the lookup is one hash probe.

### Systems and singletons

| API | Complexity |
|---|---|
| `world.AddSystem<T>()` / `AddSystem(system)` (calls `OnCreate`) | O(systems) |
| `world.RemoveSystem<T>()` (calls `OnDestroy`) / `GetSystem<T>()` | O(systems) |
| `world.Update()` | Σ systems; allocation-free |
| `world.AddSingleton<T>()` → `ref T` / `GetSingleton<T>()` → `ref T` / `TryGetSingleton` / `HasSingleton` / `RemoveSingleton` | O(1) |
| `world.GetFragmentationStats()` / `CompactArchetypes()` | O(archetypes) / O(total slots) |

## `ref` everywhere, and `ref` across `await`

Components are structs stored in arrays: always mutate them through a `ref`; a local copy is just a copy.

```csharp
var hp = entity.GetComponent<Health>().Value;        // ❌ copy: the change below is lost
hp.Value -= 10;

ref var hp = ref entity.GetComponent<Health>().Value; // ✅ ref into storage
hp.Value -= 10;
```

**A `ref` into ECS storage is valid only until the next structural change.** Storage arrays grow (and are reallocated)
when entities, components or archetypes are added, by your code or another system; an old `ref` then silently points
at the stale copy. Hence `ref` and `await` must not mix:

- C# forbids `ref` locals that live across an `await`. Don't smuggle refs through `Unsafe`, pointers or `Span`
  fields either.
- During an `await` other code runs and may add entities/components (reallocating storage), or destroy the entity
  and recycle its id.
- After an `await`, **re-acquire** through a version-checked handle (`EntityRef` / `ComponentRef<T>` throw if the
  entity was destroyed):

```csharp
async ValueTask SaveAsync(World world, EntityRef player)
{
    int hp = player.Value.GetComponent<Health>().Value.Value;   // read what you need before awaiting
    await database.SaveAsync(hp);                               // the world may change meanwhile
    if (player.Value.TryGetComponent<Health>(out var health))   // re-validate after the await (throws if destroyed)
        health.Value.Value = hp;                                // fresh ref into current storage
}
```

Rules: use `ref` freely inside `[Each]` / `ForEach` / `Query` bodies (synchronous, storage-refresh aware); never keep
a `ref` in a field or across an `await` (keep `EntityRef` / `ComponentRef<T>`); read before awaiting, write back
through a re-acquired ref after. Systems themselves are synchronous by design.

## Threading

- **Recommended: one `World` per shard/thread.** Worlds share nothing (storage, singletons, query cache, systems).
- Structural operations take a per-world `System.Threading.Lock`; queries are lock-free over immutable copy-on-write
  archetype snapshots.
- Component **values** are not synchronized: one owner thread per world, or `WorldOptions.Parallel` with systems of
  the same priority touching disjoint data.

## Performance

All numbers below are from BenchmarkDotNet; run `dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'` to reproduce them. The headline benchmarks simulate a real server tick, not a toy loop:

- **`WorldSimulationBenchmark.Tick`** runs one tick of a world with 30% players and 70% NPCs, and projectiles on top. The tick contains:
  - movement (`Without<Stunned>`, `Without<Dead>`)
  - an AOI grid
  - combat that randomly adds `Burning` / `Stunned`
  - status expiry that removes those components
  - death that adds `Dead`
  - NPC despawn and respawn
  - projectile spawn (~5% of players per tick) and expiry

  Setup asserts that the world is busy. With 100k entities, one tick does about 11k hits, 430 deaths, 1,500 projectile spawns and 5,600 AOI cell changes.
- **`ShardedWorldBenchmark.TickAllShards`** runs one world per core on a long-lived thread each, the recommended server layout. It measures scaling and cross-world contention.
- **`MicroBenchmarks`** isolate single operations for diagnosis. `RawArrayWrite` is the no-ECS floor.

Measured with BenchmarkDotNet's default job on .NET 10, on a 4-core Xeon VM at 2.1 GHz.

**Realistic server tick** (the "Allocated" column is per tick):

| Benchmark | Entities | Mean | Allocated |
|---|---:|---:|---:|
| WorldSimulation.Tick | 10,000 | 343 μs | 0 B |
| WorldSimulation.Tick | 100,000 | 4.82 ms | 0 B |
| ShardedWorld.TickAllShards (4 worlds × 25k) | 100,000 | 2.06 ms | 0 B |

**Micro benchmarks** (N = 10,000). The v3.1.1 column comes from BenchmarkDotNet ShortRun on the same machine.

| Benchmark | v3.1.1 | v4.0.0 | Allocated (v4) |
|---|---:|---:|---:|
| CreateAddDestroy (fresh world) | 5,358 μs | 3,724 μs | 4.1 MB (array growth of the new world) |
| Churn: 10 rounds of create, add and destroy in one world | 5,247 μs / 3.19 MB | 1,789 μs | 154 KB (steady state reached in round 1) |
| ToggleComponent: remove + add | 1,185 μs | 450 μs | 0 B |
| QueryIterate (`Query<P,V>` + reduction) | 40.6 μs | 36.7 μs | 0 B |
| QueryWrite (`Query`, pure write) | – | 31.1 μs | 0 B |
| EachSystemWrite (`[Each]` system) | – | 27.3 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 29.4 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 12.1 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 9.3 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 1.03 μs | 0 B |

`Query`, `ForEach` and `[Each]` iterate at the same speed. All three are about 2.6× the plain-array floor; the gap is the price of sparse ids, tombstones and per-entity indirection, which in turn make structural changes O(1).

## Upgrading from v3

v4 targets .NET 10, renames the API to standard ECS terms (`Context` → `World`, `GroupOf` → `Query`, ...), makes
systems synchronous (`OnCreate`/`OnUpdate`/`OnDestroy`) and recycles entity ids. See
[MIGRATION_GUIDE.md](MIGRATION_GUIDE.md).

## Documentation

- [doc/architecture.md](doc/architecture.md): storage layout, archetypes, id recycling, iteration safety, threading.
- [doc/performance.md](doc/performance.md): which API to use when, do's and don'ts, how to measure.

## License

MIT
