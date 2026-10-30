---
name: easyecs
description: "Write correct, fast EasyEcs code (the .NET 10 ECS on NuGet as EasyEcs) - components and singletons as data, stateless [EcsSystem] ref structs with [ForEach] methods, generated members, filters, change tracking, parallel passes, Reduce/Emit/Scratch/Lookup parameters, vector loops, saves - and avoid what the EECS analyzer reports. Use when writing, reviewing or migrating code that uses EasyEcs. Triggers: a .cs file or project referencing EasyEcs, [EcsSystem], [ForEach], IComponent, World, Entity, an EECS diagnostic, ECS architecture questions for .NET, 用 EasyEcs 写组件、系统、查询."
---

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
  `EntityId` keys (EECS036). A component has one index key: never two `IIndexed<TKey>` (EECS036). A key that is a
  member is declared, not written: `[LookupKey]` (or `[UniqueKey]`) on the field or positional parameter of a partial
  struct (`public partial record struct Account([UniqueKey] int Id) : IComponent;`; `[LookupKey]` on the type when the
  value itself is the key), and the generator writes the interface and an explicit `Key` (EECS067). `[Group]` on an
  `int` or enum member declares a table group the same way (a hand-written `Group` returning one: EECS067), and `[GroupBy]` in its place also makes every world group by it
  (no `world.GroupBy<T>()` per world). A key computed from several members stays a hand-written `Key`. When the keys a
  world holds at once map onto bounded slots (grid cells modulo the loaded area, small ids), make it
  `IDirectMapped<TKey>` (`static Slots`, `static SlotOf(key)`, pure arithmetic) instead of keeping a grid of ids by
  hand; never for sparse keys or huge ranges.
