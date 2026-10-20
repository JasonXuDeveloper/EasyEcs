# EasyEcs rules for AI assistants

Rules for writing code that uses EasyEcs (NuGet package `EasyEcs`, .NET 10, C# 14). Docs:
https://ecs.xgamedev.net (full text for models: https://ecs.xgamedev.net/llms-full.txt). Every page is also
available as Markdown: add `.md` to its URL.

## Data

- Components are structs implementing `IComponent`: `public record struct Health(int Value) : IComponent;`.
  A tag is a struct with no fields: `public struct Dead : IComponent;`. Never a class (EECS006).
- Singletons are structs implementing `ISingletonComponent`, one per world: `world.Arena.Tick++`.
- Store `EntityId`, not `Entity`, in components (EECS017).
- Use the generated members: `entity.Health` (a `ref`; `ref readonly` for an `ITrackChanges` or indexed component,
  written with `SetHealth(…)` or, for a tracked one, `WriteHealth()`), `HasHealth`, `SetHealth(100)`, `RemoveHealth()`,
  `IsDead` / `SetDead()` / `RemoveDead()` for tags, `world.Arena` for singletons, `item.Position` on query items.
  `entity.Read<T>()` reads without counting as a write. Prefer them to the generic calls (EECS033).
- Components marked `ITrackChanges` record writes; indexed components (`IIndexed<TKey>`) are written with `Set` or
  `SetX(…)`, never through a `ref` (EECS015).
- A key that must name one entity (an account id, a session token, a cell's occupant) is `IUniqueIndexed<TKey>`: look
  it up with `world.XIndex.Get(key)` / `TryGet(key, out e)`, never with a hand-kept dictionary. A write that would give
  a second entity a taken key throws `InvalidOperationException`; to move a key, change the old holder first. Not for
  `EntityId` keys (EECS036).
- A list per entity is a `DynamicBuffer<T>` component; never copy it by value (EECS012). Call
  `EnsureCapacity(n)` on it (and `world.EnsureCapacity<T…>(n)` on the world) before a wave of known size.
- An external resource (a socket, a seat) lives in an `ICleanupComponent`: destroying the entity keeps it with the
  `Destroyed` tag until a system releases the resource and removes the component.
- An optional component is a `Maybe<T>` parameter, but not in the hottest loops: there, write two methods with
  `[With<T>]` and `[Without<T>]`.

## Systems

- A system derives from `EcsSystem` and holds no state: no instance fields (EECS007), no references to other systems
  (EECS008). State goes into components or singletons.
- One system per responsibility. Per-entity logic is a `static` `[ForEach]` method (EECS005):
  `[ForEach, Without<Dead>] static void Move(ref Position p, in Velocity v) => ...`.
  Take components by `ref` to write and `in` to read (EECS002); a `ref` that only reads is EECS025. Do not change
  what an `in` parameter holds (EECS011). Take only what the method uses: a component it only requires is a
  `[With<T>]` filter (EECS026), and a component or singleton it reads is a parameter, not a lookup through the
  `Entity` or `World` on every row (EECS027). A method that uses nothing of its system is `static` (EECS029).
- Filters on a `[ForEach]` method: `[With<T>]`, `[Without<T>]`, `[WithAny<T>]`, `[Changed<T>]`, `[Removed<T>]`.
  A `[Removed<T>]` method does not take `T` (EECS016). On a system group struct, for all its systems: `[With<T>]`,
  `[Without<T>]`, `[WithAny<T>]` (on any other struct: EECS023), or `IGroupWith<T>` / `IGroupWithout<T>` implemented
  by the group or an interface it implements; a generic group interface needs the interfaces, as an attribute cannot
  name a type parameter. `[Changed<T>]` / `[Removed<T>]` go on methods only.
- React to changes with `[Changed<T>]` / `[Removed<T>]` on an `ITrackChanges` component, not with dirty flags.
  For "any of these changed" use `[ChangedAny<A, B>]` / `[RemovedAny<A, B>]` (one attribute, not several `Changed`,
  which mean all of them).
- A system that recomputes a tracked component every tick takes it as `Mut<T>` and writes with
  `target.SetIfChanged(value)` (or `Write()` when it knows it writes): a `ref` parameter stamps every visited row.
  Outside a pass, `entity.SetHealthIfChanged(…)`. Keep a plain `ref` where most rows really change.
- Per-client replication: keep one `static readonly QueryFilter` (`QueryFilter.Create().ChangedAny<A, B>(0)`) and run
  it with each client's acknowledged tick, `filter.Since(client.Ack)`; never one filter per client per tick.
- Despawns for replication: set `WorldOptions.DestroyLog` to at least the destroys of the longest gap between two
  reads of a client, and call `world.DestroyedSince(client.Ack, buffer, out count)`; on `false`, send that client the
  whole state. `[Removed<T>]` does not see destroyed entities. A resource to release exactly once is a cleanup
  component, not the log.
- Spread heavy per-row work that may lag over runs with `[ForEach(Slices = N)]`; never on `[Changed<T>]` /
  `[Removed<T>]` methods (EECS035). `system.RunCount % N` is the current slice; `Query`, `Count` and bulk calls refuse
  a sliced filter.
- To change structure (add, remove, create, destroy) in a `[ForEach]` method, take `Entity` or `World`
  (EECS013). Changes apply at once; the pass stays safe.
- Order systems with `[UpdateBefore<T>]` / `[UpdateAfter<T>]` inside the same group; no cycles (EECS019, EECS020).
  There is no `Priority`. Groups are `ISystemGroup` structs with `[UpdateInGroup<T>]`; cadence is `UpdateInterval`
  and `UpdatePhase`, constants (EECS018).
- A system never calls `World.Update()` (EECS034): the host runs the world.
- `[ForEach(Parallel = true)]` methods write data only and make no structural changes (EECS010); a later system on the
  owner thread acts on that data.
- A request or message for one entity runs as a struct job on it: `entity.Run<T…, TJob>(ref job, Filter)` with an
  `IForEachEntity` job and a `static readonly` `QueryFilter`; it returns `false` when the entity is gone, lacks a
  component or fails the filter. It is structural: never inside a parallel `[ForEach]` (EECS010), never in a
  `[ForEach]` method without `Entity` or `World` (EECS013), never off the owner thread (EECS014). A job that makes no
  structural change implements `INoStructuralChanges`; `static int Writes` names the components it writes.
- No events or callbacks: a state change is data (a tag, a component, a message entity, a tracked write). There is no
  command buffer and no `Added<T>`: add a tag such as `Spawned` and remove it in the system that reacts.

## Threads and refs

- A world belongs to one owner thread. Systems, queries and structural changes run there; other threads post work to
  it (EECS014). Use one world per thread for more cores, or `ParallelSystems` / parallel `[ForEach]`.
- Never keep a `ref` to a component across an `await` or a structural change of another entity; read it again.
- `Query` results are ref structs: no `await` inside the loop, no storing items, no LINQ over them.

## Performance

- An `EECS` warning (EECS025 to EECS032, and EECS017, EECS018, EECS020) means the code costs: apply its fix. The one
  suggestion, EECS033, is the same cost written shorter. An `EECS` error means the code cannot work.
- Hot paths allocate nothing: no LINQ, closures, boxing, string formatting or `params object[]` in systems (EECS028).
- Keep a `QueryFilter` in a `static readonly` field when a query runs every tick.
- Prefer `[ForEach]` methods over hand-written `Query` loops; use bulk calls (`SetAll`, `RemoveAll`, `DisableAll`,
  `EnableAll`, `CreateEntities`, `DestroyEntities`) for many entities at once, not a loop over a query (EECS030), and
  `CreateEntities` or `EnsureCapacity` before creating many entities in a loop (EECS031). Ask `world.Any<T>()` and
  `world.Count<T>()`, and filter with `QueryFilter`, instead of counting or testing in a loop (EECS032).
- Removing a component keeps the row (one bit flips), so status components that come and go are cheap; adding a new
  kind of component moves the row once.
- Use `Prefab` + `world.Instantiate` for repeated spawns, `GroupBy<T>()` to split tables by zone or team, and value
  indexes for lookups by key. Give an instance its own values in the same call, `world.Instantiate(prefab, new
  ChildOf(parent), new Position(x, y))`, not `Instantiate` then `Set` (two row copies).
- Call `world.TrimExcess()` only at a quiet moment after a peak (a wave cleared, a match ended), never every tick:
  what it drops is allocated again by the next structural changes.

## What EasyEcs does not do

- It does not run on Unity, Godot or runtimes older than .NET 10.
- It has no whole-entity disable flag: add a tag and filter with `Without<T>`; `entity.Disable<T>()` hides one
  component and keeps its value.
- It has no actor type. An actor is a pattern: one type per kind of actor that is its key component
  (`IIndexed<TSelf>`), its system group and the group's filter (`IGroupWith<In<TSelf>>`), declared once through a
  generic interface (`interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, IGroupWith<In<TSelf>>`).
  Implement `Key` explicitly on a record struct (`Shop IIndexed<Shop>.Key => this;`): a public `Key => this` makes
  the record's `ToString()` recurse until the stack overflows.
  https://github.com/JasonXuDeveloper/EasyEcs/tree/master/samples/Actors
- It has no shared components: put shared data in a singleton and store an id.

## Persistence

- `world.Capture()` / `Restore(snapshot)` copy a world in memory (undo, rollback, tests); `world.Save` / `Load` write
  bytes that survive a restart or go to another process.
- Collect a subtree with `world.Descendants(root)` (not only `Children`) before `SaveEntities` or `MoveEntities`:
  a child left behind dies with the original.
- Durable state is a record: `SaveEntities(writer, ids, Persistent)` with a `static readonly ComponentSet` of the
  durable components only, never the runtime ones (connections, timers, caches).
- Restore a record with `LoadEntitiesInto(bytes, entities, markWritten: false)`, so a system that saves or
  replicates changed components does not send it straight back; the values it replaces (a prefab's defaults) count as
  never written too, so one prefab per kind serves new and restored entities. `LoadEntitiesInto` is structural (EECS010, EECS013, EECS014).
- Resume a saved world after a restart with `world.Load(bytes, markWritten: false)`: no loaded row counts as written
  or removed, also for systems added after it. A system added to a world that already holds its state starts with
  `world.AddSystem<T>(changesFromNow: true)`.
- A restart save leaves runtime state out: `world.Save(writer, Runtime)` with a `static readonly ComponentSet` of the
  runtime components and singletons (`.With<Session>().WithSingleton<Config>()`). The new process sets its
  configuration first, then `world.Load(bytes, ComponentSet.Create().WithSingleton<Config>(), markWritten: false)`,
  which keeps it. A `keep` set holds singletons only; a `SaveEntities` set holds components only.

## Project layout and debugging

- Lay a project out like `dotnet new easyecs` (package `EasyEcs.Templates`): data in `Components/`, one system per
  file in `Systems/`, and `Program.cs` only as the host: it builds the world, sets every singleton before the first
  update and adds the systems in one method the tests share. Input from other threads goes through a queue held in a
  singleton. https://ecs.xgamedev.net/en/docs/guide/project-structure
- To look inside a world: `world.Describe()` (tables, rows, bytes), `entity.Describe()`, `system.Stats` with
  `WorldOptions.MeasureSystems` (times, `LastAllocated` bytes, `LastEntities` visited), `system.Error` with
  `SystemErrorPolicy.Disable`; on the owner thread between updates, never in game logic.
  https://ecs.xgamedev.net/en/docs/guide/diagnostics
- Log `world.DescribeSchedule()` at startup (or `.ToMermaid()` for a chart) to see stages and why systems wait; a
  pair listed as ordered only by registration needs `[UpdateBefore<T>]` / `[UpdateAfter<T>]` if its order matters.

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
