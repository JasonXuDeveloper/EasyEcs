# EasyEcs rules for AI assistants

Rules for writing code that uses EasyEcs (NuGet package `EasyEcs`, .NET 10, C# 14). Docs:
https://ecs.xgamedev.net (full text for models: https://ecs.xgamedev.net/llms-full.txt). Every page is also
available as Markdown: add `.md` to its URL.

## Data

- Components are structs implementing `IComponent`: `public record struct Health(int Value) : IComponent;`.
  A tag is a struct with no fields: `public struct Dead : IComponent;`. Never a class (EECS006).
- Singletons are structs implementing `ISingletonComponent`, one per world: `world.Arena.Tick++`.
- Store `EntityId`, not `Entity`, in components (EECS017).
- Use the generated members: `entity.Health` (a `ref`), `HasHealth`, `SetHealth(100)`, `RemoveHealth()`,
  `IsDead` / `SetDead()` / `RemoveDead()` for tags, `world.Arena` for singletons, `item.Position` on query items.
  `entity.Read<T>()` reads without counting as a write. Prefer them to the generic calls (EECS033).
- Components marked `ITrackChanges` record writes; indexed components (`IIndexed<TKey>`) are written with `Set` or
  `SetX(…)`, never through a `ref` (EECS015).
- A list per entity is a `DynamicBuffer<T>` component; never copy it by value (EECS012).

## Systems

- A system derives from `EcsSystem` and holds no state: no instance fields (EECS007), no references to other systems
  (EECS008). State goes into components or singletons.
- One system per responsibility. Per-entity logic is a `static` `[ForEach]` method (EECS005):
  `[ForEach, Without<Dead>] static void Move(ref Position p, in Velocity v) => ...`.
  Take components by `ref` to write and `in` to read (EECS002); a `ref` that only reads is EECS025. Do not change
  what an `in` parameter holds (EECS011). Take only what the method uses: a component it only requires is a
  `[With<T>]` filter (EECS026), and a component or singleton it reads is a parameter, not a lookup through the
  `Entity` or `World` on every row (EECS027). A method that uses nothing of its system is `static` (EECS029).
- Filters are attributes on the method or on the system group struct: `[With<T>]`, `[Without<T>]`, `[WithAny<T>]`,
  `[Changed<T>]`, `[Removed<T>]` (EECS023). A `[Removed<T>]` method does not take `T` (EECS016).
- To change structure (add, remove, create, destroy) in a `[ForEach]` method, take `Entity` or `World`
  (EECS013). Changes apply at once; the pass stays safe.
- Order systems with `[UpdateBefore<T>]` / `[UpdateAfter<T>]` inside the same group; no cycles (EECS019, EECS020).
  There is no `Priority`. Groups are `ISystemGroup` structs with `[UpdateInGroup<T>]`; cadence is `UpdateInterval`
  and `UpdatePhase`, constants (EECS018).
- A system never calls `World.Update()` (EECS034): the host runs the world.
- `[ForEach(Parallel = true)]` methods write data only and make no structural changes (EECS010); a later system on the
  owner thread acts on that data.
- No events or callbacks: a state change is data (a tag, a component, a message entity, a tracked write). There is no
  command buffer and no `Added<T>`: add a tag such as `Spawned` and remove it in the system that reacts.

## Threads and refs

- A world belongs to one owner thread. Systems, queries and structural changes run there; other threads post work to
  it (EECS014). Use one world per thread for more cores, or `ParallelSystems` / parallel `[ForEach]`.
- Never keep a `ref` to a component across an `await` or a structural change of another entity; read it again.
- `Query` results are ref structs: no `await` inside the loop, no storing items, no LINQ over them.

## Performance

- Hot paths allocate nothing: no LINQ, closures, boxing, string formatting or `params object[]` in systems (EECS028).
- Keep a `QueryFilter` in a `static readonly` field when a query runs every tick.
- Prefer `[ForEach]` methods over hand-written `Query` loops; use bulk calls (`SetAll`, `RemoveAll`, `DisableAll`,
  `EnableAll`, `CreateEntities`, `DestroyEntities`) for many entities at once, not a loop over a query (EECS030), and
  `CreateEntities` or `EnsureCapacity` before creating many entities in a loop (EECS031). Ask `world.Any<T>()` and
  `world.Count<T>()`, and filter with `QueryFilter`, instead of counting or testing in a loop (EECS032).
- Removing a component keeps the row (one bit flips), so status components that come and go are cheap; adding a new
  kind of component moves the row once.
- Use `Prefab` + `world.Instantiate` for repeated spawns, `GroupBy<T>()` to split tables by zone or team, and value
  indexes for lookups by key.

## What EasyEcs does not do

- It does not run on Unity, Godot or runtimes older than .NET 10.
- It has no whole-entity disable flag: add a tag and filter with `Without<T>`; `entity.Disable<T>()` hides one
  component and keeps its value.
- It has no shared components: put shared data in a singleton and store an id.

## Example

```csharp
using EasyEcs;

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public struct Dead : IComponent;

public class MovementSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

[UpdateAfter<MovementSystem>]
public class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp) { if (hp.Value <= 0) unit.SetDead(); }
}

// using var world = new World();
// world.AddSystem<MovementSystem>();
// world.AddSystem<DeathSystem>();
// world.CreateEntity(new Position(0, 0), new Velocity(1, 0), new Health(100));
// world.Update();
```
