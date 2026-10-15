# Getting started

This tutorial builds a small simulation step by step: units that move, take damage, die and are cleaned up. Each
step introduces one concept and links to the guide page that covers it in depth.

- [1. Install](#1-install)
- [2. Create a world](#2-create-a-world)
- [3. Define components and tags](#3-define-components-and-tags)
- [4. Create entities](#4-create-entities)
- [5. Read and write components](#5-read-and-write-components)
- [6. Query entities](#6-query-entities)
- [7. Write a system with `[ForEach]`](#7-write-a-system-with-foreach)
- [8. Keep world state in singletons](#8-keep-world-state-in-singletons)
- [9. Order systems and react to state](#9-order-systems-and-react-to-state)
- [10. Run the update loop](#10-run-the-update-loop)
- [11. Different cadences: system groups](#11-different-cadences-system-groups)
- [Next steps](#next-steps)

## 1. Install

```bash
dotnet add package EasyEcs
```

EasyEcs targets `net10.0`. The package ships the source generator and the analyzer (`analyzers/dotnet/cs`), so
`[ForEach]` methods and compile-time checks work without extra setup.

If you reference the sources instead of the package (a `ProjectReference` to `EasyEcs.Core`), also reference the
generator as an analyzer:

```xml
<ProjectReference Include="..\EasyEcs\EasyEcs.Core\EasyEcs.Core.csproj" />
<ProjectReference Include="..\EasyEcs\EasyEcs.Generator\EasyEcs.Generator.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Everything lives in one namespace: `using EasyEcs;`.

## 2. Create a world

A `World` owns entities, component storage, singletons and systems. Worlds are fully isolated; on a server, the
recommended layout is one world per shard (thread).

```csharp
using var world = new World(new WorldOptions { InitialEntityCapacity = 10_000 });
```

| `WorldOptions` | Default | Meaning |
|---|---|---|
| `InitialEntityCapacity` | 1024 | Pre-sized entity capacity, so storage does not grow at run time. |
| `Parallel` | `false` | Run systems of equal priority in parallel (see [Threading](guide/threading.md)). |
| `MaxDegreeOfParallelism` | processor count | Limit for `Parallel`. |

`Dispose()` calls every system's `OnDestroy` (in reverse order) and releases storage.

## 3. Define components and tags

A component is a plain struct implementing `IComponent`: fields only, no logic. A tag is an empty component used
as a filter.

```csharp
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public struct Dead : IComponent;      // tag
```

Components must be structs; a class implementing `IComponent` fails the build with
[EECS006](guide/analyzer.md#eecs006). Keep them small and split rarely-used data into its own component: iteration
only loads the columns it reads.

## 4. Create entities

```csharp
Entity empty = world.CreateEntity();
Entity unit  = world.CreateEntity(new Position(0, 0), new Velocity(1, 0), new Health(100));
```

`CreateEntity` with values (up to 9) places the entity directly in its final archetype. An `Entity` is a handle (id,
version, world). Ids are recycled after `Destroy()`, and the version makes sure an old handle can never touch the
entity that reuses its id: accessing a destroyed entity throws. Keep the `Entity`, never a raw id.

More in [Entities and components](guide/entities-and-components.md).

## 5. Read and write components

`Get<T>()` returns a `ref T` into storage. Mutate through the ref; changing a copy changes nothing.

```csharp
unit.Get<Health>().Value -= 10;              // write in place
ref var pos = ref unit.Get<Position>();      // a ref local
pos.X += 1;

if (unit.Has<Velocity>()) { /* ... */ }
if (unit.TryGet<Health>(out var hp)) { /* hp is a copy */ }

unit.Set(new Health(50), new Dead());        // add or overwrite several at once (one transition)
unit.Remove<Dead>();                         // missing components are ignored
ref var v = ref unit.GetOrAdd<Velocity>();   // add with default value if missing
unit.Destroy();
```

A `ref` is valid only until the next structural change and never across an `await`: see
[`ref` and `await`](guide/refs-and-await.md).

## 6. Query entities

For ad-hoc code (tools, tests, one-off passes), iterate with `Query`, or ask `Count` / `Any`:

```csharp
// A field: build each filter once.
private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();

// In a method:
foreach (var item in world.Query<Position, Velocity>(Alive))
{
    ref var p = ref item.Component1;
    p.X += item.Component2.X;
    // item.Entity is the entity's handle
}

int movers = world.Count<Position, Velocity>(Alive);
bool anyDead = world.Any<Dead>();
```

Queries allocate nothing. Build filters once and keep them in `static readonly` fields. See
[Queries and filters](guide/queries.md), including struct jobs (`world.ForEach`).

## 7. Write a system with `[ForEach]`

A system is a class deriving from `EcsSystem` with one responsibility. Mark a method `[ForEach]` and it runs once
per entity that has its component parameters. The class must be `partial` (the generator adds the job).

```csharp
public partial class MovementSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Move(ref Position p, in Velocity v) => p = p with { X = p.X + v.X, Y = p.Y + v.Y };
}

world.AddSystem<MovementSystem>();
```

- `ref` parameters are written, `in` parameters are read; 1 to 9 components.
- `[With<T>]` requires a component that is not passed in; `[Without<T>]` excludes entities that have it.
- An `Entity` parameter gives the current entity's handle; a `World` parameter gives the world (handy in static
  methods).

Systems also have lifecycle hooks: `OnCreate()` when added, `OnUpdate()` once per update before the `[ForEach]`
methods, and `OnDestroy()` when removed or when the world is disposed. More in [Systems](guide/systems.md).

## 8. Keep world state in singletons

Systems are stateless ([EECS007](guide/analyzer.md#eecs007)). State with one value per world (a clock, scores,
configuration) is a singleton: a struct implementing `ISingletonComponent`.

```csharp
public record struct Stats(long Tick, int Deaths) : ISingletonComponent;

public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity entity, in Health hp, ref Stats stats)   // resolved once per pass
    {
        if (hp.Value > 0) return;
        entity.Set(new Dead());
        stats.Deaths++;
    }
}

public sealed class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.GetSingleton<Stats>().Tick++;
}
```

```csharp
world.AddSingleton<Stats>();                         // add it once, at setup
ref var stats = ref world.GetSingleton<Stats>();     // ref T, like components
```

A `[ForEach]` method takes a singleton as a `ref` / `in` parameter (resolved once per pass, added if missing), and
`OnUpdate` reaches it through the system's `World` property.

Note the `Without<Dead>` filter on a job that adds `Dead`: an entity that moves into another matching archetype during
a pass may be visited again, so jobs filter out what they produce. See
[Structural changes during iteration](guide/structural-changes.md). More on singletons in
[Singletons](guide/singletons.md).

## 9. Order systems and react to state

Systems run in ascending `Priority` (registration order breaks ties). They never call each other
([EECS008](guide/analyzer.md#eecs008)); they cooperate through data. `DeathSystem` marks `Dead`; a later system
reacts to that state without `DeathSystem` knowing it exists:

```csharp
public partial class CorpseSystem : EcsSystem
{
    public override int Priority => 10;      // after the default priority 0

    [ForEach]
    static void Remove(Entity entity, in Dead _) => entity.Destroy();
}
```

Structural changes apply immediately, so later systems in the same update see them. Why this design matters is in
[ECS principles](ecs-principles.md).

## 10. Run the update loop

```csharp
world.AddSingleton<Stats>();
world.AddSystem<MovementSystem>();
world.AddSystem<DeathSystem>();
world.AddSystem<CorpseSystem>();
world.AddSystem<ClockSystem>();

while (running)
{
    world.Update();                           // every enabled, due system, synchronously
}
```

`Update()` is synchronous and allocation-free. Per system you can also set `Enabled = false` to skip it, or override
`UpdateInterval` to run every N-th update. Exceptions thrown by systems go to `world.SystemError` if you subscribe;
otherwise they propagate.

## 11. Different cadences: system groups

`world.Update()` runs the default group. Systems marked `[UpdateInGroup<TGroup>]` run only when
`world.Update<TGroup>()` is called, for example input handling on every wake-up and the simulation on a fixed tick:

```csharp
public struct InputGroup : ISystemGroup;

[UpdateInGroup<InputGroup>]
public partial class ApplyInputSystem : EcsSystem { /* ... */ }

world.Update<InputGroup>();   // whenever input arrives
world.Update();               // fixed tick
```

## Next steps

- [ECS principles](ecs-principles.md): design features as data plus one-job systems.
- [Guide](README.md#guide): one page per concept.
- [Performance guide](performance.md): which API to use when.
- [API reference](api.md): every public API and its complexity.
