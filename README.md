# EasyEcs

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/EasyEcs/.github/workflows/dotnet.yml?branch=master)
![license](https://img.shields.io/github/license/JasonXuDeveloper/EasyEcs)
![nuget](https://img.shields.io/nuget/v/EasyEcs)

A small, fast, allocation-free Entity-Component-System for .NET servers (and anything long-running).

- **Struct components, immediate API**: `CreateEntity()` / `AddComponent<T>()` return refs right away. There's no command buffer and no callbacks.
- **Zero GC on the hot path**: queries, iteration, component access and add/remove don't allocate once warmed up.
- **Built for long-running processes**: entity ids are recycled, so memory stays bounded by the peak live entity count no matter how many entities come and go.
- **Archetype queries** with SIMD tag matching, cached query results and cached archetype transitions.
- **Struct-job iteration** (`ForEach<..., TJob>`). The JIT specializes and inlines it, with no delegates and no closures.
- **Async systems** with priority, execution frequency and optional parallelism inside a priority bucket.
- Targets `net8.0` and `net10.0`.

```
dotnet add package EasyEcs
```

## Concepts

| Concept | What it is |
|---|---|
| `Context` | A world. It owns entities, component storage, systems and singleton components. Contexts are fully isolated from each other. |
| `Entity` / `EntityRef` | An id plus a version. Hold `EntityRef` rather than `Entity`; it is validated on access and throws if the entity was destroyed. |
| `IComponent` | A plain `struct` with data only. |
| `ISingletonComponent` | A `struct` that exists once per context, such as clocks or configuration. |
| `SystemBase` + `IInitSystem` / `IExecuteSystem` / `IEndSystem` | Logic. Systems run in ascending `Priority` order. Systems in the same priority bucket may run in parallel. |

Logic lives only in systems. Components hold data and nothing else. Systems don't reference each other; they only see the entities they query. That keeps dependencies one-directional.

## Quick start

```csharp
using Cysharp.Threading.Tasks;
using EasyEcs.Core;
using EasyEcs.Core.Components;
using EasyEcs.Core.Systems;

public struct Position : IComponent { public float X, Y; }
public struct Velocity : IComponent { public float X, Y; }
public struct Clock : ISingletonComponent { public long Tick; }

public sealed class MoveSystem : SystemBase, IExecuteSystem
{
    public override int Priority => 0;     // lower runs first
    public int ExecuteFrequency => 1;      // every Update(); 2 = every other Update(), ...

    private struct Move : IForEach<Position, Velocity>
    {
        public void Execute(int entityId, ref Position p, ref Velocity v)
        {
            p.X += v.X;
            p.Y += v.Y;
        }
    }

    public UniTask OnExecute(Context ctx)
    {
        var job = new Move();
        ctx.ForEach<Position, Velocity, Move>(ref job);    // fastest path, zero allocation
        ctx.GetSingletonComponent<Clock>().Value.Tick++;
        return UniTask.CompletedTask;
    }
}

var ctx = new Context(new Context.Options(parallel: false, initialEntityCapacity: 4096));
ctx.AddSystem<MoveSystem>();
ctx.AddSingletonComponent<Clock>();

var e = ctx.CreateEntity();
var (pos, vel) = e.Value.AddComponents<Position, Velocity>();   // one archetype transition
vel.Value.X = 1;

await ctx.Init();
while (running)
    await ctx.Update();
await ctx.DisposeAsync();
```

## API overview

**Entities**

```csharp
EntityRef e = ctx.CreateEntity();
ref Entity entity = ref e.Value;          // throws if destroyed
ctx.DestroyEntity(e);                     // id is recycled, old refs become invalid
ctx.TryGetEntityById(id, out var r);
foreach (var r in ctx.AllEntities) { }
```

**Components**

```csharp
ComponentRef<Hp> hp = entity.AddComponent<Hp>();
var (a, b) = entity.AddComponents<A, B>();      // up to 9, single archetype transition
entity.RemoveComponent<Hp>();
entity.RemoveComponents<A, B>();                // up to 9
entity.HasComponent<Hp>();
entity.TryGetComponent<Hp>(out var r);
ref Hp v = ref entity.GetComponent<Hp>().Value; // always mutate through ref
```

**Queries**

```csharp
// Struct job: fastest, zero allocation. 1..6 components.
ctx.ForEach<A, B, MyJob>(ref job);

// Enumerator: convenient, zero allocation. 1..9 components.
foreach (var r in ctx.GroupOf<A, B>())
{
    ref var a = ref r.Component1;
    ref var entity = ref r.Entity;
}

ctx.AnyOf<A, B>();     // existence check without building an enumerator
ctx.CountOf<A, B>();   // count without iterating
```

You can make structural changes (create, destroy, add, remove) while iterating. Entities added during a pass may or may not be visited; destroyed ones are skipped.

**Singletons** (scoped per context)

```csharp
ctx.AddSingletonComponent<Clock>();
ref var clock = ref ctx.GetSingletonComponent<Clock>().Value;
ctx.TryGetSingletonComponent<Clock>(out var c);
ctx.HasSingletonComponent<Clock>();
ctx.RemoveSingletonComponent<Clock>();
```

**Maintenance**

```csharp
var (slots, alive, fragmentation) = ctx.GetFragmentationStats();
if (fragmentation > 0.5f) ctx.CompactArchetypes();   // between frames, never while iterating
```

## Threading model

- Structural changes (create, destroy, add and remove component, add and remove system) take a per-context lock, so they're safe from any thread.
- Reading and writing component **values** is not synchronized. Give each context one owner thread, or partition the work so that systems in the same priority bucket touch disjoint data. For servers, the recommended layout is **one context per shard or world, each driven by its own thread**.
- With `Options(parallel: true)`, systems in the same priority bucket run concurrently when a bucket has more than 4 systems.

## Performance

Measured with `EasyEcs.Benchmark`: N = 10,000, .NET 10, BenchmarkDotNet ShortRun, on a 4-core Xeon VM.

| Benchmark | v3.1.1 | v4.0.0 | Change |
|---|---:|---:|---:|
| CreateAddDestroy | 5,358 μs | 3,501 μs | -35% |
| Churn: 10 rounds of create, add and destroy in one context | 5,247 μs / 3,190 KB | 1,794 μs / 156 KB | -66% time, -95% alloc |
| ToggleComponent: remove + add | 1,185 μs | 428 μs | -64% |
| QueryIterate (`GroupOf<P,V>`) | 40.6 μs | 43.5 μs | within noise |
| ForEachIterate (new; same work as QueryIterate) | – | 32.2 μs | -21% vs `GroupOf` |
| QueryIterate3 | 16.2 μs | 12.1 μs | -25% |
| SingletonAccess (×1000) | 1.42 μs | 0.89 μs | -37% |

None of the query or access benchmarks allocate. To run the benchmarks:

```
dotnet run -c Release --project EasyEcs.Benchmark -- --filter '*'
```

## What's new in v4.0.0

- **Entity id recycling**: destroyed ids are reused. Versions are bumped, so stale `EntityRef` or `Entity` copies are rejected rather than mutating the new occupant.
- **Destroy releases component data**, so managed references inside components can be collected.
- **O(1) archetype membership without hashing**: the context tracks entity → (archetype, slot). Single-component add and remove follow cached archetype transition edges instead of hashing a 256-bit tag. New archetypes start small.
- **Per-context singletons**. Before v4 they were a process-wide static shared by every context. Also added: `HasSingletonComponent<T>()` and `RemoveSingletonComponent<T>()`.
- **`ForEach<T1..T6, TJob>(ref TJob job)`**: struct-job iteration.
- `System.Threading.Lock` on .NET 9+; query accessors skip redundant bounds checks.
- New `EasyEcs.Benchmark` project.

Upgrading from v3? See [MIGRATION_GUIDE.md](MIGRATION_GUIDE.md). The breaking changes are behavioral: ids are recycled and singletons are per context.

## More examples

The `EasyEcs.UnitTest` project has commented examples of systems, priorities, frequencies, structural changes during iteration and the entity lifecycle.

## License

MIT
