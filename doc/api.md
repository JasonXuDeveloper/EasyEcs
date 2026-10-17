# API reference and complexity

[Docs index](README.md) · Everything is in `namespace EasyEcs`. T1..T9 overloads are generated from one template
each (see [Architecture](architecture.md#source-generators-and-analyzer-easyecsgenerator)).

Notation:

- *n*: components in the call (1-9).
- *c*: components of the entity.
- *M*: archetypes matching a query.
- *S*: rows of those archetypes (alive entities; plus tombstones while a pass that makes structural changes runs).
- "Amortized": tables and the entity array occasionally double.

## Types

| Type | Kind | Purpose |
|---|---|---|
| `World` | sealed class, `IDisposable` | Owns entities, components, singletons, systems. |
| `World.EntityEnumerator` | struct | Result of `world.Entities`: enumerates alive entities in id order, allocation-free. |
| `WorldOptions` | sealed class | `ParallelSystems`, `MaxDegreeOfParallelism`, `InitialEntityCapacity`, `MeasureSystems` (init-only). |
| `Entity` | readonly struct | Handle: `Id`, `Version`, `World`; equality includes the version. A destroyed or default handle answers questions (`IsAlive`, `Has`, `TryGet`, `TryGetRef`) with false, throws on accesses and changes (`Get`, `Read`, `Add`, `Set`, `Remove`), and ignores `Destroy`. |
| `IComponent` | marker interface | Implemented by component structs. |
| `ITrackChanges` | marker interface | A component whose rows record the change tick of their last write (`[Changed<T>]`, `QueryFilter.Changed<T>(since)`). |
| `IIndexed<TKey>` | interface | A component (`ITrackChanges`) the world indexes by its `Key`; an `Entity` key makes it a relation. |
| `ValueIndex<T, TKey>` | sealed class | `world.Index<T, TKey>()`: `Find(key)` (enumerator of `Entity`), `TryFind`, `Count`. |
| `DynamicBuffer<T>` | struct, `IComponent, ITrackChanges` | A list per entity: `Count`, `Capacity`, `this[i]` (read-only), `AsReadOnlySpan()`, `foreach`; writes through `DynamicBufferExtensions` (`ref this`): `Add`, `AddRange`, `Insert`, `RemoveAt`, `RemoveAtSwapBack`, `Clear`, `EnsureCapacity`, `TrimExcess`, `AsSpan()`. |
| `ISingletonComponent` | marker interface | Implemented by singleton structs. |
| `EcsSystem` | abstract class | `World`, `Priority`, `UpdateInterval`, `Enabled`, `Stats`, `LastRunTick`; `OnCreate` / `OnUpdate` / `OnDestroy`. |
| `SystemStats` | readonly struct | `Runs`, `Last`, `Total`, `Average` of a system's runs; recorded with `WorldOptions.MeasureSystems`. |
| `SystemAccess` | struct | What a system reads and writes (`Read<T>`, `Write<T>`, `ReadSingleton<T>`, `WriteSingleton<T>`, `RunsAlone`), used by `ParallelSystems` batching. Only the generated `GeneratedAccess` override of a system fills it (from its `[ForEach]` parameters); user code never calls it. Each call O(1). |
| `ForEachAttribute`, `WithAttribute<T>`, `WithoutAttribute<T>`, `WithAnyAttribute<T>`, `ChangedAttribute<T>` | attributes | `[ForEach]` methods (`Parallel = true` runs chunks of rows on several threads) and their filters. |
| `ISystemGroup`, `UpdateInGroupAttribute<TGroup>` | marker / attribute | System groups. |
| `QueryFilter` | readonly struct | `Create().With<T>().Without<U>().WithAny<V>().WithAny<W>().Changed<X>(since)`; `ChangedSince(since)` moves the tick of a kept filter. |
| `IForEach<T1..Tn>` | interface | Struct job: `void Execute(ref T1 c1, …)`. Optional static `Writes` (bit k - 1 for component k: what to carry over when the job moves its entity, and which tracked components it stamps; default all). |
| `IForEachEntity<T1..Tn>` | interface | Struct job that also receives the handle: `void Execute(Entity entity, ref T1 c1, …)` (for the id, `entity.Id`). Optional static `Writes`, as for `IForEach`. |
| `IForEachBatch<T1..Tn>` | interface | Struct job receiving runs of rows: `void Execute(Span<T1> c1, …)`; no structural changes. Optional static `Writes` (which spans the job writes; default all). |
| `INoStructuralChanges` | marker interface | A job that makes no structural change: its passes skip the bookkeeping (generated for `[ForEach]` methods that cannot change structure). |
| `QueryEnumerator<T1..Tn>`, `QueryItem<T1..Tn>` | ref structs | Result of `Query`: `item.Entity`, `item.Component1..n` (refs into the row); enumerator `Dispose()` (ends the pass; `foreach` calls it). A loop over them cannot `await`. |
| `SingletonRef<T>` | readonly struct | Stable singleton handle; `.Value` is `ref T`. |
| `SystemList` | readonly struct | `world.Systems`: `foreach` over every group's systems, `Count`; allocation-free. |
| `WorldSnapshot` | sealed class | Copies of a world's entities, components and singletons (`world.Capture()`); `EntityCount`. |

## Entities

| API | Complexity | Notes |
|---|---|---|
| `new World(options)` | O(`InitialEntityCapacity`) | `options` may be null (defaults); allocates the entity array and the empty table |
| `world.CreateEntity()` | O(1) amortized | reuses recycled ids |
| `world.CreateEntity(in T1 … in Tn)` | O(n) amortized | placed directly in its final table |
| `world.CreateEntities(Span<Entity> entities, in T1 … in Tn)` | O(count × n) amortized | one gate pass and at most one table growth for the batch; writes the handles |
| `entity.Destroy()` / `world.DestroyEntity(entity)` | O(c) | the table's last row fills the hole (a tombstone during a pass); recycles the id; stale handles are ignored; once a world has an index, each indexed component unlinks the entity (O(1)) |
| `world.DestroyEntities<T1..Tn>(filter)` → `int` | O(M + S) | destroys every match in one gate pass and returns how many; safe during a pass |
| `entity.IsAlive` | O(1) | |
| `world.TryGetEntity(id, out e)` / `GetEntity(id)` | O(1) | `GetEntity` throws if no entity is alive with that id |
| `world.EntityCount` | O(1) | |
| `world.Entities` | O(capacity) | diagnostics |
| `world.EnsureEntityCapacity(capacity)` | O(capacity) | pre-size the entity array to avoid growth at runtime; no-op when already large enough |

## Components

| API | Complexity | Notes |
|---|---|---|
| `entity.Get<T>()` → `ref T` | O(1) | version check + column lookup (+ one presence-bit test) + one indexed load; throws if missing or not alive; counts as a write of a tracked (`ITrackChanges`) component |
| `entity.Read<T>()` → `ref readonly T` | O(1) | as `Get`, but read-only: never counts as a write |
| `entity.TryGetRef<T>(out bool found)` → `ref T` | O(1) | a null ref when missing or not alive (check `found`); counts as a write of a tracked component when found |
| `entity.Has<T>()` / `TryGet<T>(out T)` | O(1) | false when missing or not alive; `TryGet` returns a copy (not a write) |
| `entity.Has<T1..Tn>()` | O(n) | all of them; one meta lookup |
| `entity.Add<T>()` → `ref T` | O(c) amortized | adds (or resets) and returns a ref; a component the table has a column for only sets its presence bit (O(1)), a new one copies the row |
| `entity.GetOrAdd<T>()` → `ref T` | O(1), or as `Add` when missing | counts as a write of a tracked component |
| `entity.Set(in T1 … in Tn)` → `Entity` | O(n), or O(c) amortized when it moves | add or overwrite, at most one row copy, chainable |
| `entity.Remove<T1..Tn>()` → `Entity` | O(n) | clears presence bits, the entity keeps its row; missing components are ignored; a removed indexed component unlinks the entity (O(1)); chainable |

## Dynamic buffers

`DynamicBuffer<T>` is a component (`IComponent, ITrackChanges`); `new DynamicBuffer<T>()` holds no list until its
first write. Reads work on any reference (`in`, `Read<T>()`); writes need a `ref` (a tracked write)
([Dynamic buffers](guide/dynamic-buffers.md)).

| API | Complexity | Notes |
|---|---|---|
| `bag.Count`, `bag.Capacity`, `bag[i]` (`ref readonly`), `AsReadOnlySpan()`, `foreach` | O(1) | read-only; out-of-range indexes throw |
| `bag.Add(item)` / `AddRange(span)` | O(1) amortized / O(items) | allocates only when the list grows (doubling, at least 4) |
| `bag.Insert(i, item)` / `RemoveAt(i)` | O(count) | keep the order |
| `bag.RemoveAtSwapBack(i)` | O(1) | the last element moves into `i` |
| `bag.Clear()` | O(1), O(count) for elements with references | keeps the capacity |
| `bag.EnsureCapacity(n)` / `AsSpan()` | O(count) when it grows / O(1) | `AsSpan()` is writable |
| `bag.TrimExcess()` | O(count) when the capacity exceeds the count | reallocates to the count; an empty list holds no array |
| `world.Capture()` / `Restore` of a buffer column | O(elements) | copies the elements (a new list per entity) |

## Queries

| API | Complexity | Notes |
|---|---|---|
| `[ForEach]` method / `world.ForEach<T1..Tn, TJob>(ref job, filter)` | O(M + S) | fastest: struct job, no delegate |
| `world.ForEachEntity<T1..Tn, TJob>(ref job, filter)` | O(M + S) | like `ForEach`, the job receives the `Entity` |
| `world.ForEachParallel<T1..Tn, TJob>(ref job, filter)` / `ForEachEntityParallel` | O(M + S), divided over up to `MaxDegreeOfParallelism` threads | `INoStructuralChanges` jobs; chunks of `World.ChunkRows` rows, one job copy per chunk (state is not merged); fewer than two chunks run on the calling thread |
| `world.ForEachBatch<T1..Tn, TJob>(ref job, filter)` | O(M + S / 64 + runs) | spans of consecutive rows; one call for a table whose rows all match |
| `foreach (var item in world.Query<T1..Tn>(filter))` | O(M + S) | `item.Entity`, `item.Component1..n` (refs) |
| `world.Count<T1..Tn>(filter)` | O(M), + S / 64 for tables whose rows must be tested (some rows lack a required component, have an excluded one or lack every `WithAny` component, or a `Changed` filter applies) | sums table counts, or counts presence bits (and change ticks) |
| `world.Any<T1..Tn>(filter)` | O(M + S / 64) worst | stops at the first match; S / 64 only for tables whose rows must be tested (as for `Count`) |
| `QueryFilter.Create().With<T>().Without<U>().WithAny<V>()` | O(1); allocates once a component's bit is 256 or more (worlds with more than 256 component types) | build once and keep it in a `static readonly` field; `WithAny` calls form one "at least one of" group |
| `filter.Changed<T>(since)` / `filter.ChangedSince(since)` | O(1); a pass skips tables, 4,096-row blocks and 64-row words written before `since` (4 summaries per SIMD compare) and takes a word's written rows from its summary; only words written at several ticks after `since`, or row by row by a query, test 4 rows per SIMD compare | rows whose `T` (`ITrackChanges`) was written at or after `since`; one tick per filter (`Changed` filters naming different ticks throw `ArgumentException`); `ChangedSince` returns a copy with a new tick, so a kept filter takes a new tick per call without being rebuilt |

The first time a query shape is used, building its cache entry costs O(all archetypes) under the structural gate.
After that, each lookup is one hash probe. In turn, creating a new archetype (the first entity with a new component
set) costs O(archetypes + cached query shapes + Σ over the matching cached queries of their archetype count), since
it is appended (copy-on-write) to the archetype list and to every cached query it matches. Both are cold paths.

## Indexes

| API | Complexity | Notes |
|---|---|---|
| `world.Index<T, TKey>()` → `ValueIndex<T, TKey>` | O(1); the first call creates it | one per world and indexed component (`T : IIndexed<TKey>`, `TKey : IEquatable<TKey>`); asking for a second key type of the same component throws |
| `index.Count(key)` / `TryFind(key, out Entity)` | O(1) (a hash probe; an array access for relations), + catching up with the w rows written since the previous lookup: O(tables) when w = 0, else O(W / 4096 + written 64-row words + w) for W rows of `T` | allocation-free after warm-up; destroyed entities and removed components leave the index at once ([indexes and relations](guide/indexes-and-relations.md)) |
| `index.Find(key)` → `ValueIndex<T, TKey>.Matches` | as `Count`, + O(1) per entity of the key | a `ref struct` for `foreach` (`Current` is an `Entity`); destroying an entity during the loop is allowed, changing `T` is not |

## Systems, singletons, maintenance

| API | Complexity |
|---|---|
| `world.AddSystem<T>()` / `AddSystem(system)` (calls `OnCreate`) | O(systems + b²), b = systems of the same priority (batch rebuild); cold path; throws `InvalidOperationException` if the system already belongs to a world |
| `world.RemoveSystem<T>()` (calls `OnDestroy`) | O(systems + b²), b = systems of the same priority (batch rebuild); cold path |
| `world.GetSystem<T>()` | O(systems) |
| `world.Update()` (default group) / `world.Update<TGroup>()` | Σ systems of the group; allocation-free; throws `ObjectDisposedException` after `Dispose()` |
| `world.ChangeTick` | O(1); grows by one per system batch, once after each update and once per index lookup that catches up with written rows outside a pass ([change tracking](guide/change-tracking.md)) |
| `world.AddSingleton<T>()` / `GetSingleton<T>()` / `GetOrAddSingleton<T>()` → `ref T` | O(1); `AddSingleton` resets an existing singleton in its storage, so `SingletonRef<T>` handles stay attached |
| `world.SingletonRef<T>()` → `SingletonRef<T>` (stable handle; adds if missing) / `singletonRef.Value` → `ref T` | O(1) / O(1), one load |
| `world.TryGetSingleton<T>(out T)` / `HasSingleton<T>()` / `RemoveSingleton<T>()` → `bool` | O(1); `RemoveSingleton` returns false when it was not added |
| `world.GetFragmentationStats()` → `(TotalSlots, AliveEntities, FragmentationRatio)` | O(archetypes); a diagnostic: tables stay dense on their own (passes end even when a job throws; an update closes the pass of a query enumerator abandoned without `Dispose`), so the ratio is 0 between updates |
| `world.Capture()` → `WorldSnapshot` | O(entities × components + archetypes + singletons); copies by value, not while a query iterates |
| `world.Restore(snapshot)` | O(entities × components + archetypes + singletons); snapshot handles are valid again, later ones stale; every tracked row counts as written; indexes re-index on their next lookup; not while a query iterates |

Other `World` members:

| Member | Meaning |
|---|---|
| `world.Systems` → `SystemList` | the systems of every group in execution order; `foreach` allocation-free, O(systems); `Count` O(groups) |
| `world.UpdateCount` | number of default-group updates so far; O(1) |
| `world.SystemError` | `event Action<EcsSystem, Exception>`; without a subscriber, exceptions propagate; a failed run still moves `LastRunTick` on; raised O(1) per failure |
| `world.Dispose()` | `OnDestroy` on every system in reverse order, then releases storage; stale handles throw; indexes still held find nothing; O(systems + entities + archetypes) |
| `World.ChunkRows` | rows per chunk of a parallel `[ForEach]` (16,384, a multiple of 64) |

`RemoveSystem<T>()` returns whether a system was removed. `GetSingleton<T>()` throws if the singleton was not added.

Values and handles, all O(1): `entity.Id` / `Version` / `World`, `==` / `Equals` / `GetHashCode` (id and version) /
`ToString`; `snapshot.EntityCount`; `system.Stats.Runs` / `Last` / `Total` / `Average`;
`system.LastRunTick`, `Priority`, `UpdateInterval`, `Enabled`, `World`.
