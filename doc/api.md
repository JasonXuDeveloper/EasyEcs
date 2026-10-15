# API reference and complexity

[Docs index](README.md) · Everything is in `namespace EasyEcs`. T1..T9 overloads are generated from one template
each (see [Architecture](architecture.md#source-generators-and-analyzer-easyecsgenerator)).

Notation:

- *n*: components in the call (1-9).
- *c*: components of the entity.
- *M*: archetypes matching a query.
- *S*: slots in those archetypes, alive entities plus tombstones.
- "Amortized": storage occasionally doubles.

## Types

| Type | Kind | Purpose |
|---|---|---|
| `World` | sealed class, `IDisposable` | Owns entities, components, singletons, systems. |
| `WorldOptions` | sealed class | `Parallel`, `MaxDegreeOfParallelism`, `InitialEntityCapacity` (init-only). |
| `Entity` | readonly struct | Handle: `Id`, `Version`, `World`; equality includes the version. |
| `IComponent` | marker interface | Implemented by component structs. |
| `ISingletonComponent` | marker interface | Implemented by singleton structs. |
| `EcsSystem` | abstract class | `World`, `Priority`, `UpdateInterval`, `Enabled`; `OnCreate` / `OnUpdate` / `OnDestroy`. |
| `SystemAccess` | struct | What a system reads and writes (`Read<T>`, `Write<T>`, `ReadSingleton<T>`, `WriteSingleton<T>`, `RunsAlone`); generated from `[ForEach]` parameters, used by `Parallel` batching. |
| `ForEachAttribute`, `WithAttribute<T>`, `WithoutAttribute<T>` | attributes | `[ForEach]` methods and their filters. |
| `ISystemGroup`, `UpdateInGroupAttribute<TGroup>` | marker / attribute | System groups. |
| `QueryFilter` | readonly struct | `Create().With<T>().Without<U>()`. |
| `IForEach<T1..Tn>` | interface | Struct job: `void Execute(int entityId, ref T1 c1, …)`. |
| `QueryEnumerator<T1..Tn>`, `QueryItem<T1..Tn>` | structs | Result of `Query`: `item.Entity`, `item.Component1..n` (refs); enumerator `Count`, `IsEmpty`. |
| `SingletonRef<T>` | readonly struct | Stable singleton handle; `.Value` is `ref T`. |

## Entities

| API | Complexity | Notes |
|---|---|---|
| `world.CreateEntity()` | O(1) amortized | reuses recycled ids |
| `world.CreateEntity(in T1 … in Tn)` | O(n) amortized | placed directly in its final archetype |
| `entity.Destroy()` / `world.DestroyEntity(entity)` | O(c) | recycles the id; stale handles are ignored |
| `entity.IsAlive` | O(1) | |
| `world.TryGetEntity(id, out e)` / `GetEntity(id)` | O(1) | `GetEntity` throws if no entity is alive with that id |
| `world.EntityCount` | O(1) | |
| `world.Entities` | O(capacity) | diagnostics |
| `world.EnsureEntityCapacity(n)` | O(capacity) | pre-size to avoid growth at runtime |

## Components

| API | Complexity | Notes |
|---|---|---|
| `entity.Get<T>()` → `ref T` | O(1) | version check + one mask test + one indexed load; throws if missing |
| `entity.Has<T>()` / `TryGet<T>(out T)` | O(1) | `TryGet` returns a copy |
| `entity.Add<T>()` → `ref T` | O(1) amortized | adds (or resets) and returns a ref |
| `entity.GetOrAdd<T>()` → `ref T` | O(1) amortized | |
| `entity.Set(in T1 … in Tn)` → `Entity` | O(n) amortized | add or overwrite, one archetype transition, chainable |
| `entity.Remove<T1..Tn>()` | O(n) | one transition; missing components are ignored |

## Queries

| API | Complexity | Notes |
|---|---|---|
| `[ForEach]` method / `world.ForEach<T1..Tn, TJob>(ref job, filter)` | O(M + S) | fastest: struct job, no delegate |
| `foreach (var item in world.Query<T1..Tn>(filter))` | O(M + S) | `item.Entity`, `item.Component1..n` (refs) |
| `world.Count<T1..Tn>(filter)` | O(M) | sums archetype counts |
| `world.Any<T1..Tn>(filter)` | O(M) worst | early exit |
| `QueryFilter.Create().With<T>().Without<U>()` | O(1) | build once and keep it in a `static readonly` field |

The first time a query shape is used, building its cache entry costs O(all archetypes). After that, each lookup is
one hash probe.

## Systems, singletons, maintenance

| API | Complexity |
|---|---|
| `world.AddSystem<T>()` / `AddSystem(system)` (calls `OnCreate`) | O(systems) |
| `world.RemoveSystem<T>()` (calls `OnDestroy`) / `GetSystem<T>()` | O(systems) |
| `world.Update()` (default group) / `world.Update<TGroup>()` | Σ systems of the group, allocation-free |
| `world.AddSingleton<T>()` / `GetSingleton<T>()` / `GetOrAddSingleton<T>()` → `ref T` | O(1) |
| `world.SingletonRef<T>()` → `SingletonRef<T>` (stable handle, `.Value` is `ref T`; adds if missing) | O(1) |
| `world.TryGetSingleton<T>(out T)` / `HasSingleton<T>()` / `RemoveSingleton<T>()` | O(1) |
| `world.CompactArchetypes()` | O(S log S) |
| `world.GetFragmentationStats()` → `(TotalSlots, AliveEntities, FragmentationRatio)` | O(archetypes) |

Other `World` members:

| Member | Meaning |
|---|---|
| `world.Systems` | the systems, as `IReadOnlyList<EcsSystem>` |
| `world.UpdateCount` | number of default-group updates so far |
| `world.SystemError` | `event Action<EcsSystem, Exception>`; without a subscriber, exceptions propagate |
| `world.Dispose()` | `OnDestroy` on every system in reverse order, then releases storage; stale handles throw |

`RemoveSystem<T>()` returns whether a system was removed. `GetSingleton<T>()` throws if the singleton was not added.
