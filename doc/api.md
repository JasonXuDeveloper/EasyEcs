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
| `WorldOptions` | sealed class | `Parallel`, `MaxDegreeOfParallelism`, `InitialEntityCapacity`, `MeasureSystems` (init-only). |
| `Entity` | readonly struct | Handle: `Id`, `Version`, `World`; equality includes the version. |
| `IComponent` | marker interface | Implemented by component structs. |
| `ISingletonComponent` | marker interface | Implemented by singleton structs. |
| `EcsSystem` | abstract class | `World`, `Priority`, `UpdateInterval`, `Enabled`, `Stats`; `OnCreate` / `OnUpdate` / `OnDestroy`. |
| `SystemStats` | readonly struct | `Runs`, `Last`, `Total`, `Average` of a system's runs; recorded with `WorldOptions.MeasureSystems`. |
| `SystemAccess` | struct | What a system reads and writes (`Read<T>`, `Write<T>`, `ReadSingleton<T>`, `WriteSingleton<T>`, `RunsAlone`); generated from `[ForEach]` parameters, used by `Parallel` batching. |
| `ForEachAttribute`, `WithAttribute<T>`, `WithoutAttribute<T>`, `WithAnyAttribute<T>` | attributes | `[ForEach]` methods and their filters. |
| `ISystemGroup`, `UpdateInGroupAttribute<TGroup>` | marker / attribute | System groups. |
| `QueryFilter` | readonly struct | `Create().With<T>().Without<U>().WithAny<V>().WithAny<W>()`. |
| `IForEach<T1..Tn>` | interface | Struct job: `void Execute(int entityId, ref T1 c1, …)`. Optional static overrides: `ReadsId` (false skips loading ids, `entityId` is 0) and `Writes` (bit k - 1 for component k: what to carry over when the job moves its entity; default all). |
| `IForEachEntity<T1..Tn>` | interface | Struct job receiving the handle: `void Execute(Entity entity, ref T1 c1, …)`. Optional static `Writes`, as for `IForEach`. |
| `IForEachBatch<T1..Tn>` | interface | Struct job receiving runs of rows: `void Execute(Span<T1> c1, …)`; no structural changes. |
| `INoStructuralChanges` | marker interface | A job that makes no structural change: its passes skip the bookkeeping (generated for `[ForEach]` methods that cannot change structure). |
| `QueryEnumerator<T1..Tn>`, `QueryItem<T1..Tn>` | ref structs | Result of `Query`: `item.Entity`, `item.Component1..n` (refs into the row); enumerator `Count`, `IsEmpty`, `Dispose()` (ends the pass; `foreach` calls it). A loop over them cannot `await`. |
| `SingletonRef<T>` | readonly struct | Stable singleton handle; `.Value` is `ref T`. |

## Entities

| API | Complexity | Notes |
|---|---|---|
| `world.CreateEntity()` | O(1) amortized | reuses recycled ids |
| `world.CreateEntity(in T1 … in Tn)` | O(n) amortized | placed directly in its final table |
| `world.CreateEntities(Span<Entity> entities, in T1 … in Tn)` | O(count × n) amortized | one gate pass and at most one table growth for the batch; writes the handles |
| `entity.Destroy()` / `world.DestroyEntity(entity)` | O(c) | the table's last row fills the hole (a tombstone during a pass); recycles the id; stale handles are ignored |
| `world.DestroyEntities<T1..Tn>(filter)` → `int` | O(M + S) | destroys every match in one gate pass and returns how many; safe during a pass |
| `entity.IsAlive` | O(1) | |
| `world.TryGetEntity(id, out e)` / `GetEntity(id)` | O(1) | `GetEntity` throws if no entity is alive with that id |
| `world.EntityCount` | O(1) | |
| `world.Entities` | O(capacity) | diagnostics |
| `world.EnsureEntityCapacity(capacity)` | O(capacity) | pre-size the entity array to avoid growth at runtime; no-op when already large enough |

## Components

