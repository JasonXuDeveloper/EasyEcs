# Analyzer rules (EECS001-EECS012)

[Docs index](../README.md) · Related: [Systems](systems.md) · [ECS principles](../ecs-principles.md)

The EasyEcs analyzer ships in the package next to the source generator. EECS001-EECS006, EECS009 and EECS010 are errors: the generator
skips invalid input and the analyzer reports it, so "not generated" always means "reported". EECS007, EECS008,
EECS011 and EECS012 are warnings that enforce the [ECS principles](../ecs-principles.md).

| Id | Severity | Rule |
|---|---|---|
| [EECS001](#eecs001) | Error | A system with `[ForEach]` methods must be `partial` |
| [EECS002](#eecs002) | Error | Invalid `[ForEach]` parameter |
| [EECS003](#eecs003) | Error | A `[ForEach]` method takes 1 to 9 components, or none with a `[With<T>]` or `[Changed<T>]` |
| [EECS004](#eecs004) | Error | Generic systems and methods are not supported |
| [EECS005](#eecs005) | Error | `[ForEach]` outside an `EcsSystem` |
| [EECS006](#eecs006) | Error | Components and singletons must be structs |
| [EECS007](#eecs007) | Warning | Systems should be stateless |
| [EECS008](#eecs008) | Warning | Systems must not reference systems |
| [EECS009](#eecs009) | Error | A batch `[ForEach]` method takes only spans and singletons |
| [EECS010](#eecs010) | Error | A `[ForEach(Parallel = true)]` method must be able to run on several threads |
| [EECS011](#eecs011) | Warning | Components must not hold collections |
| [EECS012](#eecs012) | Warning | A `[ForEach]` method must not change what a read-only parameter holds |

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
(`[ForEach, With<Dead>] static void Remove(Entity e)`).

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

**Systems should be stateless.** A system has a mutable instance field or a settable auto-property. State in a
system is invisible to other systems, queries and persistence, and it is lost or duplicated when the system is
removed or re-added.

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

**Systems must not reference systems.** A field or auto-property of a system has a system type. Systems that call
each other are coupled: neither can be removed, reordered or tested alone.

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
    static void Kill(Entity e, in Health hp) { if (hp.Value <= 0) e.Set(new Dead()); }
}

public partial class ScoreSystem : EcsSystem
{
    public override int Priority => 10;                    // after KillSystem: it reads the Dead it wrote
    [ForEach, With<Dead>]
    static void Count(Entity e, ref Score score)
    {
        score.Deaths++;
        e.Destroy();                                       // counted once
    }
}
```

## EECS009

**Batch `[ForEach]` mixes spans and per-entity parameters.** A method that takes component spans receives whole runs
of rows, so a per-entity parameter (a `ref` / `in` component, an `Entity`) has no single value for the call, and the
`World` could change rows while the spans point into them.

Fix: take every component as a span (`Span<T>` to write, `ReadOnlySpan<T>` to read) and only singletons besides; or
write a per-entity method instead.

```cs
[ForEach] static void Drift(Span<Position> p, in Velocity v)   // EECS009: v has no single value for a run of rows
```

```csharp
public partial class DriftAllSystem : EcsSystem
{
    [ForEach]
    static void Drift(Span<Position> p, ReadOnlySpan<Velocity> v)   // element i of both spans is one entity
    {
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
writes, or drop `Parallel`.

```cs
[ForEach(Parallel = true)] static void Kill(Entity e, in Health hp)   // EECS010: e.Set changes structure
{
    if (hp.Value <= 0) e.Set(new Dead());
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

**Components must not hold collections.** A component (directly, or through the fields of a struct or class it
holds) has a field that is an array or a mutable collection (`List<T>`, `Dictionary<K, V>`, `HashSet<T>`, `Queue<T>`,
any `IEnumerable` but `string`). Its row stores only the reference, so a change to the collection is not a write:
`[Changed<T>]` systems and indexes miss it, parallel systems that take the component as `in` race with it, and
snapshots share the collection instead of copying it.

Fix: make each item an entity that points at its owner (a relation), or keep a small fixed number of values inline
([ECS principles, rule 8](../ecs-principles.md#8-hold-collections-as-entities-not-in-components)). Immutable and frozen
collections (`System.Collections.Immutable`, `System.Collections.Frozen`) are allowed: they cannot change, so replacing
one through `ref` is a tracked write. Singletons may hold collections (a spatial grid, a lookup table): there is one per
world, and [EECS012](#eecs012) still guards writes through `in`.

```cs
public record struct Inventory(List<Entity> Items) : IComponent;   // EECS011: the list changes behind the row's back
```

```csharp
public record struct ItemOf(Entity Owner) : IComponent, IIndexed<Entity>   // each item points at its owner
{
    public Entity Key => Owner;
}

public static class Inventories
{
    public static int CountItems(World world, Entity player) => world.Index<ItemOf, Entity>().Count(player);
}
```

## EECS012

**Write through a read-only parameter.** A `[ForEach]` method takes a component or singleton as `in` (or a component
as `ReadOnlySpan<T>`) and changes data it holds by reference: it calls a mutating method on a collection (`Add`,
`Remove`, `Clear`, `Insert`, `Push`, `Enqueue`, `Sort`, ...), assigns an element (`grid.Cells[i] = x`) or a member of a
class it holds (`holder.Owner.Gold++`). The compiler allows it because only the reference is read-only, but the write
is scheduled as a read (parallel systems that read the same data race with it) and never stamped, so `[Changed<T>]`
systems and value indexes miss it.

Fix: take the parameter by `ref`. The rule sees changes made directly on the parameter's members; a reference copied
into a local, or changed inside a method of your own class, is not followed.

```cs
public record struct Grid(List<int> Cells) : ISingletonComponent;

[ForEach] static void Mark(in Position p, in Grid grid) => grid.Cells.Add((int)p.X);   // EECS012: 'in', but written
```

```csharp
public record struct Grid(System.Collections.Generic.List<int> Cells) : ISingletonComponent;

public partial class GridSystem : EcsSystem
{
    [ForEach]
    static void Mark(in Position p, ref Grid grid) => grid.Cells.Add((int)p.X);   // ref: scheduled as a write
}
```