- A list per entity is a `DynamicBuffer<T>` component; never copy it by value (EECS012). Call
  `EnsureCapacity(n)` on it (and `world.Reserve<T…>(n)` on the world) before a wave of known size. Write a whole
  buffer through the span `SetCount(n)` returns; compute many passes over the same elements in a `stackalloc`'d span
  and copy it in once. `TrimExcess()` on an empty scratch buffer gives its list back to the pool. Buffers longer than
  4 KB that come and go (a chunk's blocks, a path) set `DynamicBuffer<T>.MaxPooledLength` to their length and call
  `DynamicBuffer<T>.FillPool(peak)` once at startup, after `MaxPooledLength` (both process-wide); `world.Reserve` does not
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

- A system is an `[EcsSystem] public ref struct` of static members only, never instantiated (not a ref struct:
  EECS074; an instance member: EECS075). Code a system once ran per update goes into hooks: `OnCreate`,
  `OnUpdate` and `OnDestroy` static methods (the name makes the hook, no attribute) taking the `World`, the `SystemHandle`, both or neither (another
  signature: EECS076). An `OnUpdate` method runs before the system's `[ForEach]` methods and makes it run alone.
  A system holds no state: no mutable static field (EECS007), no reach into another system or `world.System<T>()` from
  system code (EECS008). State goes into components or singletons; what the host hands in (a channel, a queue) is a
  `[NotSaved]` singleton the host sets before `AddSystem<T>()`. The system APIs (`AddSystem<T>`, `[UpdateAfter<T>]`,
  ...) take `[EcsSystem]` types only (EECS077).
- One system per responsibility. Per-entity logic is a `static` `[ForEach]` method (EECS005):
  `[ForEach, Without<Dead>] static void Move(ref Position p, in Velocity v) => ...`.
  Take components by `ref` to write and `in` to read (EECS002); a `ref` that only reads is EECS025. Do not change
  what an `in` parameter holds (EECS011). Take only what the method uses: a component it only requires is a
  `[With<T>]` filter (EECS026), and a component or singleton it reads is a parameter, not a lookup through the
  `Entity` or `World` on every row (EECS027; an indexed component too: `in` or
  `ReadOnlyMaybe<T>`).
  A `[ForEach]` method takes 1 to 9 components (or none with a `[With<T>]` / `[Changed<T>]`, EECS003), is not generic
  (EECS004) nor a local function (EECS024), and its types are visible to the generated code (EECS022); a batch method
  takes spans, singletons and an `EntitySpan` only (EECS009).
- Filters on a `[ForEach]` method: `[With<T>]`, `[Without<T>]`, `[WithAny<T1, T2>]`, `[Changed<T>]`, `[Removed<T>]`.
  A `[Removed<T>]` method does not take `T` (EECS016). On a system group struct, for all its systems: `[With<T>]`,
  `[Without<T>]`, `[WithAny<T1, T2>]` (on any other struct: EECS023), or `ISystemGroupWith<T>` / `ISystemGroupWithout<T>` implemented
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
- Event and command entities handled once: `[ForEach, Consume]` destroys exactly the rows the pass visited when it
  ends (in bulk when the method changes no structure), not `entity.Destroy()` at the end of the body (EECS084); not
  with `Slices` (EECS085). Its system runs alone, as any system that destroys entities.
- Order systems with `[UpdateBefore<T>]` / `[UpdateAfter<T>]` inside the same group; no cycles (EECS019, EECS020).
  There is no `Priority`. Groups are `ISystemGroup` structs with `[UpdateInGroup<T>]`; cadence is
  `[UpdateInterval(n, Phase = p)]` on the system (EECS083: n >= 1, 0 <= p < n).
- A system never calls `World.Update()` (EECS034): the host runs the world.
- `[ForEach(Parallel = true)]` methods write data only and make no structural changes (EECS010); a later system on the
  owner thread acts on that data. A few hundred heavy rows (microseconds each) set `ChunkSize = 64` so threads take 64
  rows at a time (EECS054: only with `Parallel = true`); light rows keep the default. Threads claim chunks from one
  shared counter, like OpenMP's `schedule(dynamic, n)`: never assume an order between chunks.
- Code that runs on several threads (a parallel `[ForEach]` method, the `Execute` of a job run by `ForEachParallel`,
  `ForEachEntityParallel`, `ForEachBatchParallel` or `ParallelFor`) never writes shared state: no static field, no
  object a context parameter or a job field refers to (EECS060). A parallel job's fields are not merged back unless
  the job implements `Merges` itself: each chunk runs a copy, so never read a field after the parallel call; run it
  with `ForEach` / `ForEachEntity` / `ForEachBatch` instead (EECS061). A total over the rows (a count, a sum, a min / max, a bounding box) is a `ref Reduce<T>` parameter (always `ref`:
  without it, EECS002), with `T` a singleton implementing `IReduction<T>`: declare it with field attributes on a
  partial struct (`[Sum] public int Alive; [Min] public float Low;`, also `[Product]`, `[Max]`, `[All]`, `[Any]`), and
  the generator writes `Identity` and `Combine`. Every field gets one attribute (EECS064), the struct and its outer
  types are partial (EECS062), and it writes no `Identity` / `Combine` of its own (EECS063). Write `IReduction<T>` by
  hand only for another combination (EECS065 suggests the attributes), with an `Identity` that changes nothing
  (EECS066). Read the result as `world.T` or `in T`
  after the pass, never `Reduce<T>.Value` as the total (it is the chunk's part). One `Reduce<T>` per `T` per pass
  (EECS057). A singleton that a system resets each update and a `[ForEach]` method then accumulates through `ref`
  (`s.Count++`, `s.Max = Math.Max(s.Max, x)`) is that reduction written by hand (EECS086): take `ref Reduce<T>` and
  drop the reset. A total that grows across updates (a score) stays a `ref T`. Set `ChunkSize` when a float total must be the same on every machine. A list or buffer a row fills
  is a `Scratch<T>` parameter (`var list = scratch.Value ??= new(); list.Clear();`), or `stackalloc` for a small
  fixed bound, never a `new` per row; never store what a scratch holds into a component or singleton (EECS058).
  Read other entities through a `Lookup<T>` parameter (`lookup[id]`, `Read`, `Has`, `TryFind` by key), never a
  `World` parameter in parallel code (EECS010), and never a `World` taken only for `TryGetEntity` and reads (EECS087:
  it keeps the system alone in its stage); it sees the values from before the pass, and the same method may not
  write `T` (EECS059): write another component and copy it back later.
  Records a pass produces (hits, events, messages) go through an `Emit<T>` parameter (`hits.Add(new Hit(...))`), joined
  in row order into singleton `Emitted<T>` when the pass ends, never a `List<T>` in a `ref` singleton (EECS089). A
  later system reads `world.GetSingleton<Emitted<T>>().Items` and clears it; the emitting method never takes `Emitted<T>` and emits
  one kind of record through one parameter (EECS070).
  Random numbers in a `[ForEach]` method come from `in EntityRandom random` (`random.Range(entity.Id, stream, min,
  max)`, a pure function of seed, entity, tick and stream), never `Random.Shared`, a `new Random` or a `Random` field
  (EECS071). Each draw for the same entity in a pass has a stream of its own, and a draw in a loop adds the iteration
  to its stream (`stream + (uint)i`, EECS088); every `World.Update()` advances `EntityRandom.Tick`, no code needed.
  A grid of cells updated from its neighbors is tiles: a component implementing `ITile<TKey>` (static `Width`,
  `Depth`, optional `Height` / `Faces` / `Halo` / `Neighborhood` / `Boundary`, a `Key`, `Beside(key, face)`, which
  wraps on a periodic grid) beside a `DynamicBuffer<TCell>`, shaded by a
  `[ForEach]` method with a `Tile<TCell, TTile>` parameter: `tile.ExchangeHalo(); foreach (var row in
  tile.Rows(TileStencil.Layer)) { var c = tile.At(row); ... c.Load128<T>(dx, dy, dz) ... }`, writing a second buffer that a
  later system commits (never the tile's own buffer, EECS059); never fetch neighbor buffers by hand. A stencil
  reaching r cells needs `Halo => r` (at most the tile, EECS072); a 9- or 27-point one needs
  `Neighborhood => TileNeighborhood.All`; one step per pass (several steps per load lose on a cached grid);
  quiet tiles sleep through `tile.ChangedEdges` / `tile.Facing` and an awake tag. One cell type per field, one
  `Tile` per field (two of the same cell type is EECS073).
- Hot per-row arithmetic over numbers: a static per-entity `[ForEach]` method (on one thread or `Parallel`; no
  `Entity`, `World`, span, `Mut<T>`, `Scratch<T>`, `Lookup<T>`, `Emit<T>`, `Tile` or `ref` value parameter) gets a generated vector loop when its components hold 1 to 4 lanes of
  one size: 4-byte `float` / `int` / `uint`, 8-byte `double` / `long` / `ulong`, or narrow `byte` / `sbyte` / `short`
  / `ushort` lanes, whose `int` arithmetic must fit the lanes (`(byte)Math.Min(a.X + b.X, 255)` is a saturating add;
  a value the lanes cannot hold stays scalar). Struct values are one vector per lane: `Vector2` / `Vector3` /
  `Vector4` fields, `p = new Position(...)`, `p with { ... }`; a combining call (`Dot`, `Length`) stays scalar. On
  AVX-512 a wider component (up to 16 words) qualifies when the method uses only its 4-byte fields. A `Maybe<T>` of a
  lane component is a mask of the rows that have it (on one thread only). An `if` becomes a mask; a branch the loop
  cannot express (a call, a loop, logging) is cold, and the vectors where a lane takes it run the method. Do not mix
  lane sizes (`float` with `double`, `int` with `byte`; `float` with `int` is fine), and keep `bool` fields out of hot
  components. A `Reduce<T>` keeps lane accumulators when each field is declared `[Sum]` / `[Min]` / `[Max]` (or a
  hand-written `Combine` sums, `Min`s or `Max`es each field); `[Product]` / `[All]` / `[Any]` stay scalar (EECS055
  names the field). Results are bit for bit the method's
  (`FloatMode.Exact`, the default); `[ForEach(FloatMode = FloatMode.Fast)]` lets `MathF.Sin` / `Cos` / `Exp` / `Log`
  / `Log2` / `Hypot` and a float `Reduce<T>` sum vectorize, with results that are not bit for bit the method's (nor the
  same across vector widths): keep `Exact` where results must match on every machine. EECS055 (a suggestion) names
  why a method stayed scalar (a narrow value out of range, a component too wide for the work, a `Maybe<T>` in a
  parallel method, a call outside a condition); put `[ForEach(SimdMode = SimdMode.Require)]` on a hot method so losing the loop fails the
  build (EECS056), `SimdMode.Off` on one that should stay scalar.
- A request or message for one entity is a private static `[EntityMethod]` method (`ref` for what it writes, filter
  attributes such as `[Without<Dead>]`, the caller's values after the components), called through its generated entry
  point `Owner.Method(entity, values…)`; a job written by hand and run with `entity.Run` is EECS080, an instance
  method or one taking spans EECS081. The entry point returns `false` when the entity is gone, lacks a component or
  fails the filter. A hand-written `entity.Run<T…, TJob>(ref job, Filter)` takes an `IForEachEntity` job and a
  `static readonly` `QueryFilter`. Both are structural: never inside a parallel `[ForEach]` (EECS010), never in a
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

- An `EECS` warning means the code costs (EECS025 to EECS032, EECS052, EECS087, and EECS017, EECS020), loses a write
  (EECS037, EECS038), stamps rows it only reads or leaves alone (EECS046, EECS053), uses a ref after a structural
  change (EECS047) or repeats a random stream (EECS088): apply
  its fix (one mistake gets one diagnostic: where two rules see the same code, one defers). The suggestions, EECS033 and EECS039, are
  the same cost written shorter; EECS086 and EECS089 name the parameter that lets a method run per chunk. An `EECS` error means the code cannot work.
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
- Use `Prefab` + `world.Instantiate` for repeated spawns, `[GroupBy]` (or `world.GroupBy<T>()`) to split tables by zone or team, and value
  indexes for lookups by key. Give an instance its own values in the same call, `world.Instantiate(prefab, new
  ChildOf(parent), new Position(x, y))`, not `Instantiate` then `Set` (two row copies). A component instances gain
  later (a `Stunned`, a `Target`) goes into the recipe as `prefab.SetAbsent<T>()`: its column is there, so adding it
  flips a presence bit instead of moving the row. `entity.GetOrAddX(…)` (and `world.GetOrAddX(…)` for a singleton) adds a value only when it is
  missing.
- Call `world.TrimExcess()` only at a quiet moment after a peak (a wave cleared, a match ended), never every tick:
  what it drops is allocated again by the next structural changes (EECS028 in `OnUpdate` and `[ForEach]` methods).

## What EasyEcs does not do

- It does not run on Unity or runtimes older than .NET 10 (Godot 4 .NET projects that target `net10.0` do run it).
- It has no whole-entity disable flag: add a tag and filter with `Without<T>`; `entity.Disable<T>()` hides one
  component and keeps its value.
- It has no actor type. An actor is a pattern: one type per kind of actor that is its key component
  (`IIndexed<TSelf>`), its system group and the group's filter (`ISystemGroupWith<In<TSelf>>`), declared once through a
  generic interface (`interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, ISystemGroupWith<In<TSelf>>`).
  Declare the key with `[LookupKey]` on the type (`[LookupKey] public readonly partial record struct Shop(long Id) :
  IActor<Shop>;`): the generated `Key` is explicit, while a public `Key => this` makes the record's `ToString()` recurse
  until the stack overflows (EECS043).
  https://github.com/JasonXuDeveloper/EasyEcs/tree/master/samples/Actors
- It has no shared components: put shared data in a singleton and store an id.

## Persistence

- `world.Capture()` / `Restore(snapshot)` copy a world in memory (undo, rollback, tests); `world.Save` / `Load` write
  bytes that survive a restart or go to another process. A component or singleton holding references (a `string`, an
  array, a class) is `[NinoType]` and partial so a save can write it, or `[NotSaved]` when it is runtime state a
  restart rebuilds (EECS069); `[NotSaved]`, `[GroupBy]` and `[WhenTargetDestroyed]` go only on types they fit (EECS068).
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
  Each system's `Loops` names the loop each `[ForEach]` method got (a vector loop, or why it runs row by row).

## Example

```csharp
using EasyEcs;

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public struct Dead : IComponent;

[EcsSystem]
public ref struct MovementSystem
{
    [ForEach, Without<Dead>]
    static void Move(ref Position p, in Velocity v) => p = new Position(p.X + v.X, p.Y + v.Y);
}

[EcsSystem, UpdateAfter<MovementSystem>]
public ref struct DeathSystem
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