| API | Complexity | Notes |
|---|---|---|
| `entity.Get<T>()` → `ref T` | O(1) | version check + column lookup (+ one presence-bit test) + one indexed load; throws if missing |
| `entity.Has<T>()` / `TryGet<T>(out T)` | O(1) | `TryGet` returns a copy |
| `entity.Add<T>()` → `ref T` | O(c) amortized | adds (or resets) and returns a ref; a component the table has a column for only sets its presence bit (O(1)), a new one copies the row |
| `entity.GetOrAdd<T>()` → `ref T` | O(1), or as `Add` when missing | |
| `entity.Set(in T1 … in Tn)` → `Entity` | O(n), or O(c) amortized when it moves | add or overwrite, at most one row copy, chainable |
| `entity.Remove<T1..Tn>()` | O(n) | clears presence bits, the entity keeps its row; missing components are ignored |

## Queries

| API | Complexity | Notes |
|---|---|---|
| `[ForEach]` method / `world.ForEach<T1..Tn, TJob>(ref job, filter)` | O(M + S) | fastest: struct job, no delegate |
| `world.ForEachEntity<T1..Tn, TJob>(ref job, filter)` | O(M + S) | like `ForEach`, the job receives the `Entity` |
| `world.ForEachBatch<T1..Tn, TJob>(ref job, filter)` | O(M + S / 64 + runs) | spans of consecutive rows; one call for a table whose rows all match |
| `foreach (var item in world.Query<T1..Tn>(filter))` | O(M + S) | `item.Entity`, `item.Component1..n` (refs) |
| `world.Count<T1..Tn>(filter)` | O(M), + S / 64 for tables with rows lacking a queried component | sums table counts, or counts presence bits |
| `world.Any<T1..Tn>(filter)` | O(M + S / 64) worst | early exit; S / 64 only for tables with rows lacking a queried component |
| `QueryFilter.Create().With<T>().Without<U>().WithAny<V>()` | O(1) | build once and keep it in a `static readonly` field; `WithAny` calls form one "at least one of" group |

The first time a query shape is used, building its cache entry costs O(all archetypes) under the structural gate.
After that, each lookup is one hash probe. In turn, creating a new archetype (the first entity with a new component
set) costs O(archetypes + cached query shapes + Σ over the matching cached queries of their archetype count), since
it is appended (copy-on-write) to the archetype list and to every cached query it matches. Both are cold paths.

## Systems, singletons, maintenance

| API | Complexity |
|---|---|
| `world.AddSystem<T>()` / `AddSystem(system)` (calls `OnCreate`) | O(systems + b²), b = systems of the same priority (batch rebuild); cold path |
| `world.RemoveSystem<T>()` (calls `OnDestroy`) | O(systems + b²), b = systems of the same priority (batch rebuild); cold path |
| `world.GetSystem<T>()` | O(systems) |
| `world.Update()` (default group) / `world.Update<TGroup>()` | Σ systems of the group; allocation-free |
| `world.AddSingleton<T>()` / `GetSingleton<T>()` / `GetOrAddSingleton<T>()` → `ref T` | O(1) |
| `world.SingletonRef<T>()` → `SingletonRef<T>` (stable handle, `.Value` is `ref T`; adds if missing) | O(1) |
| `world.TryGetSingleton<T>(out T)` / `HasSingleton<T>()` / `RemoveSingleton<T>()` | O(1) |
| `world.CompactArchetypes()` | O(archetypes + tombstones × columns); tables are kept dense automatically, so this is only needed after a pass was interrupted by an exception (which `Update` also handles) |
| `world.GetFragmentationStats()` → `(TotalSlots, AliveEntities, FragmentationRatio)` | O(archetypes) |

Other `World` members:

| Member | Meaning |
|---|---|
| `world.Systems` | the systems of every group, as a new `IReadOnlyList<EcsSystem>` (allocates; for tooling) |
| `world.UpdateCount` | number of default-group updates so far |
| `world.SystemError` | `event Action<EcsSystem, Exception>`; without a subscriber, exceptions propagate |
| `world.Dispose()` | `OnDestroy` on every system in reverse order, then releases storage; stale handles throw |

`RemoveSystem<T>()` returns whether a system was removed. `GetSingleton<T>()` throws if the singleton was not added.
