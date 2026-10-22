# EasyEcs rules for AI assistants

Rules for writing code that uses EasyEcs (NuGet package `EasyEcs`, .NET 10, C# 14). Docs:
https://ecs.xgamedev.net (full text for models: https://ecs.xgamedev.net/llms-full.txt). Every page is also
available as Markdown: add `.md` to its URL.

## Data

- Components are structs implementing `IComponent`: `public record struct Health(int Value) : IComponent;`.
  A tag is a struct with no fields: `public struct Dead : IComponent;`. Never a class (EECS006).
- Singletons are structs implementing `ISingletonComponent`, one per world: set it with `world.SetArena(…)` before the
  first update, then `world.Arena.Tick++`. `AddSingleton<T>()` throws when the world has `T` already; replace a value
  with `SetArena(…)`.
- Store `EntityId`, not `Entity`, in components and buffers (EECS017).
- `var hp = entity.Health;` copies the value: write through the `ref` (`ref var hp = ref entity.Health;`,
  `entity.Health.Value -= 10`), never a copy you then drop (EECS038).
- Use the generated members: `entity.Health` (a `ref`; `ref readonly` for an `ITracked` or indexed component,
  written with `SetHealth(…)` or, for a tracked one, `WriteHealth()`), `HasHealth`, `SetHealth(100)`, `RemoveHealth()`,
  `IsDead` / `SetDead()` / `RemoveDead()` for tags, `world.Arena` for singletons, `item.Position` on query items.
  `entity.Read<T>()` reads without counting as a write. Prefer them to the generic calls (EECS033).
- Components marked `ITracked` record writes; indexed components (`IIndexed<TKey>`) are written with `Set` or
  `SetX(…)`, never through a `ref` or a `Maybe<T>` (EECS015: read one with `in` or `ReadOnlyMaybe<T>`), and never while a `foreach` walks their index (`Find`, `Children`,
  `Range`): collect the entities first (EECS042).
- A key that must name one entity (an account id, a session token, a cell's occupant) is `IUniqueIndexed<TKey>`: look
  it up with `world.XIndex.Get(key)` / `TryGet(key, out e)`, never with a hand-kept dictionary. A write that would give
  a second entity a taken key throws `InvalidOperationException`; to move a key, change the old holder first. Not for
  `EntityId` keys (EECS036). A component has one index key: never two `IIndexed<TKey>` (EECS036). When the keys a
  world holds at once map onto bounded slots (grid cells modulo the loaded area, small ids), make it
  `IDirectIndexed<TKey>` (`static Slots`, `static SlotOf(key)`, pure arithmetic) instead of keeping a grid of ids by
  hand; never for sparse keys or huge ranges.
- A list per entity is a `DynamicBuffer<T>` component; never copy it by value (EECS012). Call
  `EnsureCapacity(n)` on it (and `world.Reserve<T…>(n)` on the world) before a wave of known size. Write a whole
  buffer through the span `SetCount(n)` returns; compute many passes over the same elements in a `stackalloc`'d span
  and copy it in once. `TrimExcess()` on an empty scratch buffer gives its list back to the pool. Buffers longer than
  4 KB that come and go (a chunk's blocks, a path) set `DynamicBuffer<T>.PoolCapacity` to their length and call
  `DynamicBuffer<T>.ReservePool(peak)` once at startup, after `PoolCapacity` (both process-wide); `world.Reserve` does not
  reserve buffer lists.
- An external resource (a socket, a seat) lives in an `ICleanupComponent`: `Destroy` retires the entity (alive, with
  the `Retired` tag) until a system releases the resource and removes the component, which frees it. Never set or remove `Retired` or
  `Disabled<T>` yourself (EECS045): `Destroy`, `Disable<T>()` and `Enable<T>()` do. A cleanup component and `ChildOf`
  are never disabled (EECS048). A retired entity (kept for its cleanup components) stays retired: `MoveEntities` skips it (and a moved entity leaves
  its cleanup components behind), `Clone()` throws, and `SetAll` reaches it only when the filter names `Retired`.
- An optional component is a `Maybe<T>` parameter (`ReadOnlyMaybe<T>` when the method only reads it, the one an
  indexed component accepts), but not in the hottest loops: there, write two methods with `[With<T>]` and
  `[Without<T>]`.

## Systems

- A system derives from `EcsSystem` and holds no state: no instance fields (EECS007), no references to other systems
  (EECS008). State goes into components or singletons.
- One system per responsibility. Per-entity logic is a `static` `[ForEach]` method (EECS005):
  `[ForEach, Without<Dead>] static void Move(ref Position p, in Velocity v) => ...`.
  Take components by `ref` to write and `in` to read (EECS002); a `ref` that only reads is EECS025. Do not change
  what an `in` parameter holds (EECS011). Take only what the method uses: a component it only requires is a
  `[With<T>]` filter (EECS026), and a component or singleton it reads is a parameter, not a lookup through the
  `Entity` or `World` on every row (EECS027; an indexed component too: `in` or
  `ReadOnlyMaybe<T>`). A method that uses nothing of its system is `static` (EECS029).
  A `[ForEach]` method takes 1 to 9 components (or none with a `[With<T>]` / `[Changed<T>]`, EECS003), is not generic
  (EECS004) nor a local function (EECS024), and its types are visible to the generated code (EECS022); a batch method
  takes spans, singletons and an `EntitySpan` only (EECS009).
- Filters on a `[ForEach]` method: `[With<T>]`, `[Without<T>]`, `[WithAny<T>]`, `[Changed<T>]`, `[Removed<T>]`.
  A `[Removed<T>]` method does not take `T` (EECS016). On a system group struct, for all its systems: `[With<T>]`,
  `[Without<T>]`, `[WithAny<T>]` (on any other struct: EECS023), or `ISystemGroupWith<T>` / `ISystemGroupWithout<T>` implemented
  by the group or an interface it implements; a generic group interface needs the interfaces, as an attribute cannot
  name a type parameter. `[Changed<T>]` / `[Removed<T>]` go on methods only. A method never excludes what it or its
  group requires (EECS049), and a `QueryFilter` names one `InGroup` (EECS050).
- React to changes with `[Changed<T>]` / `[Removed<T>]` on an `ITracked` component, not with dirty flags.
  For "any of these changed" use `[ChangedAny<A, B>]` / `[RemovedAny<A, B>]` (one attribute, not several `Changed`,
  which mean all of them).
- A system that recomputes a tracked component every tick takes it as `Mut<T>` and writes with
  `target.SetIfChanged(value)` (or `Write()` when it knows it writes): a `ref` parameter stamps every visited row.
  Outside a pass, `entity.SetHealthIfChanged(…)`. Keep a plain `ref` where most rows really change. A tracked `ref`
  that some path leaves unwritten (an early `return`, a write in an `if`) is a `Mut<T>` (EECS053). A `Mut<T>` that
  is never written is `in T` (EECS025); one of an untracked component is `ref T` (EECS039). `SetIfChanged` needs a
  record struct, an `IEquatable<T>` or an unmanaged `T` (EECS040).
- Per-client replication: keep one `static readonly QueryFilter` (`QueryFilter.Create().ChangedAny<A, B>(0)`) and run
  it with each client's acknowledged tick, `filter.Since(client.Ack)`; never one filter per client per tick.
- Despawns for replication: set `WorldOptions.DestroyLogCapacity` to at least the destroys of the longest gap between two
  reads of a client, and call `world.DestroyedSince(client.Ack, buffer, out count)`; on `false`, send that client the
  whole state. `[Removed<T>]` does not see destroyed entities, and the log records a retired entity when it is freed.
  A resource to release exactly once is a cleanup
  component, not the log.
- Spread heavy per-row work that may lag over runs with `[ForEach(Slices = N)]`; never on `[Changed<T>]` /
  `[Removed<T>]` methods (EECS035). `system.RunCount % N` is the current slice; `Query`, `Read`, `Count`, `Any`,
  `DestroyAll`, `entity.Run` and bulk calls refuse a sliced filter (EECS044).
- To change structure (add, remove, create, destroy) in a `[ForEach]` method, take `Entity` or `World`
  (EECS013). Changes apply at once; the pass stays safe.
- Order systems with `[UpdateBefore<T>]` / `[UpdateAfter<T>]` inside the same group; no cycles (EECS019, EECS020).
  There is no `Priority`. Groups are `ISystemGroup` structs with `[UpdateInGroup<T>]`; cadence is `UpdateInterval`
  and `UpdatePhase`, constants (EECS018).
- A system never calls `World.Update()` (EECS034): the host runs the world.
- `[ForEach(Parallel = true)]` methods write data only and make no structural changes (EECS010); a later system on the
  owner thread acts on that data. A few hundred heavy rows (microseconds each) set `ChunkRows = 64` so threads take 64
  rows at a time (EECS054: only with `Parallel = true`); light rows keep the default. Threads claim chunks from one
  shared counter, like OpenMP's `schedule(dynamic, n)`: never assume an order between chunks.
- Code that runs on several threads (a parallel `[ForEach]` method, the `Execute` of a job run by `ForEachParallel`)
  never writes shared state: no static field, no object a context parameter or a job field refers to (EECS060). A
  parallel job's fields are not merged back: each chunk runs a copy, so never read a field after `ForEachParallel`
  (EECS061). A total over the rows (a count, a sum, a min / max, a bounding box) is a `ref Reduce<T>` parameter (always `ref`:
  without it, EECS002), with `T` a singleton implementing `IReduction<T>` (`Identity`, an associative `Combine`); read the result as `world.T` or `in T`
  after the pass, never `Reduce<T>.Value` as the total (it is the chunk's part). One `Reduce<T>` per `T` per pass
  (EECS057). Set `ChunkRows` when a float total must be the same on every machine. A list or buffer a row fills
  is a `Scratch<T>` parameter (`var list = scratch.Value ??= new(); list.Clear();`), or `stackalloc` for a small
  fixed bound, never a `new` per row; never store what a scratch holds into a component or singleton (EECS058).
  Read other entities through a `Lookup<T>` parameter (`lookup[id]`, `Read`, `Has`, `TryFind` by key), never a
  `World` parameter in parallel code (EECS010); it sees the values from before the pass, and the same method may not
  write `T` (EECS059): write another component and copy it back later.
  A grid of cells updated from its neighbours is tiles: a component implementing `ITile<TKey>` (static `Width`,
  `Depth`, optional `Height` / `Faces`, a `Key`, `Beside(key, face)`) beside a `DynamicBuffer<TCell>`, shaded by a
  `[ForEach]` method with a `Tile<TCell, TTile>` parameter: `var p = tile.Load(); foreach (var row in
  p.Rows(TileReads.Layer)) { var c = p.At(row); ... c.Load128<T>(dx, dy, dz) ... }`, writing a second buffer that a
  later system commits (never the tile's own buffer, EECS059); never fetch neighbour buffers by hand.
- Hot per-row arithmetic over numbers: a static, non-parallel `[ForEach]` method over components of 1 to 4 fields of
  one primitive type (no `Entity`, `World`, `Mut<T>` or `Maybe<T>` parameter) gets a generated `Vector<T>` loop; write
  the fields (`p.X += v.X`), not a new whole component, and do not mix `float` and `int`. EECS055 (a suggestion) names
  why a method stayed scalar; put `[ForEach(Simd = SimdMode.Require)]` on a hot method so losing the loop fails the
  build (EECS056), `SimdMode.Off` on one that should stay scalar.
- A request or message for one entity runs as a struct job on it: `entity.Run<T…, TJob>(ref job, Filter)` with an
  `IForEachEntity` job and a `static readonly` `QueryFilter`; it returns `false` when the entity is gone, lacks a
  component or fails the filter. It is structural: never inside a parallel `[ForEach]` (EECS010), never in a
  `[ForEach]` method without `Entity` or `World` (EECS013), never off the owner thread (EECS014). A job that makes no
  structural change implements `INoStructuralChanges`; `static int WriteMask` names the components it writes, no more
  and no fewer (EECS037).
- No events or callbacks: a state change is data (a tag, a component, a message entity, a tracked write). There is no
  command buffer and no `Added<T>`: add a tag such as `Spawned` and remove it in the system that reacts.

## Threads and refs

- A world belongs to one owner thread. Systems, queries and structural changes run there; other threads post work to
  it (EECS014). Use one world per thread for more cores, or `ParallelSystems` / parallel `[ForEach]`.
- Never keep a `ref` to a component across an `await` or a structural change; read it again (EECS047). Never call
  `world.Update()` inside a query loop of that world (EECS051).
- `Query` results are ref structs: no `await` inside the loop, no storing items, no LINQ over them.
- A loop that only reads tracked components walks `world.Read<T…>()`, not `Query`: a `Query` item stamps every row
  it visits, so `[Changed<T>]` readers would see rows nobody changed (EECS046).
- During a pass (a `foreach` over `Query`, a `[ForEach]` method, a job's `Execute`) never call `TrimExcess`,
  `Capture`, `Restore`, `Save`, `SaveWithout`, `Load`, `LoadKeeping`, `SaveEntities`, `SaveEntitiesWith`, `SaveEntitiesWithout`, `LoadEntities`,
  `LoadEntitiesInto`, `MoveEntities`, `GroupBy`
  or `RemoveTypes` on its world: they throw (EECS041). Record the request as data and call them after the pass.

## Performance

- An `EECS` warning means the code costs (EECS025 to EECS032, EECS052, and EECS017, EECS018, EECS020), loses a write
  (EECS037, EECS038), stamps rows it only reads or leaves alone (EECS046, EECS053) or uses a ref after a structural change (EECS047): apply
  its fix (one mistake gets one diagnostic: where two rules see the same code, one defers). The suggestions, EECS033 and EECS039, are
  the same cost written shorter. An `EECS` error means the code cannot work.
- Hot paths allocate nothing: no LINQ, closures, boxing, string formatting or `params object[]` in systems (EECS028).
- Keep a `QueryFilter` in a `static readonly` field when a query runs every tick.
- Give a new entity its components in `CreateEntity(a, b, c)` / `Instantiate(prefab, a, b)`, not a `Set` right
  after (EECS052).
- Prefer `[ForEach]` methods over hand-written `Query` loops; use bulk calls (`SetAll`, `RemoveAll`, `DisableAll`,
  `EnableAll`, `CreateEntities`, `DestroyAll`, `DestroyEntities(ids)`; `CollectIds<T>(span, filter)` for a bounded
  batch of ids, the first N requests of a tick) for many entities at once, not a loop over a query or a `[ForEach]` method whose whole body is that one change
  (EECS030), and
  `CreateEntities` or `Reserve` before creating many entities in a loop (EECS031); `world.Reserve(prefab, n)` also
  reserves the prefab's index keys, its retired table (cleanup components) and free ids, so a wave of a kind spawns at 0 B. Ask `world.Any<T>()` and
  `world.Count<T>()`, and filter with `QueryFilter`, instead of counting or testing in a loop (EECS032).
- Removing a component keeps the row (one bit flips), so status components that come and go are cheap; adding a new
  kind of component moves the row once.
- Use `Prefab` + `world.Instantiate` for repeated spawns, `GroupBy<T>()` to split tables by zone or team, and value
  indexes for lookups by key. Give an instance its own values in the same call, `world.Instantiate(prefab, new
  ChildOf(parent), new Position(x, y))`, not `Instantiate` then `Set` (two row copies).
- Call `world.TrimExcess()` only at a quiet moment after a peak (a wave cleared, a match ended), never every tick:
  what it drops is allocated again by the next structural changes (EECS028 in `OnUpdate` and `[ForEach]` methods).

## What EasyEcs does not do

- It does not run on Unity or runtimes older than .NET 10 (Godot 4 .NET projects that target `net10.0` do run it).
- It has no whole-entity disable flag: add a tag and filter with `Without<T>`; `entity.Disable<T>()` hides one
  component and keeps its value.
- It has no actor type. An actor is a pattern: one type per kind of actor that is its key component
  (`IIndexed<TSelf>`), its system group and the group's filter (`ISystemGroupWith<In<TSelf>>`), declared once through a
  generic interface (`interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, ISystemGroupWith<In<TSelf>>`).
  Implement `Key` explicitly on a record struct (`Shop IIndexed<Shop>.Key => this;`): a public `Key => this` makes
  the record's `ToString()` recurse until the stack overflows (EECS043).
  https://github.com/JasonXuDeveloper/EasyEcs/tree/master/samples/Actors
- It has no shared components: put shared data in a singleton and store an id.

## Persistence

- `world.Capture()` / `Restore(snapshot)` copy a world in memory (undo, rollback, tests); `world.Save` / `Load` write
  bytes that survive a restart or go to another process.
- Collect a subtree with `world.Descendants(root).AddTo(ids, includeRoot: true)` (not only `Children`) before
  `SaveEntities` or `MoveEntities`, and pass `CollectionsMarshal.AsSpan(ids)`: a child left behind dies with the original.
- Size a buffer from `index.Find(key).Count` (O(1)), never by walking the matches twice; `index.KeyCount` /
  `relations.TargetCount` count distinct keys / targets in O(1).
- Durable state is a record: `SaveEntitiesWith(writer, ids, Persistent)` with a `static readonly ComponentSet` of the
  durable components only, never the runtime ones (connections, timers, caches); when the runtime set is the smaller
  one, `SaveEntitiesWithout(writer, ids, Runtime)` writes everything else. Neither set may name a singleton (it throws).
- Restore a record with `LoadEntitiesInto(bytes, entities, markWritten: false)`, so a system that saves or
  replicates changed components does not send it straight back; the values it replaces (a prefab's defaults) count as
  never written too, so one prefab per kind serves new and restored entities. `LoadEntitiesInto` is structural (EECS010, EECS013, EECS014).
- Resume a saved world after a restart with `world.Load(bytes, markWritten: false)`: no loaded row counts as written
  or removed, also for systems added after it. A system added to a world that already holds its state starts with
  `world.AddSystem<T>(changesFromNow: true)`.
- A restart save leaves runtime state out: `world.SaveWithout(writer, Runtime)` with a `static readonly ComponentSet` of the
  runtime components and singletons (`.With<Session>().WithSingleton<Config>()`). The new process sets its
  configuration first, then `world.LoadKeeping(bytes, ComponentSet.Create().WithSingleton<Config>(), markWritten: false)`,
  which keeps it. A `keep` set holds singletons only; a `SaveEntitiesWith` set holds components only.

## Project layout and debugging

- Lay a project out like `dotnet new easyecs` (package `EasyEcs.Templates`): data in `Components/`, one system per
  file in `Systems/`, and `Program.cs` only as the host: it builds the world, sets every singleton before the first
  update and adds the systems in one method the tests share. Input from other threads goes through a queue held in a
  singleton. https://ecs.xgamedev.net/en/docs/guide/project-structure
- To look inside a world: `world.DescribeTables()` (tables, rows, bytes), `entity.Describe()`, `system.Stats` with
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
