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
- [12. States that come and go](#12-states-that-come-and-go)
- [13. Whole runs of rows: batch methods](#13-whole-runs-of-rows-batch-methods)
- [14. Heavy work on every core](#14-heavy-work-on-every-core)
- [15. Only what changed](#15-only-what-changed)
- [16. Find entities by value, follow relations](#16-find-entities-by-value-follow-relations)
- [17. Batches, snapshots, timings](#17-batches-snapshots-timings)
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
recommended layout is one world per thread (for example one per map or zone).

```csharp
using var world = new World(new WorldOptions { InitialEntityCapacity = 10_000 });
```

| `WorldOptions` | Default | Meaning |
|---|---|---|
| `InitialEntityCapacity` | 1024 | Pre-sized entity array (one 16-byte record per id), so it does not grow at run time. Archetype tables grow on their own, doubling. |
| `Parallel` | `false` | Run non-conflicting systems of equal priority at the same time (see [Threading](guide/threading.md#parallel-systems)). |
| `MaxDegreeOfParallelism` | processor count | Threads one parallel run uses at most: systems run by `Parallel`, and the chunks of `[ForEach(Parallel = true)]`, `ForEachParallel` and `ForEachEntityParallel` (those run in chunks whether `Parallel` is on or not). |
| `MeasureSystems` | `false` | Record each system's run count and time (`system.Stats`). |

`Dispose()` calls every system's `OnDestroy` (in reverse order) and releases storage.

## 3. Define components and tags

A component is a plain struct implementing `IComponent`: fields only, no logic. A tag is an empty component used
as a filter.

```csharp
public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent, ITrackChanges;   // writes are tracked (step 15)
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
// A field of your class: build each filter once.
private static readonly QueryFilter Alive = QueryFilter.Create().Without<Dead>();
```

```csharp
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
```

```csharp
world.AddSystem<MovementSystem>();
```

- `ref` parameters are written, `in` parameters are read; 1 to 9 components. This is also the system's declared
  access, which lets `WorldOptions.Parallel` run non-conflicting systems at the same time.
- `[With<T>]` requires a component that is not passed in; `[Without<T>]` excludes entities that have it. A method
  can take no component at all when a `[With<T>]` (or a `[Changed<T>]`) names what to query: `[ForEach, With<Dead>]
  static void Remove(Entity e)`.
- An `Entity` parameter gives the current entity's handle; a `World` parameter gives the world (handy in static
  methods).

Systems also have lifecycle hooks: `OnCreate()` when added, `OnUpdate()` once per update before the `[ForEach]`
methods, and `OnDestroy()` when removed or when the world is disposed. More in [Systems](guide/systems.md).

## 8. Keep world state in singletons

Systems are stateless ([EECS007](guide/analyzer.md#eecs007)). State with one value per world (a clock, scores,
configuration) is a singleton: a struct implementing `ISingletonComponent`.

```csharp
public record struct Tally(long Tick, int Deaths, int Afflicted) : ISingletonComponent;

public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity entity, in Health hp, ref Tally tally)   // resolved once per pass
    {
        if (hp.Value > 0) return;
        entity.Set(new Dead());
        tally.Deaths++;
    }
}

public sealed class ClockSystem : EcsSystem
{
    protected override void OnUpdate() => World.GetSingleton<Tally>().Tick++;
}
```

```csharp
ref var tally = ref world.GetSingleton<Tally>();     // ref T, like components (added once at setup, step 10)
```

A `[ForEach]` method takes a singleton as a `ref` / `in` parameter (resolved once per pass, added if missing), and
`OnUpdate` reaches it through the system's `World` property.

The `Set(new Dead())` applies at once: later systems of the same update already see the corpse, and the
`Without<Dead>` filter keeps the next updates from counting it again. Each entity is visited at most once per pass;
see [Structural changes during iteration](guide/structural-changes.md). More on singletons in
[Singletons](guide/singletons.md).

## 9. Order systems and react to state

Systems run in ascending `Priority` (registration order breaks ties). They never call each other
([EECS008](guide/analyzer.md#eecs008)); they cooperate through data. `DeathSystem` marks `Dead`; a later system
reacts to that state without `DeathSystem` knowing it exists:

```csharp
public partial class CorpseSystem : EcsSystem
{
    public override int Priority => 10;      // after the default priority 0

    [ForEach, With<Dead>]
    static void Remove(Entity entity) => entity.Destroy();
}
```

Structural changes apply immediately, so later systems in the same update see them. Why this design matters is in
[ECS principles](ecs-principles.md).

## 10. Run the update loop

```csharp
world.AddSingleton<Tally>();
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
public partial class ApplyInputSystem : EcsSystem { /* [ForEach] methods */ }
```

```csharp
world.Update<InputGroup>();   // whenever input arrives
world.Update();               // fixed tick
```

## 12. States that come and go

A temporary state (burning, frozen, stunned) is a component that is added and later removed. Removing one only
clears a presence bit, so the entity keeps its row and the next add is as cheap. `[WithAny<A>, WithAny<B>]` visits
entities that have at least one of them:

```csharp
public record struct Burning(int Ticks) : IComponent;
public record struct Frozen(int Ticks) : IComponent;

public partial class StatusSystem : EcsSystem
{
    [ForEach]
    static void Burn(Entity unit, ref Burning burning, ref Health hp)
    {
        hp = hp with { Value = hp.Value - 3 };
        if (--burning.Ticks == 0) unit.Remove<Burning>();    // safe in the middle of the pass
    }

    [ForEach, WithAny<Burning>, WithAny<Frozen>]              // burning or frozen, or both
    static void Count(in Health hp, ref Tally tally) => tally.Afflicted++;
}
```

State is data: instead of a `bool IsBurning` field every system checks, the systems that care query `Burning`.
See [Structural changes](guide/structural-changes.md).

## 13. Whole runs of rows: batch methods

A `[ForEach]` method that takes `Span<T>` / `ReadOnlySpan<T>` receives whole runs of matching rows, so the JIT can
vectorize the loop. The filters apply as usual; it cannot change structure:

```csharp
public partial class FastMovementSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        for (int i = 0; i < positions.Length; i++)
            positions[i] = new Position(positions[i].X + velocities[i].X, positions[i].Y + velocities[i].Y);
    }
}
```

## 14. Heavy work on every core

`[ForEach(Parallel = true)]` splits one method's rows into chunks that run on several threads. The method may not
change structure and takes singletons only `in`; the analyzer rejects anything that could race
([EECS010](guide/analyzer.md#eecs010)):

```csharp
public record struct Target(float X, float Y) : ISingletonComponent;

public partial class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true), Without<Dead>]
    static void Steer(in Position p, ref Velocity v, in Target target)
    {
        float dx = target.X - p.X, dy = target.Y - p.Y, length = MathF.Sqrt(dx * dx + dy * dy) + 1;
        v = new Velocity(dx / length, dy / length);
    }
}
```

It pays for heavy per-entity work over tens of thousands of entities. Independent *systems* of one priority can also
run together with `WorldOptions.Parallel` ([Threading](guide/threading.md)).

## 15. Only what changed

A component marked `ITrackChanges` records the tick of each row's last write; any write counts, including through
`ref`. A `[Changed<T>]` method visits only the rows written since its system last ran, which is what replication
and persistence need:

```csharp
public record struct Replica(int Health) : IComponent;
public struct Network : ISystemGroup;

[UpdateInGroup<Network>]
public partial class ReplicationSystem : EcsSystem
{
    [ForEach, Changed<Health>]
    static void Send(in Health hp, ref Replica replica) => replica = new Replica(hp.Value);
}
```

```csharp
world.Update();                // simulation
world.Update<Network>();       // when the server sends: only healths written since the last send
```

Take components `in` when you only read them: a `ref` parameter, `Get<T>()` or a query item counts as a write.
See [Change tracking](guide/change-tracking.md).

## 16. Find entities by value, follow relations

A tracked component implementing `IIndexed<TKey>` is found by its key through `world.Index<T, TKey>()`, which
follows every write. An `Entity` key makes a relation: the index lists the entities pointing at an entity.

```csharp
public record struct Team(int Id) : IComponent, IIndexed<int> { public int Key => Id; }
public record struct OwnedBy(Entity Owner) : IComponent, IIndexed<Entity> { public Entity Key => Owner; }
```

```csharp
var teams = world.Index<Team, int>();
int red = teams.Count(1);                                     // units in team 1
Entity archer = world.CreateEntity(new Team(1));
world.CreateEntity(new OwnedBy(archer), new Position(0, 0));   // an arrow
foreach (var shot in world.Index<OwnedBy, Entity>().Find(archer))
    shot.Destroy();                                           // destroying while iterating is allowed
```

See [Indexes and relations](guide/indexes-and-relations.md).

## 17. Batches, snapshots, timings

```csharp
var units = new Entity[10_000];
world.CreateEntities(units, new Position(), new Velocity(1, 0), new Health(100));   // one table growth

var save = world.Capture();     // copy the world in memory
world.Update();
world.Restore(save);            // and roll it back: handles from before stay valid
```

With `WorldOptions { MeasureSystems = true }`, every system records its runs and times in `system.Stats`. See
[Snapshots](guide/snapshots.md).

The [battle server example](example.md) puts all of it together in one battle server, which is also a
benchmark and a test of this repository.

## Next steps

- [ECS principles](ecs-principles.md): design features as data plus one-job systems.
- [Guide](README.md#guide): one page per concept, including [change tracking](guide/change-tracking.md) (systems that
  visit only what changed) and [indexes and relations](guide/indexes-and-relations.md) (find entities by value).
- [Performance guide](performance.md): which API to use when.
- [API reference](api.md): every public API and its complexity.
