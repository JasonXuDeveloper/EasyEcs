# EasyEcs

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/EasyEcs/.github/workflows/dotnet.yml?branch=master)
![license](https://img.shields.io/github/license/JasonXuDeveloper/EasyEcs)
![nuget](https://img.shields.io/nuget/v/EasyEcs)

A fast, allocation-free Entity-Component-System for .NET 10 servers and other long-running processes.

- **Write a method, get a system.** Annotate `[Query] void Move(ref Position p, in Velocity v)` and the bundled source generator produces the zero-allocation iteration job and `OnExecute`.
- **Zero GC in steady state.** A full tick of a 100k-entity world allocates 0 bytes. That tick covers movement, AOI, combat, buff add/remove, and projectile spawn/despawn.
- **Bounded memory.** Entity ids are recycled, so storage tracks the peak live entity count, not the total number of entities ever created.
- **Cheap structural changes.** Add/remove follows cached archetype transitions. Membership is a 16-byte record per entity.
- **Queries with filters.** `With<T>` / `Without<T>`, served from a lock-free copy-on-write archetype cache.
- **Async systems** with priority, execution frequency and optional parallelism.

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
public struct Frozen   : IComponent { }                  // tag component
public struct Clock    : ISingletonComponent { public long Tick; }

// A system is a partial class with [Query] methods. The generator emits OnExecute.
public partial class MoveSystem : SystemBase
{
    [Query, Without<Frozen>]
    static void Move(ref Position p, in Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

var ctx = new Context(new Context.Options(parallel: false, initialEntityCapacity: 100_000));
ctx.AddSystem<MoveSystem>();
ctx.AddSingletonComponent<Clock>();

var e = ctx.CreateEntity();
var (pos, vel) = e.Value.AddComponents<Position, Velocity>();   // one archetype transition
vel.Value.X = 1;                                                // always write through .Value (a ref)

await ctx.Init();
while (running) await ctx.Update();
await ctx.DisposeAsync();
```

### `[Query]` methods

A `[Query]` method lives in a `partial` class (usually a `SystemBase`). It may be static or an instance method. Its parameters can be:

| Parameter | Meaning |
|---|---|
| `ref T` / `in T` where `T : IComponent` | Component data. Use 1 to 9 of them. `ref` to write, `in` to read. |
| `int entityId` | Id of the current entity. |
| `EntityRef entity` | Version-checked reference to the current entity. |
| `Context context` | The context, for structural changes or singletons. |

Add `[With<T>]` to require a component without reading it, and `[Without<T>]` to exclude entities that have it. Both can be repeated.

For every query the generator emits `Run{Method}(Context)`. If the class doesn't define `OnExecute`, it also implements `IExecuteSystem.OnExecute`, which runs the queries in declaration order. To add your own logic around them, write `OnExecute` yourself and call `Run{Method}`:

```csharp
public partial class DamageSystem : SystemBase, IExecuteSystem
{
    public UniTask OnExecute(Context context)
    {
        // ... pre-processing ...
        RunBurn(context);
        return UniTask.CompletedTask;
    }

    [Query]
    void Burn(int entityId, ref Health hp, ref Burning burning, Context context)
    {
        hp.Value -= burning.DamagePerTick;
        if (--burning.Ticks == 0)
            context.GetEntityById(entityId).Value.RemoveComponent<Burning>();   // allowed during iteration
    }
}
```

The generator reports these diagnostics: `EECS001` (the class or a containing type isn't `partial`), `EECS002` (invalid parameter), `EECS003` (component count not between 1 and 9), `EECS004` (generic system).

## API and complexity

Notation:

| Symbol | Meaning |
|---|---|
| *n* | Number of components in the call (1–9) |
| *K* | Component types registered in the context |
| *M* | Archetypes matching a query |
| *S* | Slots (alive entities + tombstones) in those archetypes |

"Amortized" means storage occasionally doubles.

### Entities

| API | Complexity | Notes |
|---|---|---|
| `ctx.CreateEntity()` → `EntityRef` | O(1) amortized | Reuses a recycled id when one is available. |
| `ctx.DestroyEntity(e)` | O(K) | Clears the entity's component slots. The id is recycled and old refs become invalid. |
| `ctx.TryGetEntityById(id, out r)` / `GetEntityById(id)` | O(1) | |
| `entityRef.Value` → `ref Entity` | O(1) | Version check. Throws if the entity was destroyed. |
| `ctx.EntityCount` | O(1) | |
| `ctx.AllEntities` | O(capacity) | Diagnostics only. |
| `ctx.EntityAt(index)` | O(capacity) | Diagnostics only. |

### Components

| API | Complexity | Notes |
|---|---|---|
| `entity.AddComponent<T>()` → `ComponentRef<T>` | O(1) amortized | Follows a cached transition edge. Creating the first archetype of a new combination costs O(cached queries). |
| `entity.AddComponents<T1..Tn>()` | O(n) | Single archetype transition. Returns a tuple of refs. |
| `entity.RemoveComponent<T>()` / `RemoveComponents<T1..Tn>()` | O(1) / O(n) | Components the entity doesn't have are ignored. |
| `entity.GetComponent<T>()` / `TryGetComponent<T>(out r)` / `HasComponent<T>()` | O(1) | Bit test. |
| `componentRef.Value` → `ref T` | O(1) | Version check plus one indexed load. Inlined. |

### Queries

| API | Complexity | Notes |
|---|---|---|
| `[Query]` method / `ctx.ForEach<T1..Tn, TJob>(ref job, filter)` | O(M + S) | The fastest path. Struct job, no delegate, inlined. |
| `foreach (var r in ctx.GroupOf<T1..Tn>(filter))` | O(M + S) | Gives `r.Entity` and `r.Component1..n` as refs. |
| `ctx.CountOf<T1..Tn>(filter)` | O(M) | Sums archetype counts. Doesn't iterate entities. |
| `ctx.AnyOf<T1..Tn>(filter)` | O(M) worst case | Exits early. |
| `QueryFilter.Create().With<T>().Without<U>()` | O(1) | Build once and keep it in a `static readonly` field. |

The first call of a given query shape costs O(all archetypes) to build its cache entry. After that the lookup is one hash probe.

### Singletons (scoped per context)

| API | Complexity |
|---|---|
| `ctx.AddSingletonComponent<T>()` / `RemoveSingletonComponent<T>()` | O(1) |
| `ctx.GetSingletonComponent<T>().Value` → `ref T` / `TryGet…` / `Has…` | O(1) |

### Maintenance

| API | Complexity | Notes |
|---|---|---|
| `ctx.GetFragmentationStats()` | O(archetypes) | Lock-free. |
| `ctx.CompactArchetypes()` | O(total slots) | Call between frames when fragmentation is high. Never call it while iterating. |

## `ref` everywhere, and `ref` across `await`

Components are structs stored in arrays. Always mutate them through a `ref`. A local copy is just a copy:

```csharp
var hp = entity.GetComponent<Health>().Value;        // ❌ copy: the change below is lost
hp.Value -= 10;

ref var hp = ref entity.GetComponent<Health>().Value; // ✅ ref into storage
hp.Value -= 10;
```

**A `ref` into ECS storage is only valid until the next structural change.** Storage arrays grow when entities or components are added. When they grow, the arrays are reallocated and an old `ref` silently points at the stale copy. Creating an entity, adding a component or creating a new archetype can each trigger this, whether your own code does it or another system does.

That is why `ref` and `await` don't mix:

- C# doesn't allow `ref` locals to live across an `await` in async methods. Keep it that way: don't smuggle refs through `Unsafe`, pointers or `Span` fields.
- During an `await`, other systems run and may create entities or add components, which reallocates storage. The entity itself may be destroyed and its id recycled for a different entity.
- After an `await`, **re-acquire** the data from a version-checked handle. `EntityRef` or `ComponentRef<T>` throws if the entity was destroyed:

```csharp
public async UniTask OnExecute(Context ctx)
{
    ComponentRef<Health> hpRef = _player.Value.GetComponent<Health>(); // handle (id + version), safe to keep
    int before = hpRef.Value.Value;                                    // read what you need before awaiting

    await SaveToDatabaseAsync(before);                                 // other systems may run here

    if (_player.Value.TryGetComponent<Health>(out var hp))             // re-validate after the await
        hp.Value.Value = before;                                       // fresh ref, current storage
}
```

Rules of thumb:

1. Inside `[Query]` / `ForEach` / `GroupOf` bodies, use `ref` freely. They are synchronous, and the iterators refresh their storage after structural changes.
2. Never keep a `ref T` or a `ref Entity` in a field, and never keep one across an `await`. Keep `EntityRef` / `ComponentRef<T>` instead.
3. Read values into locals before `await` and write back through a re-acquired ref after it.

## Threading

- **Recommended layout: one `Context` per shard or world, driven by one thread.** Contexts are fully isolated: storage, singletons and the query cache are per context.
- Structural operations take a per-context `System.Threading.Lock`, so they're safe from any thread.
- Queries are lock-free. They read immutable copy-on-write archetype snapshots, so creating new archetypes concurrently is safe.
- Component **values** aren't synchronized. Writing the same component from two threads is a data race. That is why one owner per context is the model.
- `Options(parallel: true)` runs the systems of one priority bucket concurrently when the bucket holds more than 4 systems. Those systems must touch disjoint data.

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
| WorldSimulation.Tick | 10,000 | 375 μs | 0 B |
| WorldSimulation.Tick | 100,000 | 5.24 ms | 0 B |
| ShardedWorld.TickAllShards (4 worlds × 25k) | 100,000 | 2.16 ms | 0 B |

**Micro benchmarks** (N = 10,000). The v3.1.1 column comes from BenchmarkDotNet ShortRun on the same machine.

| Benchmark | v3.1.1 | v4.0.0 | Allocated (v4) |
|---|---:|---:|---:|
| CreateAddDestroy (fresh context) | 5,358 μs | 3,289 μs | 4.1 MB (array growth of the new context) |
| Churn: 10 rounds of create, add and destroy in one context | 5,247 μs / 3.19 MB | 1,692 μs | 155 KB (steady state reached in round 1) |
| ToggleComponent: remove + add | 1,185 μs | 441 μs | 0 B |
| QueryIterate (`GroupOf<P,V>` + reduction) | 40.6 μs | 34.7 μs | 0 B |
| QueryWrite (`GroupOf`, pure write) | – | 29.9 μs | 0 B |
| QuerySystemWrite (`[Query]` system) | – | 31.0 μs | 0 B |
| ForEachWrite (`ForEach` struct job) | – | 31.6 μs | 0 B |
| RawArrayWrite (two plain arrays, the floor) | – | 11.5 μs | 0 B |
| QueryIterate3 (half the entities match) | 16.2 μs | 9.7 μs | 0 B |
| SingletonAccess (×1000) | 1.42 μs | 1.05 μs | 0 B |

`GroupOf`, `ForEach` and `[Query]` iterate at the same speed. All three are about 2.6× the plain-array floor; the gap is the price of sparse ids, tombstones and per-entity indirection, which in turn make structural changes O(1).

## Upgrading from v3

v4 targets .NET 10 only. Ids are recycled, singletons are per context, and there are new `[Query]`, filter and `ForEach` APIs. See [MIGRATION_GUIDE.md](MIGRATION_GUIDE.md).

## Documentation

- [doc/architecture.md](doc/architecture.md): storage layout, archetypes, id recycling, iteration safety, threading contract.
- [doc/performance.md](doc/performance.md): which API to use when, do's and don'ts, how to measure.

## License

MIT
