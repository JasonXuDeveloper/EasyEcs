# Analyzer rules (EECS001-EECS016)

[Docs index](../README.md) · Related: [Systems](systems.md) · [ECS principles](../ecs-principles.md)

The EasyEcs analyzer ships in the package next to the source generator. Every rule is an error: code that breaks the
[ECS principles](../ecs-principles.md) does not build. The generator skips invalid input and the analyzer reports it, so
"not generated" always means "reported".

| Id | Severity | Rule |
|---|---|---|
| [EECS001](#eecs001) | Error | A system with `[ForEach]` methods must be `partial` |
| [EECS002](#eecs002) | Error | Invalid `[ForEach]` parameter |
| [EECS003](#eecs003) | Error | A `[ForEach]` method takes 1 to 9 components, or none with a `[With<T>]` or `[Changed<T>]` |
| [EECS004](#eecs004) | Error | Generic systems and methods are not supported |
| [EECS005](#eecs005) | Error | `[ForEach]` outside an `EcsSystem` |
| [EECS006](#eecs006) | Error | Components and singletons must be structs |
| [EECS007](#eecs007) | Error | Systems must be stateless |
| [EECS008](#eecs008) | Error | Systems must not reference systems |
| [EECS009](#eecs009) | Error | A batch `[ForEach]` method takes only spans, singletons and an `EntitySpan` |
| [EECS010](#eecs010) | Error | A `[ForEach(Parallel = true)]` method must be able to run on several threads |
| [EECS011](#eecs011) | Error | A `[ForEach]` method must not change what a read-only parameter holds |
| [EECS012](#eecs012) | Error | A `DynamicBuffer<T>` is never copied |
| [EECS013](#eecs013) | Error | A `[ForEach]` method that takes no `Entity` or `World` changes no structure |
| [EECS014](#eecs014) | Error | Structural changes stay on the world's owner thread |
| [EECS015](#eecs015) | Error | An indexed component is written with `Set` |
| [EECS016](#eecs016) | Error | A `[Removed<T>]` method does not require `T` |

## EECS001

**System must be partial.** A system that declares `[ForEach]` methods, and every type containing it, must be
`partial`, because the generator adds the jobs and the `GeneratedUpdate` override to it.

Fix: add `partial` to the class (and to any containing types).

```cs
public class HealSystem : EcsSystem              // EECS001: the generator cannot add the job to it
{
    [ForEach] static void Heal(ref Health hp) => hp.Value++;
}
```

```csharp
public partial class HealSystem : EcsSystem
{
    [ForEach] static void Heal(ref Health hp) => hp.Value++;
}
```

## EECS002

**Invalid `[ForEach]` parameter.** Every parameter must be a `ref` / `in` / `ref readonly` struct implementing
`IComponent` or `ISingletonComponent`, a `Span<T>` / `ReadOnlySpan<T>` of an `IComponent` struct, an `Entity`, or
the `World`.

Fix: pass components and singletons by `ref` (write) or `in` / `ref readonly` (read); remove other parameters and get
what you need from data instead.

```cs
[ForEach] static void Move(ref Position p, float dt)   // EECS002: a float is not ECS data
```

```csharp
public record struct DeltaTime(float Seconds) : ISingletonComponent;   // the value becomes a singleton

public partial class MoveSystem : EcsSystem
{
    [ForEach]
    static void Move(ref Position p, in Velocity v, in DeltaTime dt)
    {
        p.X += v.X * dt.Seconds;
        p.Y += v.Y * dt.Seconds;
    }
}
```

## EECS003

**Invalid component count.** A `[ForEach]` method must take between 1 and 9 components (singletons, `Entity` and
`World` do not count), or none when a `[With<T>]` (else a `[Changed<T>]`) names the component to query
(`[ForEach, With<Dead>] static void Remove(Entity e)`). `[Removed<T>]` does not name one: the entities it selects no
longer have `T`, so the method also needs a component parameter or a `[With<U>]`.

Fix: with no component, use `OnUpdate`; with more than 9, move the extra requirements to `[With<T>]` or split the
method.

```cs
[ForEach] static void Remove(Entity e) => e.Destroy();   // EECS003: which entities? no component names them
```

```csharp
public partial class CorpseCleanupSystem : EcsSystem
{
    [ForEach, With<Dead>]                          // the filter names what to query
    static void Remove(Entity e) => e.Destroy();
}
```

## EECS004

**Generic systems are not supported.** `[ForEach]` in a generic type, or a generic `[ForEach]` method.

Fix: make the system and the method non-generic; share code through a non-generic base class (its `[ForEach]` methods
run first in derived systems).

```cs
public partial class ClampSystem<T> : EcsSystem where T : struct, IComponent   // EECS004
```

```csharp
public partial class ClampSystem : EcsSystem
{
    [ForEach] static void Clamp(ref Health hp) => hp.Value = Math.Max(hp.Value, 0);
}

public partial class PlayerClampSystem : ClampSystem          // Clamp runs first, then Floor
{
    [ForEach, With<Player>] static void Floor(ref Health hp) => hp.Value = Math.Max(hp.Value, 1);
}
```

## EECS005

**`[ForEach]` outside a system.** The type declaring `[ForEach]` methods does not derive from `EcsSystem`.

Fix: derive the type from `EcsSystem`, or use `world.ForEach` / `world.Query` for ad-hoc iteration.

```cs
public static class Physics                       // EECS005: nothing runs it
{
    [ForEach] static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
}
```

```csharp
public partial class PhysicsSystem : EcsSystem
{
    [ForEach] static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
}
```

## EECS006

**ECS data must be a struct.** A class implements `IComponent` or `ISingletonComponent`. Components and singletons
are stored by value. (Interfaces may extend the markers; classes may not implement them.)

Fix: change the class to a struct (`record struct` works well).

```cs
public class Wallet : IComponent { public int Gold; }   // EECS006
```

```csharp
public record struct Wallet(int Gold) : IComponent;
```

## EECS007

**Systems must be stateless.** A system has a mutable field or a settable auto-property, static ones included. State
in a system is invisible to other systems, queries and persistence, and it is lost or duplicated when the system is
removed or re-added; a static one is also shared by every world.

Fix: move per-entity state into a component and per-world state into a [singleton](singletons.md). `readonly`
fields (dependencies, reusable scratch buffers), `init`-only properties and computed properties are allowed.

```cs
public partial class SpawnSystem : EcsSystem
{
    private int _spawned;                                  // EECS007: state no one else can see or save
    protected override void OnUpdate() => _spawned++;
}
```

```csharp
public record struct Spawned(int Count) : ISingletonComponent;

public partial class SpawnSystem : EcsSystem
{
    protected override void OnUpdate() => World.GetOrAddSingleton<Spawned>().Count++;
}
```

## EECS008

**Systems must not reference systems.** A field or auto-property of a system has a system type, or a system looks
another one up with `World.GetSystem<T>()`. Systems that call each other are coupled: neither can be removed,
reordered or tested alone. (`GetSystem` is for tooling, tests and the code that hosts the world.)

Fix: write the data the other system reads (a component or a singleton) and order the two with `Priority`. See
[ECS principles, rule 3](../ecs-principles.md#3-systems-never-reference-systems).

```cs
public partial class ScoreSystem : EcsSystem
{
    private readonly KillSystem _kills;                    // EECS008: the two can no longer change apart
}
```

```csharp
public partial class KillSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Kill(Entity e, in Health hp) { if (hp.Value <= 0) e.SetDead(); }
}

public sealed class ScoreSystem : EcsSystem
{
    public override int Priority => 10;                    // after KillSystem: it reads the Dead it wrote

    // One pass destroys the corpses and says how many there were: each is counted once
    protected override void OnUpdate() => World.GetSingleton<Score>().Deaths += World.DestroyEntities<Dead>();
}
```

## EECS009

**Batch `[ForEach]` mixes spans and per-entity parameters.** A method that takes component spans receives whole runs
of rows, so a per-entity parameter (a `ref` / `in` component, an `Entity`) has no single value for the call, and the
`World` could change rows while the spans point into them. An `EntitySpan` without component spans has no run to
describe.

Fix: take every component as a span (`Span<T>` to write, `ReadOnlySpan<T>` to read), singletons besides, and an
`EntitySpan` for the run's entities; or write a per-entity method instead.

```cs
[ForEach] static void Drift(Span<Position> p, in Velocity v)   // EECS009: v has no single value for a run of rows
```

```csharp
public partial class DriftAllSystem : EcsSystem
{
    [ForEach]
    static void Drift(Span<Position> p, ReadOnlySpan<Velocity> v)   // element i of both spans is one entity
    {                                                                   // (vectorize it: see performance.md)
        for (int i = 0; i < p.Length; i++)
        {
            p[i].X += v[i].X;
            p[i].Y += v[i].Y;
        }
    }
}
```

## EECS010

**`[ForEach(Parallel = true)]` cannot run on several threads.** A parallel method runs chunks of rows on several
threads at once, so it must make no structural change (static, no `World`, its `Entity` used only to read: `Id`,
`Has`, `Get`, ...), be per entity (not a batch method), and write no singleton (`ref` singleton parameters would race
between chunks).

Fix: take singletons as `in`, move structural changes into a sequential system that reacts to data the parallel one
writes, or drop `Parallel = true`.

```cs
[ForEach(Parallel = true)] static void Kill(Entity e, in Health hp)   // EECS010: e.Set changes structure
{
    if (hp.Value <= 0) e.SetDead();
}
```

```csharp
public record struct Regen(int PerTick) : ISingletonComponent;

public partial class RegenSystem : EcsSystem
{
    [ForEach(Parallel = true), Without<Dead>]
    static void Regenerate(ref Health hp, in Regen regen) => hp.Value += regen.PerTick;   // reads the singleton only
}
```

## EECS011

**Write through a read-only parameter.** A `[ForEach]` method takes a component or singleton as `in` (or a component
as `ReadOnlySpan<T>`) and changes data it holds by reference: it calls a mutating method on a collection (`Add`,
`Remove`, `Clear`, `Insert`, `Push`, `Enqueue`, `Sort`, ...), assigns an element (`grid.Cells[i] = x`) or a member of a
class it holds (`holder.Owner.Gold++`). The compiler allows it because only the reference is read-only, but the write
is scheduled as a read (parallel systems that read the same data race with it) and never stamped, so `[Changed<T>]`
systems and value indexes miss it.

Fix: take the parameter by `ref`. The rule sees changes made directly on the parameter's members; a reference copied
into a local, or changed inside a method of your own class, is not followed. Collections in components are allowed;
[ECS principles, rule 8](../ecs-principles.md#8-choose-how-to-hold-a-collection) says when to prefer entities.

```cs
public record struct Grid(List<int> Cells) : ISingletonComponent;

[ForEach] static void Mark(in Position p, in Grid grid) => grid.Cells.Add((int)p.X);   // EECS011: 'in', but written
```

```csharp
public record struct Grid(System.Collections.Generic.List<int> Cells) : ISingletonComponent;

public partial class GridSystem : EcsSystem
{
    [ForEach]
    static void Mark(in Position p, ref Grid grid) => grid.Cells.Add((int)p.X);   // ref: scheduled as a write
}
```

## EECS012

**`DynamicBuffer<T>` held by value.** A [`DynamicBuffer<T>`](dynamic-buffers.md) refers to its list, so a copy of the
value refers to the same list: writes through the copy are never stamped as changed, and storing the copy gives a second
entity (or a snapshot) the same list. The rule reports a `DynamicBuffer<T>` field or property, a by-value parameter or
return, a local that is not a `ref` local, an assignment of an existing buffer, and an existing buffer passed to `Set`,
`CreateEntity` or another generic method that stores it.

Fix: reach the buffer through its entity: `ref` / `in` parameters, `ref var bag = ref entity.Get<DynamicBuffer<T>>()`,
`entity.Read<DynamicBuffer<T>>()`. A new component value is `new DynamicBuffer<T>()` (or `default`), which holds no
list.

```cs
var bag = player.Read<DynamicBuffer<Loot>>();                       // EECS012: a copy
other.Set(player.Read<DynamicBuffer<Loot>>());                      // EECS012: two entities, one list
```

```csharp
public record struct Loot(int Item);

public static class Bags
{
    public static int Items(Entity player) => player.Read<DynamicBuffer<Loot>>().Count;   // read through the entity

    public static void Give(Entity player, Loot loot)
    {
        ref var bag = ref player.Get<DynamicBuffer<Loot>>();                               // a ref local: a tracked write
        bag.Add(loot);
    }

    public static Entity Spawn(World world) => world.CreateEntity(new DynamicBuffer<Loot>());   // a new, empty buffer
}
```

## EECS013

**Structural change in a `[ForEach]` method that promises none.** A `[ForEach]` method that is static and takes no
`Entity` or `World` (or uses its `Entity` only to read) cannot change structure as far as the generator can see, so it
walks the rows without re-checking storage after each call, and the system shares a [parallel stage](threading.md#parallel-systems)
with other systems. A structural change made anyway, through an `Entity` a component holds (a relation) or a world kept
elsewhere, breaks both: the pass keeps refs into rows that moved, and a change inside a parallel stage throws. The rule
reports a call to a structural member of `Entity` (`Set`, `Remove`, `Destroy`, `GetOrAdd`, `Clone`, and the
generated `SetX`, `GetOrAddX`, `RemoveX`) or `World` (`CreateEntity`, `CreateEntities`, `Instantiate`,
`DestroyEntities`, singletons, systems, `Update`, `Capture`, `Restore`, ...) in such a method.

Fix: take the `Entity` as a parameter and make the change through it (the system then runs alone, with a pass that
notices the change), or record the change as data for a system that makes it.

```cs
[ForEach] static void Kill(in Target target) => target.Entity.Destroy();   // EECS013: no Entity or World parameter
```

```csharp
public record struct Target(Entity Entity) : IComponent;
public struct Doomed : IComponent;                // a tag

public partial class MarkTargets : EcsSystem
{
    [ForEach, With<Target>]
    static void Mark(Entity hunter, in Target target)
    {
        hunter.Remove<Target>();             // a structural change through the parameter: this system runs alone
        target.Entity.SetDoomed();         // and its pass notices this one too
    }
}
```

The rule sees the method's own body: a change made in a helper method it calls is not followed. Inside a parallel stage
or a parallel `[ForEach]` the runtime still throws for it ([what the world synchronizes](threading.md#what-the-world-synchronizes)).

## EECS014

**Structural change on another thread.** A world's structure belongs to its owner thread. The rule reports a structural
call (the members EECS013 lists, `Instantiate` and `Clone` included) in a lambda or anonymous method handed to `Task.Run`, `TaskFactory.StartNew`, `new Task`, `Parallel.*`,
`ThreadPool.QueueUserWorkItem` (and its relatives), `new Thread` or `new Timer`, when the world or entity it changes can
be changed by another thread at the same time:

- `Parallel.*` runs the delegate on several threads at once, so any world or entity captured from outside it races
  (`worlds[i]`, a world per item, does not);
- the hand-off sits in a loop, so the delegate runs several times over the same captured world or entity;
- the code around the hand-off (the method, or the type for a field) changes the same world's structure too.

A world handed to a thread that then owns it (a world per thread, a world created inside the delegate) is not reported.

Fix: hand the change to the owner thread as data, through a queue its system drains
([input from other threads](threading.md#input-from-other-threads)), or give the other thread its own world.

```cs
Task.Run(() => world.CreateEntity(new Position(0, 0)));    // EECS014: the caller changes this world too
world.CreateEntity(new Position(1, 1));

Parallel.For(0, players.Length, i => players[i].Destroy()); // EECS014: entities of one world, several threads at once
```

```csharp
public static class WorldThreads
{
    // A world per thread: each thread owns its world, nothing is shared.
    public static Thread[] Start(World[] worlds)
    {
        var threads = new Thread[worlds.Length];
        for (int i = 0; i < worlds.Length; i++)
        {
            var world = worlds[i];                                      // one variable per iteration
            threads[i] = new Thread(() => { for (int t = 0; t < 1_000; t++) world.Update(); });
            threads[i].Start();
        }

        return threads;
    }
}
```

Like EECS013, the rule sees the delegate's own body, and a receiver it cannot follow (a method's result) is not
reported.

## EECS015

**Indexed component written through a ref.** A value index follows `Set`, `CreateEntity` and `CreateEntities`; a
write through a `ref` changes the key behind its back. The rule reports, for a component implementing `IIndexed<TKey>`:

- `Get<T>()`, `GetOrAdd<T>()` or `TryGetRef<T>()` on an entity;
- a `ref` local bound to it, or an assignment to it (or a field of it) through a query item;
- a `ref` or `Span<T>` parameter of it in a `[ForEach]` method or an `IForEach*` job.

Fix: read it with `in`, `ReadOnlySpan<T>`, `Read<T>()` or the generated property, and write it with `Set` (or the
generated `SetX`).

```cs
player.Get<Zone>() = new Zone(2);                       // EECS015: the index keeps the old zone
[ForEach] static void Move(ref Zone zone) => zone = new Zone(2);   // EECS015
```

```csharp
public record struct Zone(int Id) : IComponent, IIndexed<int> { public int Key => Id; }
public record struct Portal(int To) : IComponent;

public partial class TakePortals : EcsSystem
{
    [ForEach]
    static void Take(Entity player, in Zone zone, in Portal portal)
    {
        if (zone.Id != portal.To) player.SetZone(portal.To);         // a Set: the index moves the player at once
    }
}
```

## EECS016

**`[Removed<T>]` method that requires `T`.** `[Removed<T>]` visits entities that lost `T` and still lack it. Taking `T`
as a parameter, or requiring it with `[With<T>]` or `[Changed<T>]` on the same method, asks for entities that have
`T`: no row passes both, so the method would never run.

Fix: read the entity's other components (the row keeps them), and handle "added again" or "written" in another method
with `[Changed<T>]`.

```cs
[ForEach, Removed<Burning>] static void Out(in Burning burning, ref Replica r)   // EECS016: the entity lacks Burning
[ForEach, Removed<Burning>, With<Burning>] static void Out(ref Replica r)        // EECS016: no row passes both
```

```csharp
public record struct Burning(int Ticks) : IComponent, ITrackChanges;
public record struct Replica(int Health, bool Burning) : IComponent;

public partial class ReplicateFire : EcsSystem
{
    [ForEach, Changed<Burning>]
    static void Lit(ref Replica replica) => replica.Burning = true;

    [ForEach, Removed<Burning>]                       // the other components of the row are still there
    static void Out(ref Replica replica) => replica.Burning = false;
}
```
