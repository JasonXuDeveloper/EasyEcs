# Migration Guide: EasyEcs v3.x → v4.x

**Contents**

- [v4.2 → v4.3](#v42--v43)
- [v4.1 → v4.2](#v41--v42)
- [v4.0 → v4.1](#v40--v41)
- [v3.x → v4.0](#v3x--v40): [Namespaces](#namespaces) · [Entities and components](#entities-and-components) ·
  [Singletons](#singletons) · [Systems](#systems) · [Behavioral changes](#behavioral-changes) ·
  [Source generator and analyzer](#source-generator-and-analyzer)
- [v2.x → v3.0](#migration-guide-easyecs-v2x--v30): [Breaking changes](#breaking-changes-summary) ·
  [Migration steps](#migration-steps) · [New features](#new-features-in-v30) ·
  [Common patterns](#common-migration-patterns) · [Troubleshooting](#troubleshooting)

For the current API, see the [documentation](docs/content/README.md).

## v4.2 → v4.3

The analyzer gained performance rules ([EECS025 to EECS034](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs025)). Code written for 4.2 compiles
unchanged unless a system calls `World.Update()`, or the build treats warnings as errors.

- **New warnings (EECS025 to EECS032).** Each names code that works but costs, with the fix in its
  [section](https://ecs.xgamedev.net/en/docs/guide/analyzer) and, where the change is mechanical, a code fix in the IDE. Apply the fix, or suppress the
  rule where the code is deliberate, with the reason next to it:

  ```csharp
  #pragma warning disable EECS026 // the write access is the point: it stamps the rows a [Changed<T>] system reacts to
  [ForEach] static void Touch(ref Armor armor, ref Seen seen) => seen.Count++;
  #pragma warning restore EECS026
  ```

  or for a whole project in `.editorconfig`: `dotnet_diagnostic.EECS028.severity = suggestion`.
- **`World.Update()` in a system is an error (EECS034).** It ran the system again inside its own run. Run updates and
  groups from the code that hosts the world:

  **v4.2**

  ```csharp
  public class CatchUp : EcsSystem
  {
      protected override void OnUpdate() => World.Update();
  }
  ```

  **v4.3**

  ```csharp
  while (running) world.Update();   // in the host loop
  ```

- **The suggestion (EECS033)** shows in the IDE only and never fails a build: the generated member is the same call
  at the same cost, only shorter. `dotnet_diagnostic.EECS033.severity = warning` in `.editorconfig` turns it into a
  warning.

## v4.1 → v4.2

One behavior changed; code written for 4.1 compiles unchanged.

- **A `[ForEach]` singleton parameter throws when the singleton is missing.** The parameter resolves through
  `world.SingletonRef<T>()`, which used to add the singleton with its default value; both now throw
  `InvalidOperationException`, like `world.X` and `GetSingleton<T>()`, on the system's first run (even when no entity
  matches). The message names the fix: `Singleton Clock was not added to this world: set it before the systems that
  read it run (world.SetClock(...) or world.AddSingleton<Clock>()), or use GetOrAddClock() where a default is
  meant`. Add every singleton your systems read where the world is built, before the first `Update()`, also in tests:

  **v4.1** (`Ticker` takes `ref Clock`; the first update added a default `Clock`, with no step):

  ```csharp
  var world = new World();
  world.AddSystem<Ticker>();
  world.Update();
  ```

  **v4.2** (set it first, or call `world.GetOrAddClock()` where the default is right):

  ```csharp
  var world = new World();
  world.SetClock(Tick: 0, DeltaTime: 1f / 30);
  world.AddSystem<Ticker>();
  world.Update();
  ```

  A world restored with `Load` already holds the singletons of its save; add only the ones the save did not hold.

## v4.0 → v4.1

A few APIs changed (listed first below). Other code written for 4.0 compiles unchanged unless it stores `Query`
results in fields, uses them across `await`, captures them in lambdas, uses them in iterators or LINQ, or passes them
as generic arguments (they are ref structs now, see below). The storage moved from one column per component
type (indexed by entity id) to archetype tables (one row per entity, columns 64-byte aligned), which changes a few
behaviors:

- **`WorldOptions.Parallel` is `WorldOptions.ParallelSystems`.** The option runs systems side by side; the new name
  keeps it apart from `[ForEach(Parallel = true)]`, which splits one method's rows over threads. Rename the
  initializer: `new WorldOptions { ParallelSystems = true }`.
- **`Entity.Id` is an `EntityId`** (index + incarnation, 8 bytes, unmanaged) instead of an `int`, and the
  `Entity.Version` field is gone: read `entity.Id.Index` and `entity.Id.Version`. `world.GetEntity`,
  `TryGetEntity` and `IsAlive` take an `EntityId`; a handle converts to one implicitly, so `world.GetEntity(entity)`
  compiles. Store `EntityId`s, not `Entity` handles, in components: a handle holds its world, which makes the column
  managed (scanned by the GC, not copied as a block, not serializable), and the new warning
  [EECS017](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs017) points at such fields.
- **`EcsSystem.Priority` is gone.** Systems run in registration order; order them by what they depend on with
  `[UpdateBefore<TSystem>]` / `[UpdateAfter<TSystem>]` on the class (a cycle is
  [EECS019](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs019), an order against a system of another group
  [EECS020](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs020)).
- **The `World.SystemError` event is gone.** A system's exception propagates out of `Update` (the default), or, with
  `new WorldOptions { SystemErrors = SystemErrorPolicy.Disable }`, switches the system off and stays in
  `system.Error` for the application to read from `world.Systems` (enable the system again to retry).
- **Systems need no `partial`.** The generated code lives outside the system's type, so EECS001 is gone; `partial`
  still compiles.
- **`UpdateInterval` and the new `UpdatePhase` should be constants**: they are read every update, and the warning
  [EECS018](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs018) points at overrides that compute them. To stop and resume a system, set
  `Enabled`.
- **`world.DestroyEntity(entity)` is gone:** it did what `entity.Destroy()` does. Call `entity.Destroy()`, or
  `world.DestroyEntities<T…>(filter)` for every match at once.
- **`IForEach` jobs receive only the components.** `Execute(int entityId, ref T1 c1, …)` became
  `Execute(ref T1 c1, …)`, so a pass never loads the entity rows for them. A job that needs the entity implements
  `IForEachEntity<…>` (`Execute(Entity entity, ref T1 c1, …)`) and runs with
  `world.ForEachEntity`.
- **`CompactArchetypes()` and `GetFragmentationStats()` are gone.** Tables stay dense on their own, and passes now
  end even when a job throws, so there is nothing to compact and nothing to monitor (the ratio was always 0 between
  updates). Delete the calls.
- **`entity.Add<T>()` is gone.** It did what `Set` and `GetOrAdd` do, and reset a component the entity already had,
  unlike adding in other ECS libraries. Write `entity.Set(new T(…))` (or the generated `entity.SetX(…)`, `SetDead()`
  for a tag) to add or replace a value, and `ref var c = ref entity.GetOrAdd<T>()` to add a default value only when
  it is missing and fill it through the ref.
- **Stale and `default` handles answer questions with false.** `Has<T>()` and `TryGet<T>()` used to throw for a
  destroyed entity (and `default(Entity).Has<T>()` threw a `NullReferenceException`); they now return false, like
  `IsAlive`. `Get`, `GetOrAdd`, `Set` and `Remove` still throw, now `InvalidOperationException` for `default` handles too.
- **`world.Systems` is a `SystemList`** (allocation-free `foreach` and `Count`) instead of a new `IReadOnlyList`;
  `foreach` loops compile unchanged.
- **`QueryEnumerator.Count` / `IsEmpty` are gone:** use `world.Count` / `world.Any` with the same filter.
- **`entity.Remove<…>()` returns the entity** for chaining (calls that ignore it compile unchanged).
- **Structural changes belong to the world's owner thread.** 4.0 serialized creates, destroys, `Add`/`Set`/`Remove`
  and singleton or system changes from any thread with a compare-and-swap lock. 4.1 checks a plain owner flag
  instead: a change made while another runs, or inside a parallel stage of systems or a parallel `[ForEach]`, throws
  `InvalidOperationException`; two threads of your own changing one world at once are not caught reliably. Make
  structural changes on the thread that calls `Update` (other threads hand them over as data through a queue a
  system drains: [input from other threads](https://ecs.xgamedev.net/en/docs/guide/threading#input-from-other-threads)), or give each thread
  its own world. Two new analyzer errors point at such code: **EECS013** (a structural change in a `[ForEach]`
  method that takes no `Entity` or `World`, e.g. through a handle a component holds: take the `Entity` as a
  parameter) and **EECS014** (a structural change in a lambda handed to `Task.Run`, `Parallel.*`, `ThreadPool` or a
  `Thread` that races with other changes of the same world).
- **Every analyzer rule is an error.** EECS007 (mutable state in a system) and EECS008 (a system referencing a
  system) were warnings in 4.0; code that triggers them no longer builds. Move the state into a component or
  singleton, and communicate through data.
- **Iteration semantics are stricter.** A pass visits each entity at most once: entities created or moved into a
  matching archetype during the pass are not visited by it (one that only gets a queried component back keeps its
  row and is visited if the pass has not reached it); entities destroyed or
  losing a queried component before their turn are skipped. `Without<T>` filters that only existed to stop a job from
  revisiting what it produced are no longer needed (they still work).
- **Refs move more often.** A `ref` from `entity.Get<T>()` or a `Query` item can now be invalidated by a structural
  change of *another* entity (a row filled by a table's last row, a table that grows), not only by entity-capacity
  growth. The documented rule was already "a ref is valid until the next structural change"; code that relied on
  refs surviving changes must re-read through `entity.Get<T>()`. The `ref` parameters of `[ForEach]` methods and
  `ForEach` jobs stay writable when the body changes the visited entity's archetype.
- **Removing a component keeps the entity's row.** `Has<T>()`, `Get<T>()` and every query treat the component as
  absent; adding it back is a bit flip.
- **`Query` results are ref structs.** `QueryEnumerator<…>` and `QueryItem<…>` hold refs into the current table, so
  a `foreach` over a query cannot contain an `await` and an item cannot be stored. Read what you need, or keep
  `item.Entity`, then await outside the loop.
- **Iteration order** follows table rows, not entity ids, and can change when rows are moved or filled. Never rely
  on it.
- **`EnsureEntityCapacity` / `InitialEntityCapacity`** pre-size the entity records only; archetype tables grow on
  their own.
- **`ParallelSystems` keeps subclasses it cannot see alone.** A system class that derives from a generated system
  without adding `[ForEach]` methods of its own (for example to override `OnUpdate`) now runs alone in its priority, as
  its `OnUpdate` may touch anything. Give the subclass a `[ForEach]` method (it is then generated) to run it beside others again.
- **`AddSingleton<T>()` on an existing singleton resets it in place.** It used to replace the storage, which left
  `SingletonRef<T>` handles taken before pointing at a detached copy; they now see the reset value.
- **`RemoveSingleton<T>()` returns `bool`** (false when the singleton was not added). Calls that ignore the result
  compile unchanged.
- **`Entity.GetHashCode()` is deterministic** (`Index ^ (Version << 20)`) instead of `HashCode.Combine` (randomized per
  process). Equality is unchanged; only code that persisted hash codes across processes could notice.

New:

- `[ForEach]` methods can take `Span<T>` / `ReadOnlySpan<T>` components (plus singletons) and receive whole runs of
  rows, for SIMD ([EECS009](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs009) reports mixed signatures). The ad-hoc form is
  `world.ForEachBatch` with an `IForEachBatch<T1..Tn>` job.
- `world.ForEachEntity` with an `IForEachEntity<T1..Tn>` job receives the `Entity` handle instead of the id.
- `INoStructuralChanges` marks a job that makes no structural change; the generator adds it to `[ForEach]` methods
  that cannot change structure, which then iterate without per-entity bookkeeping.
- `QueryFilter.WithAny<T>()` and `[WithAny<T>]`: the entity needs at least one of the named components.
- `world.CreateEntities(span, components…)` and `world.DestroyEntities<T…>(filter)` spawn and clear waves with one
  pass through the structural gate.
- `WorldOptions.MeasureSystems` times every system run into `system.Stats` (`SystemStats`).
- `[ForEach(Parallel = true)]` (and `world.ForEachParallel` / `ForEachEntityParallel`) runs chunks of
  `World.ParallelChunkRows` rows on several threads; [EECS010](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs010) reports methods that could race.
- `world.Capture()` / `world.Restore(snapshot)`: in-memory snapshots of entities, components and singletons
  ([Snapshots](https://ecs.xgamedev.net/en/docs/guide/snapshots)).
- `ITrackChanges`, `[Changed<T>]`, `QueryFilter.Changed<T>(since)` / `Since(since)`, `world.ChangeTick`,
  `system.LastRunTick`: change tracking ([Change tracking](https://ecs.xgamedev.net/en/docs/guide/change-tracking)). The job interfaces gained
  an optional static `Writes` member (with a default).
- `[Removed<T>]` and `QueryFilter.Removed<T>(since)`: the entities that lost a tracked component since a tick
  (replication of what went away), with [EECS016](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs016) for filters no row can pass.
- `Prefab` and `world.Instantiate(prefab)` / `Instantiate(prefab, span)`: entities from a recipe of components that
  belongs to no world, built with `Set` / `Remove` or the generated `prefab.SetX(…)` / `RemoveX()` (chainable),
  and `SetBuffer(…)` / `SetLoot(…)` for the elements of a `DynamicBuffer<T>`; `entity.Clone()` copies a living
  entity.
- `IIndexed<TKey>`, `world.Index<T, TKey>()`, `ValueIndex<T, TKey>`: find entities by a component value; relations
  are indexed `Entity` keys ([Indexes and relations](https://ecs.xgamedev.net/en/docs/guide/indexes-and-relations)).
- `entity.Read<T>()` returns a `ref readonly T` that never counts as a write of a tracked component, and
  `entity.TryGetRef<T>(out bool found)` returns a `ref T` (a null ref when missing): optional access without a
  copy. `entity.Has<T1..Tn>()` tests several components with one lookup.
- `DynamicBuffer<T>`: a list per entity as a component, written only through `ref` (each write is a tracked change),
  copied by snapshots ([Dynamic buffers](https://ecs.xgamedev.net/en/docs/guide/dynamic-buffers)).
- [EECS011](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs011) rejects a `[ForEach]` method that changes a collection or class held by an
  `in` parameter (the change would be neither scheduled as a write nor tracked);
  [EECS012](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs012) rejects a `DynamicBuffer<T>` copied by value.
- The library is marked `IsAotCompatible`: trimming and Native AOT analyzers run on every build, and it publishes
  and runs with Native AOT.
- `entity.Disable<T>()` / `Enable<T>()` (generated `DisableX()` / `EnableX()` / `IsXDisabled`): switch a component
  off and keep its value; queries see `Disabled<T>`. `world.SetAll` / `RemoveAll` / `DisableAll` / `EnableAll`
  change every entity a filter matches, 64 rows at a time.
- `ICleanupComponent`: destroying an entity that holds one keeps it, tagged `Destroyed`, until a system releases what
  it holds and removes the last cleanup component.
- `ChildOf(EntityId Parent)` and `world.Children(parent)`: a hierarchy whose parents destroy their children;
  `IRelation` elements of a `DynamicBuffer<T>` (`entity.AddRelation`, `world.Relations<T>()`) link an entity to any
  number of targets, and `TargetRules` say what happens to links when a target is destroyed.
- Ordered walks of a value index with a comparable key (`Range`, `RangeDescending`, `Ascending`, `Descending`), and
  `world.GroupBy<T>()`, which splits tables by the value of an `IGroup` component.
- `Maybe<T>` parameters of `[ForEach]` methods: an optional component without a second method.
- `world.Save` / `world.Load` (a world as bytes, through Nino), `SaveEntities` / `LoadEntities` / `MoveEntities` (a
  group of entities to another world or process, `EntityId` fields rewritten), `world.Capture(into)` (reuses an
  earlier snapshot's arrays).
- `world.RemoveTypes(assembly)`: unload game logic loaded into a collectible `AssemblyLoadContext`; its systems,
  singletons, indexes and components leave the world, and the process forgets its types once it unloads.
- `world.Describe()` (tables, columns, sizes), `entity.Describe()` and a debugger view of entities.
- Static `[ForEach]` methods outside systems (a generated entry point takes the `World` and the method's other
  arguments: one pass for tools and tests), generic systems, filters on
  system groups (`[With<T>]` on a group struct applies to all its systems), `UpdatePhase` to spread systems with the
  same `UpdateInterval` over updates, `world.GetOrAddX()` for singletons, named query items (`item.Position` for
  `item.Component1`), code fixes for the analyzer's diagnostics, and [EECS024](https://ecs.xgamedev.net/en/docs/guide/analyzer#eecs024) for
  `[ForEach]` on a local function.

## v3.x → v4.0

v4 targets **.NET 10 only**, moves the whole API into the `EasyEcs` namespace with standard ECS vocabulary, makes
systems synchronous and generates the repetitive parts (arity overloads, per-entity jobs).

### Namespaces

`EasyEcs.Core`, `EasyEcs.Core.Components` and `EasyEcs.Core.Systems` → **`using EasyEcs;`**

### Entities and components

v3 had three handle types (`Entity` value, `EntityRef`, `ComponentRef<T>`). v4 has one: `Entity`, a version-checked
16-byte handle. Component access returns `ref T` directly.

| v3 | v4 |
|---|---|
| `Context` | `World` |
| `EntityRef e = ctx.CreateEntity()` | `Entity e = world.CreateEntity()` or `world.CreateEntity(new Position(..), new Velocity(..))` |
| `e.Value.AddComponent<T>().Value.X = 1` | `e.GetOrAdd<T>().X = 1` (`ref T`), or `e.Set(new T(..))` |
| `e.Value.AddComponents<A, B>()` | `e.Set(new A(..), new B(..))` (add or overwrite values, one transition) |
| `e.Value.GetComponent<T>().Value` | `e.Get<T>()` (`ref T`) |
| `e.Value.TryGetComponent<T>(out ComponentRef<T> r)` | `e.TryGet<T>(out T value)` / `ref var x = ref e.GetOrAdd<T>()` |
| `e.Value.HasComponent<T>()` | `e.Has<T>()` |
| `e.Value.RemoveComponent(s)<..>()` | `e.Remove<..>()` |
| `ctx.DestroyEntity(e)` | `e.Destroy()` |
| `ctx.GetEntityById(id)` / `TryGetEntityById` | `world.GetEntity(id)` / `TryGetEntity` |
| `ctx.AllEntities` | `world.Entities` |
| `ctx.GroupOf<T..>()` → `GroupResult` | `world.Query<T..>(filter)` → `QueryItem` (`item.Entity`, `item.Component1..n`) |
| `ctx.CountOf<T..>()` / `AnyOf<T..>()` | `world.Count<T..>(filter)` / `world.Any<T..>(filter)` |
| `new Context(new Context.Options(parallel, levels, capacity))` | `new World(new WorldOptions { ParallelSystems = .., MaxDegreeOfParallelism = .., InitialEntityCapacity = .. })` (parallel is **off** by default) |

### Singletons

| v3 | v4 |
|---|---|
| `AddSingletonComponent<T>()` / `GetSingletonComponent<T>().Value` | `AddSingleton<T>()` / `GetSingleton<T>()` / `GetOrAddSingleton<T>()` (all `ref T`) |
| `TryGetSingletonComponent<T>(out SingletonComponentRef<T>)` | `TryGetSingleton<T>(out T value)` |

Singletons are now **per world** (v3 shared them process-wide). A `[ForEach]` method takes a singleton as a
`ref`/`in` parameter (resolved once per pass), so systems rarely call `GetSingleton` themselves.

### Systems

v3 systems implemented `IInitSystem` / `IExecuteSystem` / `IEndSystem` with `UniTask` methods. v4 systems derive from
`EcsSystem` and override synchronous hooks (the UniTask dependency is gone):

```csharp
// v3
public class S : SystemBase, IInitSystem, IExecuteSystem
{
    public UniTask OnInit(Context ctx) { ...; return UniTask.CompletedTask; }
    public UniTask OnExecute(Context ctx) { ...; return UniTask.CompletedTask; }
}

// v4
public partial class S : EcsSystem
{
    protected override void OnCreate() { ... }      // when added to the world
    protected override void OnUpdate() { ... }      // every update (World property available)

    [ForEach] static void Step(ref Position p, in Velocity v) { ... }   // per entity, after OnUpdate
}
```

- `ctx.Init()` is gone: `OnCreate` runs when the system is added. `await ctx.Update()` → `world.Update()`;
  `await ctx.DisposeAsync()` → `world.Dispose()` (`OnDestroy` in reverse order).
- `IExecuteSystem.ExecuteFrequency` → `EcsSystem.UpdateInterval`; `AddSystem<T>()` returns the system;
  `GetSystem<T>()` and `Enabled` were added; `RemoveSystem<T>()` now returns `bool` (whether a system was removed).
- Exceptions: `world.SystemError` (system, exception); without a subscriber the exception propagates.

### Behavioral changes

1. **Entity ids are recycled.** `Entity.Id` is unique only among live entities; keep the `Entity` handle (id +
   version). Equality includes the version.
2. **Stale handles are rejected**: access and structural changes throw; `Destroy` of a stale handle is ignored.
3. **Removing a component / destroying an entity releases managed references** held by the component.
4. **Components and singletons must be structs**: the analyzer reports `EECS006` for classes.
5. Never call `CompactArchetypes()` while iterating (it rewrites rows).
6. **Systems should be stateless and independent**: the analyzer warns about mutable fields on systems (`EECS007`,
   move the state into a component or singleton) and about systems referencing other systems (`EECS008`,
   communicate through data). See [ECS principles](https://ecs.xgamedev.net/docs/ecs-principles).
7. **`IEvent`, `IEventHandler<T>` and `Publish` are removed.** Write state changes as data (a tag or a field) and
   let later systems query them.
8. **`ParallelSystems` is safe by construction.** v3 ran every system of one priority at once and left data races to
   you. v4 runs systems of one priority together only when their generated access does not conflict (`ref` = write,
   `in` = read); systems with an `Entity`/`World` parameter, instance `[ForEach]` methods, an `OnUpdate` override or
   no `[ForEach]` method run alone. The result equals a sequential update. See
   [Threading](https://ecs.xgamedev.net/en/docs/guide/threading#parallel-systems).

### Source generator and analyzer

Shipped in the package (`analyzers/dotnet/cs`). Projects referencing `EasyEcs.Core` by `ProjectReference` must also
reference `EasyEcs.Generator.csproj` with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`.

---


# Migration Guide: EasyECS v2.x → v3.0

## Overview

EasyECS v3.0 is a complete architectural redesign focused on **zero-allocation immediate execution** for high-performance server applications. This guide will help you migrate from v2.x to v3.0.

## Breaking Changes Summary

### 1. No More Callbacks - Immediate Execution

**v2.x (Deferred with Callbacks):**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Position>(pos => {
        pos.Value.X = 10;
        entity.AddComponent<Velocity>(vel => {
            vel.Value.X = 5;
        });
    });
});
```

**v3.0 (Immediate with Direct Returns):**
```csharp
var entity = context.CreateEntity();
var pos = entity.AddComponent<Position>();
pos.Value.X = 10;
var vel = entity.AddComponent<Velocity>();
vel.Value.X = 5;
```

### 2. API Changes

| v2.x API | v3.0 API | Notes |
|----------|----------|-------|
| `void CreateEntity(Action<EntityRef>)` | `EntityRef CreateEntity()` | Returns entity directly |
| `void AddComponent<T>(Entity, Action<ComponentRef<T>>)` | `ComponentRef<T> AddComponent<T>(Entity)` | Returns component ref |
| `void AddComponent<T>(Action<ComponentRef<T>>)` | `ComponentRef<T> AddComponent<T>()` | On Entity struct |
| `void AddSingletonComponent<T>(Action<...>)` | `SingletonComponentRef<T> AddSingletonComponent<T>()` | Returns ref |
| `void AddSystem<T>()` | `void AddSystem<T>()` | Unchanged (immediate) |
| `void RemoveSystem<T>()` | `void RemoveSystem<T>()` | Unchanged (immediate) |

### 3. Execution Model

**v2.x:**
- Operations queued to command buffer
- Executed at end of frame
- Callbacks invoked after execution

**v3.0:**
- All operations execute immediately
- Thread-safe via .NET 8.0 Lock
- No command buffer
- No callbacks

### 4. Version Tracking

**v3.0 adds entity versioning:**
```csharp
public struct Entity
{
    public readonly int Id;
    public readonly int Version;  // NEW: Increments on destroy
    // ...
}
```

EntityRef and ComponentRef validate version on access, throwing if entity was destroyed.

## Migration Steps

### Step 1: Update Project to .NET 8.0

**Before (.csproj):**
```xml
<TargetFramework>net6.0</TargetFramework>
<LangVersion>10</LangVersion>
```

**After (.csproj):**
```xml
<TargetFramework>net8.0</TargetFramework>
<LangVersion>13</LangVersion>
```

> **Note:** C# 13 is required for ref field support and other performance optimizations.

### Step 2: Remove Callback-Based Code

**Pattern 1: Entity Creation**

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    // Use entity here
    entity.AddComponent<Position>();
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
entity.AddComponent<Position>();
```

**Pattern 2: Component Addition**

❌ **Old:**
```csharp
entity.AddComponent<Health>(health => {
    health.Value.HP = 100;
});
```

✅ **New:**
```csharp
var health = entity.AddComponent<Health>();
health.Value.HP = 100;
```

**Pattern 2b: Modifying Components (IMPORTANT)**

Since components are **structs**, you must modify them correctly:

✅ **Correct - Direct assignment:**
```csharp
var health = entity.AddComponent<Health>();
health.Value.HP = 100;  // Directly modify via ComponentRef
```

✅ **Correct - Using ref:**
```csharp
var health = entity.AddComponent<Health>();
ref var h = ref health.Value;  // Get ref to modify in-place
h.HP = 100;
h.MaxHP = 150;
```

❌ **WRONG - Local copy (immutable):**
```csharp
var health = entity.AddComponent<Health>();
var h = health.Value;  // Creates a COPY!
h.HP = 100;            // Modifies the COPY, not the component!
// Component unchanged - changes lost!
```

**Rule:** Always use either `compRef.Value.field = ...` or `ref compRef.Value` to modify components. Never copy the Value to a local variable for modification.

⚠️ **CRITICAL: Ref Safety Warning**

Using `ref` to hold component or entity data can be **dangerous** if arrays are resized while the ref is alive:

```csharp
// ⚠️ DANGEROUS: ref becomes invalid if arrays resize
var entity = context.CreateEntity();
var health = entity.AddComponent<Health>();
ref var h = ref health.Value;
h.HP = 100;  // ✅ Safe: ref is still valid here

// Operations that invalidate component refs (resize component arrays):
context.CreateEntity();         // ⚠️ May resize ALL component arrays → ALL refs dangle!

h.HP = 50;  // ❌ UNSAFE! ref may point to old array or crash

// These operations are SAFE for refs:
entity.AddComponent<Armor>();        // ✅ Safe: doesn't resize component arrays
context.DestroyEntity(someEntity);   // ✅ Safe: doesn't resize component arrays
entity.RemoveComponent<Shield>();    // ✅ Safe: doesn't resize component arrays
context.CompactArchetypes();         // ✅ Safe: only compacts archetype lists, doesn't resize component arrays
```

**Key insight:** Component data is stored in `Context.Components[typeIndex][entityId]` arrays. Only **CreateEntity** (when capacity is exceeded) resizes these component arrays. AddComponent/RemoveComponent only change which archetype list an entity belongs to—they don't move or resize component data. CompactArchetypes only compacts archetype entity lists (removes tombstones) without touching component arrays.

**✅ Singleton Components are Safe with Refs:**

Singleton components are stored separately and are **NOT affected** by `CreateEntity()` or `CompactArchetypes()`:

```csharp
// ✅ SAFE: Singleton component refs are always valid
var config = context.AddSingletonComponent<GameConfig>();
ref var cfg = ref config.Value;
cfg.MaxPlayers = 100;

// Safe: singleton data doesn't move
context.CreateEntity();
context.CompactArchetypes();

cfg.MaxPlayers = 200;  // Still safe!
```

⚠️ **Warning:** Don't repeatedly add the same singleton component type - it will erase the old data:
```csharp
var config = context.AddSingletonComponent<GameConfig>();
config.Value.MaxPlayers = 100;

// ❌ BAD: Re-adding same singleton erases previous data!
context.AddSingletonComponent<GameConfig>();  // Old data lost!
```

⚠️ **Warning:** Similarly, don't repeatedly add the same component to an entity - it will erase the old data:
```csharp
var entity = context.CreateEntity();
var health = entity.AddComponent<Health>();
health.Value.HP = 100;
health.Value.MaxHP = 150;

// ❌ BAD: Re-adding same component erases previous data!
var health2 = entity.AddComponent<Health>();  // Old HP/MaxHP values lost! Reinitialized to default.
// health2.Value.HP is now 0 (default), not 100!
```

If the entity already has a component, use `GetComponent` or `TryGetComponent` instead:
```csharp
// ✅ Good: Check first
if (!entity.TryGetComponent<Health>(out var health))
{
    health = entity.AddComponent<Health>();
}
health.Value.HP = 100;
```

**When to use ref:**
- ✅ **Inside tight loops** during GroupOf iteration (when NOT creating entities):
  ```csharp
  foreach (var result in context.GroupOf<Position, Velocity>())
  {
      ref var pos = ref result.Component1.Value;
      ref var vel = ref result.Component2.Value;
      pos.X += vel.X;
      pos.Y += vel.Y;  // ✅ Safe: no CreateEntity calls, refs remain valid

      // ✅ Safe during GroupOf iteration (doesn't break iterator OR invalidate refs):
      context.DestroyEntity(someEntity);   // Safe: uses tombstones, doesn't resize arrays
      result.Entity.AddComponent<Dead>();  // Safe: changes archetype, doesn't resize arrays
      result.Entity.RemoveComponent<AI>(); // Safe: changes archetype, doesn't resize arrays

      // ❌ UNSAFE with ref: CreateEntity may resize arrays!
      // context.CreateEntity();           // Iterator OK, but ref pos/vel become INVALID!
      // pos.X = 0;                        // CRASH! ref is now dangling!

      // ⚠️ Don't call CompactArchetypes() here - invalidates GroupOf iterator!
      // context.CompactArchetypes();      // CRASH! Breaks the iterator itself!
  }
  ```

  **Note:** During GroupOf iteration, you can safely call DestroyEntity/AddComponent/RemoveComponent (tombstone pattern handles these), but:
  - **CreateEntity** may resize component arrays → invalidates component `ref` variables (but iterator continues)
  - **CompactArchetypes** removes tombstones → invalidates the `GroupOf` iterator itself (crashes immediately)

  If you need to create entities during iteration, don't use `ref` - use `result.Component1.Value` directly.

**When NOT to use ref:**
- ❌ **When creating entities** while ref is alive (may resize component arrays)
- ✅ **Adding/removing components is SAFE** (doesn't resize component arrays)
- ✅ **Destroying entities is SAFE** (doesn't resize component arrays, but logically wrong if destroying the entity whose component you're referencing)
- ✅ **CompactArchetypes is SAFE** (only compacts archetype lists, doesn't resize component arrays)

**Safe Alternative:**
```csharp
// ✅ SAFE: Always get fresh reference through ComponentRef
var entity = context.CreateEntity();
var health = entity.AddComponent<Health>();
health.Value.HP = 100;

// Structural changes are safe
context.CreateEntity();
entity.AddComponent<Armor>();

// Access through ComponentRef is always valid
health.Value.HP = 50;  // Safe: ComponentRef validates and accesses current data
```

**Rule of thumb:** If you're unsure, **always use `compRef.Value.field`** instead of `ref compRef.Value`. Only use `ref` for short-lived, local optimizations where you can guarantee no entity creation occurs (which could resize component arrays). AddComponent/RemoveComponent/CompactArchetypes are safe with refs.

**Pattern 3: Nested Callbacks**

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Position>(pos => {
        pos.Value.X = 10;
        entity.AddComponent<Velocity>(vel => {
            vel.Value.X = 5;
        });
    });
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
var pos = entity.AddComponent<Position>();
pos.Value.X = 10;
var vel = entity.AddComponent<Velocity>();
vel.Value.X = 5;
```

### Step 3: Update Singleton Components

❌ **Old:**
```csharp
context.AddSingletonComponent<GameConfig>(config => {
    config.Value.MaxPlayers = 100;
});
```

✅ **New:**
```csharp
var config = context.AddSingletonComponent<GameConfig>();
config.Value.MaxPlayers = 100;
```

### Step 4: Update Entity Destruction

**No changes needed** - still immediate:
```csharp
context.DestroyEntity(entity);  // Same in both versions
```

### Step 5: Use Batch Component Methods (New in v3.0)

**For adding/removing multiple components, use batch methods for better performance:**

✅ **Efficient - Single archetype transition:**
```csharp
// Add multiple components at once
var (pos, vel) = entity.AddComponents<Position, Velocity>();
pos.Value.X = 10;
vel.Value.X = 5;

// Or with 3 components
var (pos, vel, health) = entity.AddComponents<Position, Velocity, Health>();

// Remove multiple components at once
entity.RemoveComponents<Position, Velocity>();
```

❌ **Inefficient - Multiple archetype transitions:**
```csharp
// Each call moves entity between archetypes
entity.AddComponent<Position>();  // Archetype transition #1
entity.AddComponent<Velocity>();  // Archetype transition #2
// 2x more expensive than batch method!
```

**When to use batch methods:**
- Adding/removing 2-3 components to the same entity
- Building entities with multiple components
- Optimizing hot paths with frequent component changes

### Step 6: Iteration (No Changes)

**GroupOf iteration is unchanged:**
```csharp
foreach (var result in context.GroupOf<Position, Velocity>())
{
    result.Component1.Value.X += result.Component2.Value.X;
}
```

### Step 7: System Implementation (Mostly Unchanged)

Systems work the same way, just use the new immediate API:

```csharp
public class MovementSystem : SystemBase, IExecuteSystem
{
    public async UniTask OnExecute(Context context)
    {
        foreach (var result in context.GroupOf<Position, Velocity>())
        {
            ref var pos = ref result.Component1.Value;
            ref var vel = ref result.Component2.Value;
            pos.X += vel.X;
        }
    }
}
```

If creating entities in systems:

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Projectile>();
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
entity.AddComponent<Projectile>();
```

## Performance Improvements

### Memory Allocations

**v2.x:**
- 1 closure per CreateEntity call (~24 bytes)
- 4 closures per AddComponent call (~96 bytes)
- For 10K entities with 3 components: ~2.9 MB/s allocations

**v3.0:**
- **Zero allocations** after warmup
- Pre-allocated arrays grow only once
- No closure allocations

### Execution Speed

| Operation | v2.x | v3.0 | Improvement |
|-----------|------|------|-------------|
| Tag bitwise ops | 4 scalar ops | 1 SIMD instruction | 4x faster |
| Archetype queries | O(N) scan | O(1) cached | 10-100x faster |
| Bounds checks | Normal | Unsafe (eliminated) | ~2x faster access |

### Iteration Safety

**v2.x:**
- Undefined behavior if modifying during iteration
- Could skip entities or iterate duplicates

**v3.0:**
- Tombstone pattern (-1) for safe modification
- Predictable behavior
- Fresh span retrieval handles array resize

## New Features in v3.0

### 1. Cross-Platform SIMD

Automatically uses:
- **AVX2** on x86/x64
- **AdvSimd (NEON)** on ARM
- **Scalar fallback** on other platforms

### 2. Extended Component Types Support

- Supports up to **65,536 component types** (ushort max)
- First 256 components: inline storage (32 bytes, 2x SIMD Vector128)
- Beyond 256: automatic overflow array
- Component index changed from byte to ushort in v3.0

### 3. Archetype Query Cache

```csharp
// First call: O(N) to build cache
foreach (var result in context.GroupOf<Position, Velocity>()) { }

// Subsequent calls: O(1) cached lookup
foreach (var result in context.GroupOf<Position, Velocity>()) { }
```

### 4. Manual Defragmentation

```csharp
// Check fragmentation
var (totalSlots, alive, ratio) = context.GetFragmentationStats();
Console.WriteLine($"Fragmentation: {ratio:P}");

// Compact during loading screens
context.CompactArchetypes();
```

### 5. Pre-Allocation

```csharp
var options = new Context.Options(
    parallel: true,
    levelOfParallelism: -1,  // Use all cores
    initialEntityCapacity: 10_000  // Pre-allocate for 10K entities
);
var context = new Context(options);

// Or grow later
context.EnsureEntityCapacity(100_000);
```

## Common Migration Patterns

### Pattern 1: Unit Tests

❌ **Old:**
```csharp
[Test]
public void TestEntityCreation()
{
    bool called = false;
    context.CreateEntity(entity => {
        called = true;
        Assert.IsTrue(entity.Id >= 0);
    });
    Assert.IsTrue(called);
}
```

✅ **New:**
```csharp
[Test]
public void TestEntityCreation()
{
    var entity = context.CreateEntity();
    Assert.GreaterOrEqual(entity.Value.Id, 0);
}
```

### Pattern 2: Entity Builders

❌ **Old:**
```csharp
public void SpawnEnemy(Vector2 pos)
{
    context.CreateEntity(entity => {
        entity.AddComponent<Position>(p => p.Value = pos);
        entity.AddComponent<Enemy>(e => e.Value.HP = 100);
        entity.AddComponent<AI>();
    });
}
```

✅ **New:**
```csharp
public EntityRef SpawnEnemy(Vector2 pos)
{
    var entity = context.CreateEntity();
    entity.AddComponent<Position>().Value = pos;
    entity.AddComponent<Enemy>().Value.HP = 100;
    entity.AddComponent<AI>();
    return entity;
}
```

### Pattern 3: Component Initialization

❌ **Old:**
```csharp
entity.AddComponent<Inventory>(inv => {
    inv.Value.Capacity = 20;
    inv.Value.Items = new List<Item>();
});
```

✅ **New:**
```csharp
var inv = entity.AddComponent<Inventory>();
inv.Value.Capacity = 20;
inv.Value.Items = new List<Item>();
```

### Pattern 4: Conditional Component Addition

❌ **Old:**
```csharp
context.CreateEntity(entity => {
    entity.AddComponent<Unit>();
    if (isPlayer)
    {
        entity.AddComponent<PlayerControlled>();
    }
});
```

✅ **New:**
```csharp
var entity = context.CreateEntity();
entity.AddComponent<Unit>();
if (isPlayer)
{
    entity.AddComponent<PlayerControlled>();
}
```

## Thread Safety

### v2.x
- Command queue thread-safe
- Callbacks execute on main thread at end of frame

### v3.0
- All operations thread-safe via .NET 8.0 Lock
- Entity ID allocation lock-free (Interlocked)
- Multiple threads can create/modify entities simultaneously

```csharp
// Safe in v3.0
Parallel.For(0, 1000, i => {
    var entity = context.CreateEntity();
    entity.AddComponent<Position>();
});
```

## Troubleshooting

### Issue: "Entity has been destroyed" Exception

**Cause:** You're using a stale EntityRef/ComponentRef after entity was destroyed.

**Solution:** Entity versioning now detects this. Don't cache refs across frames.

```csharp
// ❌ Bad: Caching EntityRef
var entity = context.CreateEntity();
// ... later ...
context.DestroyEntity(entity);
var pos = entity.AddComponent<Position>();  // Throws!

// ✅ Good: Get fresh reference
if (context.TryGetEntityById(id, out var entity))
{
    entity.AddComponent<Position>();
}
```

### Issue: Tests Failing After Migration

**Cause:** Tests using callback-based assertions.

**Solution:** Update to immediate API:

```csharp
// ❌ Old
context.CreateEntity(e => Assert.NotNull(e));

// ✅ New
var entity = context.CreateEntity();
Assert.NotNull(entity);
```

### Issue: Component Modifications Not Working

**Cause:** Copying component value to local variable (components are structs).

**Symptoms:** Changes to components don't persist, or crash when using `ref` with structural changes.

**Solution:** Use correct modification patterns:

```csharp
// ❌ Bad: Local copy
var health = entity.GetComponent<Health>();
var h = health.Value;  // COPY!
h.HP = 100;  // Lost!

// ✅ Good: Direct modification
var health = entity.GetComponent<Health>();
health.Value.HP = 100;

// ✅ Good: Using ref (only if NO entity creation)
var health = entity.GetComponent<Health>();
ref var h = ref health.Value;
h.HP = 100;
h.MaxHP = 150;
// Don't create entities while holding ref! (AddComponent/RemoveComponent/CompactArchetypes are safe)
```

See **Pattern 2b: Modifying Components** for detailed guidance.

### Issue: Crash or Corruption When Using `ref`

**Cause:** Holding `ref` to component while creating entities (which may resize arrays).

**Symptoms:** Access violations, wrong data, corrupted state after CreateEntity.

**Solution:** Don't hold `ref` across CreateEntity calls:

```csharp
// ❌ Crash: ref held during CreateEntity (array resize)
ref var health = ref entity.GetComponent<Health>().Value;
context.CreateEntity();  // May resize ALL arrays → ref invalid!
health.HP = 100;  // CRASH or wrong data!

// ✅ Safe: Use ComponentRef
var health = entity.GetComponent<Health>();
context.CreateEntity();  // Safe
health.Value.HP = 100;  // Safe: ComponentRef gets current location

// ✅ Also safe: AddComponent/RemoveComponent/CompactArchetypes with ref
ref var health = ref entity.GetComponent<Health>().Value;
entity.AddComponent<Armor>();     // Safe: doesn't resize component arrays
entity.RemoveComponent<Shield>(); // Safe: doesn't resize component arrays
context.CompactArchetypes();      // Safe: doesn't resize component arrays
health.HP = 100;  // Safe: component arrays weren't resized
```

**Rule:** Only use `ref` when no entity creation will occur. AddComponent/RemoveComponent/CompactArchetypes are safe with refs.

### Issue: Iteration Behavior Changed

**Cause:** v3.0 uses tombstone pattern for safe iteration.

**Effect:** Entities added/removed during iteration handled safely (tombstones skipped).

**Action:** Usually no changes needed, just be aware iteration is now safe.

## Performance Tips

### 1. Pre-Allocate Entity Capacity

```csharp
context.EnsureEntityCapacity(expected_max_entities);
```

### 2. Check Fragmentation and Compact Manually

Archetype fragmentation occurs when entities are destroyed, leaving "tombstone" slots. Use `CompactArchetypes()` during loading screens or maintenance windows:

```csharp
// Check fragmentation
var (totalSlots, aliveEntities, frag) = context.GetFragmentationStats();
if (frag > 0.5f)  // >50% tombstones
{
    // Compact during loading screen or between levels
    context.CompactArchetypes();
}
```

⚠️ **Warning:** `CompactArchetypes()` compacts archetype entity lists (removes tombstones), which affects `GroupOf` iterators but NOT component refs:

```csharp
// ❌ UNSAFE: GroupOf iterator invalidated by compaction
foreach (var result in context.GroupOf<Health>())
{
    context.CompactArchetypes();  // CRASH! Invalidates iterator!
    result.Component1.Value.HP = 100;  // Iterator is broken!
}

// ✅ SAFE: Component refs are unaffected by compaction
ref var health = ref player.GetComponent<Health>().Value;
context.CompactArchetypes();  // Safe: doesn't move component data
health.HP = 100;  // Safe: component arrays unchanged

// ✅ SAFE: Call CompactArchetypes outside iteration
foreach (var result in context.GroupOf<Health>())
{
    result.Component1.Value.HP = 100;
}
context.CompactArchetypes();  // Safe: no active iteration
```

**Key distinction:**
- `CreateEntity()` may resize component arrays → invalidates component `ref` variables
- `CompactArchetypes()` compacts archetype entity lists → invalidates `GroupOf` iterators
- Component refs are safe across `CompactArchetypes()`, but NOT across `CreateEntity()`

### 3. Use Batch Component Methods

Use `AddComponents` and `RemoveComponents` to reduce archetype transitions:

```csharp
// ✅ Efficient: Single archetype transition
var (pos, vel, health) = entity.AddComponents<Position, Velocity, Health>();
pos.Value.X = 10;

// ❌ Inefficient: 3 archetype transitions
entity.AddComponent<Position>();
entity.AddComponent<Velocity>();
entity.AddComponent<Health>();
```

### 4. Use Parallel Execution

```csharp
var options = new Context.Options(parallel: true);
var context = new Context(options);
```

### 5. Batch Entity Creation

```csharp
// Efficient: Lock acquired once per operation
for (int i = 0; i < 1000; i++)
{
    var entity = context.CreateEntity();
    entity.AddComponent<Position>();
}
```

### 6. Modify Components In-Place

```csharp
// ❌ Bad: Creates copy, modifications lost
foreach (var result in context.GroupOf<Position, Velocity>())
{
    var pos = result.Component1.Value;  // COPY!
    pos.X += 1;  // Modifying copy, not the actual component
}

// ✅ Good: Direct modification
foreach (var result in context.GroupOf<Position, Velocity>())
{
    result.Component1.Value.X += 1;
}

// ✅ Good: Using ref for multiple modifications (SAFE: no entity creation)
foreach (var result in context.GroupOf<Position, Velocity>())
{
    ref var pos = ref result.Component1.Value;
    pos.X += result.Component2.Value.X;
    pos.Y += result.Component2.Value.Y;
}

// ⚠️ UNSAFE: ref held during CreateEntity
var health = player.GetComponent<Health>();
ref var h = ref health.Value;
h.HP = 100;
// Spawn new enemies - may resize arrays!
for (int i = 0; i < 1000; i++)
{
    context.CreateEntity().AddComponent<Enemy>();
}
h.MaxHP = 150;  // CRASH! Arrays were resized, ref is invalid

// ✅ SAFE: Use ComponentRef across CreateEntity
var health = player.GetComponent<Health>();
health.Value.HP = 100;
// Spawn enemies - ComponentRef handles array changes
for (int i = 0; i < 1000; i++)
{
    context.CreateEntity().AddComponent<Enemy>();
}
health.Value.MaxHP = 150;  // Safe: ComponentRef gets current array location

// ✅ ALSO SAFE: ref with AddComponent/RemoveComponent/CompactArchetypes (no CreateEntity)
foreach (var result in context.GroupOf<Health>())
{
    ref var health = ref result.Component1.Value;
    if (health.HP <= 0)
    {
        // AddComponent/RemoveComponent/CompactArchetypes don't resize component arrays - safe with ref!
        result.Entity.AddComponent<Dead>();
        health.HP = 0;  // Safe: component arrays weren't resized
    }
}
```

**⚠️ Ref Safety Reminder:** Only use `ref` when you're certain **no entity creation** will occur while the ref is alive (CreateEntity may resize ALL component arrays). AddComponent/RemoveComponent/CompactArchetypes are safe with refs. See "Pattern 2b: Modifying Components" for full details.

## Summary

**Key Takeaways:**
- ✅ Remove all callbacks
- ✅ Use direct return values
- ✅ Everything executes immediately
- ✅ Thread-safe by default
- ✅ Zero allocations after warmup
- ✅ Simpler, cleaner API

**Benefits:**
- 🚀 Massive performance improvement
- 💾 Zero memory allocations
- 🧵 Thread-safe
- 🎯 Predictable behavior
- 📝 Cleaner code

For questions or issues, please file an issue on GitHub.
