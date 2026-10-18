# Analyzer rules (EECS002-EECS024)

[Docs index](../README.md) · Related: [Systems](systems.md) · [ECS principles](../ecs-principles.md)

The EasyEcs analyzer ships in the package next to the source generator. Code that breaks the
[ECS principles](../ecs-principles.md) does not build: the rules are errors, except three warnings for code that
works but costs (EECS017, EECS018, EECS020). The generator skips invalid input and the analyzer reports it, so
"not generated" always means "reported". Where a rule has one mechanical answer, the IDE offers it as a code fix
(EasyEcs.CodeFixes, in the same package), and only when the result compiles.

| Id | Severity | Rule | Code fix |
|---|---|---|---|
| [EECS002](#eecs002) | Error | Invalid `[ForEach]` parameter | `in` / `ref` for a component taken by value |
| [EECS003](#eecs003) | Error | A `[ForEach]` method takes 1 to 9 components, or none with a `[With<T>]` or `[Changed<T>]` | |
| [EECS004](#eecs004) | Error | A system's `[ForEach]` method is not generic | |
| [EECS005](#eecs005) | Error | An instance `[ForEach]` method belongs to a system | Make it static |
| [EECS006](#eecs006) | Error | Components and singletons are structs | Make it a struct |
| [EECS007](#eecs007) | Error | Systems are stateless | |
| [EECS008](#eecs008) | Error | Systems do not reference systems | |
| [EECS009](#eecs009) | Error | A batch `[ForEach]` method takes only spans, singletons and an `EntitySpan` | |
| [EECS010](#eecs010) | Error | A `[ForEach(Parallel = true)]` method can run on several threads | Run it on one thread |
| [EECS011](#eecs011) | Error | A `[ForEach]` method does not change what a read-only parameter holds | Take it by `ref` |
| [EECS012](#eecs012) | Error | A `DynamicBuffer<T>` is never copied | `in`, or `ref` with the callers |
| [EECS013](#eecs013) | Error | A `[ForEach]` method that takes no `Entity` or `World` changes no structure | |
| [EECS014](#eecs014) | Error | Structural changes stay on the world's owner thread | |
| [EECS015](#eecs015) | Error | An indexed component is written with `Set` | |
| [EECS016](#eecs016) | Error | A `[Removed<T>]` method does not require `T` | |
| [EECS017](#eecs017) | Warning | Components store an `EntityId`, not an `Entity` | |
| [EECS018](#eecs018) | Warning | `UpdateInterval` and `UpdatePhase` are constants | |
| [EECS019](#eecs019) | Error | Systems are not ordered in a cycle | |
| [EECS020](#eecs020) | Warning | Systems are ordered within their group | |
| [EECS021](#eecs021) | Error | A `[ForEach]` entry point does not collide with a method | |
| [EECS022](#eecs022) | Error | The generated code can name a `[ForEach]` method's types | Make it internal |
| [EECS023](#eecs023) | Error | Filters go on `[ForEach]` methods and system groups | Remove the filter |
| [EECS024](#eecs024) | Error | `[ForEach]` is not on a local function | |

## EECS002

**Invalid `[ForEach]` parameter.** Every parameter must be a `ref` / `in` / `ref readonly` struct implementing
`IComponent` or `ISingletonComponent`, a `Span<T>` / `ReadOnlySpan<T>` of an `IComponent` struct, a `Maybe<T>` of one
(an optional component), an `Entity`, or the `World`. A [`[ForEach]` method outside a system](systems.md#foreach-outside-systems)
may also take values of any other type: its generated entry point takes them from the caller.

Fix: pass components and singletons by `ref` (write) or `in` / `ref readonly` (read); remove other parameters and get
what you need from data instead. The code fix adds `in` or `ref` to a component or singleton taken by value.

```cs
public class MoveSystem : EcsSystem
{
    [ForEach] static void Move(ref Position p, float dt) { }   // EECS002: a system's method gets no caller's value
}
```

```csharp
public record struct DeltaTime(float Seconds) : ISingletonComponent;   // the value becomes a singleton

public class MoveSystem : EcsSystem
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

**Invalid component count.** A `[ForEach]` method must take between 1 and 9 components (singletons, `Entity`,
`World` and a runner's other values do not count; a `Maybe<T>` does), or none when a `[With<T>]` (else a
`[Changed<T>]`) names the component to query (`[ForEach, With<Dead>] static void Remove(Entity e)`). `[Removed<T>]`
does not name one: the entities it selects no longer have `T`, so the method also needs a component parameter or a
`[With<U>]`.

Fix: with no component, use `OnUpdate`; with more than 9, move the extra requirements to `[With<T>]` or split the
method.

```cs
[ForEach] static void Remove(Entity e) => e.Destroy();   // EECS003: which entities? no component names them
```

```csharp
public class CorpseCleanupSystem : EcsSystem
{
    [ForEach, With<Dead>]                          // the filter names what to query
    static void Remove(Entity e) => e.Destroy();
}
```

## EECS004

**Generic `[ForEach]` method in a system.** A system runs its `[ForEach]` methods itself, once per update, so a generic
method would get no type arguments. Generic systems are fine (each closed type is a system of its own,
[generic systems](systems.md#generic-systems)), and so are generic `[ForEach]` methods outside systems, whose entry
points take the type arguments from the caller.

Fix: make the system generic instead of the method.

```cs
public class ClampSystem : EcsSystem
{
    [ForEach] static void Clamp<T>(ref T value) where T : struct, IComponent { }   // EECS004: which T?
}
```

```csharp
public interface IClamped { int Value { get; set; } }

public class ClampSystem<T> : EcsSystem where T : struct, IComponent, IClamped   // AddSystem<ClampSystem<Health>>()
{
    [ForEach] static void Clamp(ref T value) => value.Value = Math.Max(value.Value, 0);
}
```

## EECS005

**Instance `[ForEach]` method outside a system.** A `[ForEach]` method of a type that is no `EcsSystem` gets a
generated entry point that takes the world (`Physics.Move(world)`), which has no instance to call the method on.

Fix: make the method static (the code fix does, when the body uses no instance member), or move it into a system.

```cs
public class Physics
{
    [ForEach] void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }   // EECS005: on which Physics?
}
```

```csharp
public static class Physics                                   // Physics.Move(world) runs it once over the world
{
    [ForEach] public static void Move(ref Position p, in Velocity v) { p.X += v.X; p.Y += v.Y; }
}
```

## EECS006

**ECS data must be a struct.** A class implements `IComponent` or `ISingletonComponent`. Components and singletons
are stored by value. (Interfaces may extend the markers; classes may not implement them.)

Fix: change the class to a struct (`record struct` works well). The code fix does, for a class declared once, with
no base class and no member a struct cannot have (`protected`, `virtual`, `abstract`, a finalizer).

```cs
public class Wallet : IComponent { public int Gold; }   // EECS006
```

```csharp
public record struct Wallet(int Gold) : IComponent;
```

## EECS007

**Systems must be stateless.** A system has a mutable field, a settable auto-property or a field-like event, static
ones included, or a member writes a primary-constructor parameter (a hidden field). State
in a system is invisible to other systems, queries and persistence, and it is lost or duplicated when the system is
removed or re-added; a static one is also shared by every world.

Fix: move per-entity state into a component and per-world state into a [singleton](singletons.md). `readonly`
fields (dependencies, reusable scratch buffers), `init`-only properties and computed properties are allowed.

```cs
public class SpawnSystem : EcsSystem
{
    private int _spawned;                                  // EECS007: state no one else can see or save
    protected override void OnUpdate() => _spawned++;
}
```

```csharp
public record struct Spawned(int Count) : ISingletonComponent;

public class SpawnSystem : EcsSystem
{
    protected override void OnUpdate() => World.GetOrAddSingleton<Spawned>().Count++;
}
```

## EECS008

**Systems must not reference systems.** A field, auto-property, event or primary-constructor parameter of a system
has a system type (an array of systems or a type argument included), or a system looks another one up with
`World.GetSystem<T>()`. Systems that call each other are coupled: neither can be removed,
reordered or tested alone. (`GetSystem` is for tooling, tests and the code that hosts the world.)

Fix: write the data the other system reads (a component or a singleton) and order the two with `[UpdateAfter<T>]`. See
[ECS principles, rule 3](../ecs-principles.md#3-systems-never-reference-systems).

```cs
public class ScoreSystem : EcsSystem
{
    private readonly KillSystem _kills;                    // EECS008: the two can no longer change apart
}
```

```csharp
public class KillSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Kill(Entity e, in Health hp) { if (hp.Value <= 0) e.SetDead(); }
}

[UpdateAfter<KillSystem>]                                  // it reads the Dead KillSystem wrote
public sealed class ScoreSystem : EcsSystem
{
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
public class DriftAllSystem : EcsSystem
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
writes, or drop `Parallel = true` (the code fix does).

```cs
[ForEach(Parallel = true)] static void Kill(Entity e, in Health hp)   // EECS010: e.Set changes structure
{
    if (hp.Value <= 0) e.SetDead();
}
```

```csharp
public record struct Regen(int PerTick) : ISingletonComponent;

public class RegenSystem : EcsSystem
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

Fix: take the parameter by `ref` (the code fix does; a `ReadOnlySpan<T>` becomes a `Span<T>`, and a singleton of a
parallel method gets no fix, since a `ref` singleton is EECS010). Calls that return a new value (`string`, immutable
and frozen collections) are reads. The rule sees changes made directly on the parameter's members; a reference copied
into a local, or changed inside a method of your own class, is not followed. Collections in components are allowed;
[ECS principles, rule 8](../ecs-principles.md#8-choose-how-to-hold-a-collection) says when to prefer entities.

```cs
public record struct Grid(List<int> Cells) : ISingletonComponent;

[ForEach] static void Mark(in Position p, in Grid grid) => grid.Cells.Add((int)p.X);   // EECS011: 'in', but written
```

```csharp
public record struct Grid(System.Collections.Generic.List<int> Cells) : ISingletonComponent;

public class GridSystem : EcsSystem
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

Fix: reach the buffer through its entity: `ref` / `in` parameters (the code fix adds `in`, or `ref` together with each
caller's argument when every caller passes a variable; none for an override or an interface's method, whose signature
is shared), `ref var bag = ref entity.Get<DynamicBuffer<T>>()`,
`entity.Read<DynamicBuffer<T>>()`. A new component value is `new DynamicBuffer<T>()` (or `default`), which holds no
list; a [prefab](prefabs.md) gets its elements with `SetBuffer(…)` or the generated `SetLoot(…)`.

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
reports a call to a structural member of `Entity` (`Set`, `Remove`, `Destroy`, `GetOrAdd`, `Clone`, `Disable`,
`Enable`, `AddRelation`, `RemoveRelation`, and the generated `SetX`, `GetOrAddX`, `RemoveX`) or `World`
(`CreateEntity`, `CreateEntities`, `Instantiate`, `DestroyEntities`, `SetAll`, `RemoveAll`, `DisableAll`,
`EnableAll`, `Load`, `LoadEntities`, `MoveEntities`, `GroupBy`, `RemoveTypes`, singletons, systems, `Update`,
`Capture`, `Restore`, ...) in such a method. A world the method creates itself (`new World()` in a local) is its own.

Fix: take the `Entity` as a parameter and make the change through it (the system then runs alone, with a pass that
notices the change), or record the change as data for a system that makes it.

```cs
[ForEach] static void Kill(in Target target) => target.Entity.Destroy();   // EECS013: no Entity or World parameter
```

```csharp
public record struct Target(Entity Entity) : IComponent;
public struct Doomed : IComponent;                // a tag

public class MarkTargets : EcsSystem
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

A world handed to a thread that then owns it (a world per thread, a world created inside the delegate) is not reported,
and neither is a hand-off the caller waits for at once (`await`, `Wait()`, `Result`, `GetAwaiter().GetResult()`):
nothing runs beside it.

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

public class TakePortals : EcsSystem
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

public class ReplicateFire : EcsSystem
{
    [ForEach, Changed<Burning>]
    static void Lit(ref Replica replica) => replica.Burning = true;

    [ForEach, Removed<Burning>]                       // the other components of the row are still there
    static void Out(ref Replica replica) => replica.Burning = false;
}
```

## EECS017

**A component stores an `Entity` handle** (warning). An `Entity` holds a reference to its world, so a component or
singleton with an `Entity` field is managed: its column is scanned by the garbage collector, every store pays a write
barrier, rows are not copied as blocks, and saves cannot write it.

Fix: store the `EntityId` (8 bytes, unmanaged; a handle converts to it implicitly) and turn it back into a handle with
`world.TryGetEntity(id, out var entity)`, which also tells you whether the entity still lives.

```cs
public record struct Target(Entity Entity) : IComponent;          // EECS017: a managed column
```

```csharp
public record struct Target(EntityId Id) : IComponent;

public class ChaseSystem : EcsSystem
{
    [ForEach]
    static void Chase(ref Position p, in Target target, World world)
    {
        if (world.TryGetEntity(target.Id, out var prey)) p = prey.Read<Position>();   // gone: the id finds nothing
    }
}
```

## EECS018

**A system's schedule is not a constant** (warning). `UpdateInterval` and `UpdatePhase` are read every update, so a
value that changes makes the system skip or repeat runs.

Fix: return a constant. To stop and resume a system, set `Enabled`.

```cs
public override int UpdateInterval => World.EntityCount > 1000 ? 4 : 1;   // EECS018
```

```csharp
public class SaveSystem : EcsSystem
{
    public override int UpdateInterval => 60;    // every 60th update
    public override int UpdatePhase => 30;       // half a cycle after the others with the same interval
}
```

## EECS019

**Systems ordered in a cycle.** `[UpdateBefore<T>]` and `[UpdateAfter<T>]` say which of two systems runs first; a
cycle cannot hold, and `AddSystem` throws when the system that closes it is added. The analyzer reports the cycles it
sees between the systems of the compilation.

Fix: remove one of the orderings.

```cs
[UpdateAfter<B>] public class A : EcsSystem { }
[UpdateAfter<A>] public class B : EcsSystem { }   // EECS019: A after B after A
```

```csharp
public class Movement : EcsSystem { }
[UpdateAfter<Movement>] public class Collision : EcsSystem { }
[UpdateAfter<Collision>] public class Damage : EcsSystem { }
```

## EECS020

**A system ordered against another group** (warning). Orderings apply within one group: `World.Update()` and
`World.Update<TGroup>()` run their groups separately, in the order the caller calls them, so an ordering against a
system of another group never applies.

Fix: order systems of one group, or move the system into the other's group.

```cs
public struct Late : ISystemGroup;
[UpdateInGroup<Late>] public class Cleanup : EcsSystem { }
[UpdateAfter<Cleanup>] public class Movement : EcsSystem { }   // EECS020: Movement runs in the default group
```

```csharp
public struct Late : ISystemGroup;
[UpdateInGroup<Late>] public class Cleanup : EcsSystem { }
[UpdateInGroup<Late>, UpdateAfter<Cleanup>] public class Recycle : EcsSystem { }
```

## EECS021

**A `[ForEach]` entry point has the signature of another method.** Outside a system, the generator writes an overload
of each `[ForEach]` method that takes the `World` (and the change tick, for `[Changed<T>]` / `[Removed<T>]`) followed
by the method's other values. If the type already has a method with that signature, or another `[ForEach]` overload's
entry point has it, the two would collide.

Fix: rename one of them.

```cs
public static class Reports
{
    [ForEach] static void Heal(ref Health hp) => hp.Value++;
    public static void Heal(World world) { }        // EECS021: the entry point Heal(World) exists already
}
```

```csharp
public static class Reports
{
    [ForEach] static void Heal(ref Health hp) => hp.Value++;     // Reports.Heal(world)
    public static void HealAll(World world) => Heal(world);
}
```

## EECS022

**A type out of reach of the generated code.** The generator writes the jobs of `[ForEach]` methods outside the
user's type and calls the methods through `UnsafeAccessor`, so private methods and private systems are fine. The types
of the parameters (and return), and the type of a `[ForEach]` method outside a system, must be internal or public, as
must every type containing them; a `file` type is out of reach of any other file.

Fix: make the type internal (the code fix does, with every private or protected type that contains it, in each part
of a partial type that states an accessibility).

```cs
public class HealSystem : EcsSystem
{
    private struct Regen : IComponent { public int Amount; }      // EECS022: the generated job cannot name it
    [ForEach] static void Heal(ref Health hp, in Regen r) => hp.Value += r.Amount;
}
```

```csharp
public class HealSystem : EcsSystem
{
    internal struct Regen : IComponent { public int Amount; }
    [ForEach] static void Heal(ref Health hp, in Regen r) => hp.Value += r.Amount;
}
```

## EECS023

**A filter on a struct that is no system group.** `[With<T>]`, `[Without<T>]` and `[WithAny<T>]` on an
`ISystemGroup` struct join every `[ForEach]` method of the group's systems. On any other struct, such as a component,
they do nothing.

Fix: remove the filter (the code fix does), or put it on the methods or their group.

```cs
[Without<Dead>] public struct Health : IComponent { public int Value; }   // EECS023: filters nothing
```

```csharp
[Without<Dead>] public struct Alive : ISystemGroup;            // every [ForEach] of the group skips Dead entities
```

## EECS024

**`[ForEach]` on a local function.** The generator writes a job for each `[ForEach]` method and calls it from outside
its type; a local function has no name outside its method.

Fix: make it a method of a system, or a static method of a type (its entry point takes the world).

```cs
static void Report(World world)
{
    [ForEach] static void Count(in Health hp, ref int alive) => alive++;   // EECS024: nothing can call it
}
```

```csharp
public static class Report
{
    [ForEach] static void Count(in Health hp, ref int alive) => alive++;

    public static int Alive(World world)
    {
        int alive = 0;
        Count(world, ref alive);                    // the generated entry point
        return alive;
    }
}
```
