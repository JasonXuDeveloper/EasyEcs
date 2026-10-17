# Features

[Docs index](README.md) · Related: [Getting started](getting-started.md) ·
[Comparison with other ECS libraries](comparison.md) · [Performance guide](performance.md)

Every feature of EasyEcs on one page: what it does, when to use it (and when not), what it costs, and the server
scenarios it is built for. Each section links to its guide page. Numbers are BenchmarkDotNet results on a 4-core
Xeon VM at 2.1 GHz ([benchmarks](performance.md#benchmarks)).

| Feature | In one line | In the [battle server](example.md) |
|---|---|---|
| [Worlds and entities](#worlds-and-entities) | Isolated worlds of version-checked, recycled entity handles. | `new World(options)` in the host; `Entity` handles everywhere; `Hit` (`TryGetEntity`) |
| [Components and tags](#components-and-tags) | Plain structs in archetype tables; empty structs are tags. | `Position`, `Health`, …; tags `Player`, `Npc`, `Dead` |
| [Generated members](#generated-members) | `e.Position`, `e.SetZone(3)`, `e.IsDead`, `world.Battle`, `world.ZoneIndex` for every component. | `SetZone`, `SetFrozen`, `RemoveBurning`, `IsDead`, `World.Battle`, `ZoneIndex`, `OwnedByIndex` |
| [Immediate structural changes](#immediate-structural-changes) | Add, remove, create, destroy anywhere, even mid-pass; no command buffer. | `CombatSystem` adds statuses, `DeathSystem` adds `Dead`, `ProjectileSystem` spawns and destroys |
| [Systems and `[ForEach]`](#systems-and-foreach) | One-job systems; a method is the whole loop, compiled to an inlined struct job. | every system; `ClockSystem` overrides `OnUpdate` |
| [Filters](#filters) | `With`, `Without`, `WithAny`, `Changed` as attributes or a `QueryFilter`. | `With<Npc>`, `Without<Dead>`, `StatusSystem.Count` (`WithAny`), the host's `static readonly` filters |
| [Ad-hoc queries](#ad-hoc-queries) | `Query`, struct jobs, `Count`, `Any` from any code. | `NearestPlayer` (`Query`), `Census` (struct jobs, `Count`), `AnyoneBurning` (`Any`) |
| [Batch methods](#batch-methods) | Runs of rows as spans, for SIMD. | `MovementSystem`, `ManaSystem`, `SumHealth` |
| [System groups and intervals](#system-groups-and-intervals) | Separate cadences: input on every wake, simulation on a fixed tick. | `Replication` group, `RegenerationSystem.UpdateInterval`, `Ceasefire` (`Enabled`) |
| [Singletons](#singletons) | One value per world, passed to systems as parameters. | `Battle`, `Waypoints` (added in `OnCreate`), `SingletonRef<Battle>` in the host |
| [Parallel systems](#parallel-systems) | Non-conflicting systems of one priority run at once, same result as sequential. | `MovementSystem` and `ManaSystem` (`WorldOptions.ParallelSystems`) |
| [Parallel `[ForEach]`](#parallel-foreach) | One heavy method's rows split across cores. | `SteeringSystem`; `NewRound` (`ForEachParallel`, `ForEachEntityParallel`) |
| [Change tracking](#change-tracking) | Visit only the rows written since a system last ran. | `ReplicationSystem` (`[Changed<Health>]`), `Unsaved` (`ChangedSince`) |
| [Dynamic buffers](#dynamic-buffers) | A list per entity as a component; every write tracked. | players' bags of `Loot`; `ReplicationSystem.SendBag` (`[Changed<DynamicBuffer<Loot>>]`) |
| [Value indexes](#value-indexes) | Find entities by a component's value. | `Zone`: `PlayersIn`, `Despawn` (`TryFind`) |
| [Relations](#relations) | Entity-to-entity links with reverse lookup. | `OwnedBy`: `Respawn` recalls a player's projectiles |
| [Snapshots](#snapshots) | Copy a world and restore it in memory. | `Save` / `Load` |
| [Bulk create and destroy](#bulk-create-and-destroy) | Spawn and clear waves in one pass through the structural gate. | `CreateEntities` (players), `DestroyEntities` (`NewRound`) |
| [Per-system timing](#per-system-timing) | Run counts and times of every system. | `MeasureSystems`, `Slowest` |
| [Errors](#errors) | A failing system is reported and can be switched off; the tick goes on. | `SystemError` (`Failure`, `FaultySystem` in the tests) |
| [Analyzer](#analyzer) | ECS design mistakes fail the build. | the whole file builds without a warning |
| [Threading model](#threading-model) | One owner thread per world, many worlds per process. | one world per thread (`BattlesOnSeparateThreadsMatchOneRunAlone`); `SystemError` |
| [Native AOT](#native-aot) | No reflection on hot paths, nothing to configure. | nothing to change |

## Worlds and entities

**What it does.** A `World` owns entities, their components, singletons and systems; worlds share nothing. An
`Entity` is a 16-byte handle (world, id, version). Ids are recycled; every access checks the version, so a handle to a
destroyed entity throws instead of touching whatever reuses the id.

**When to use it.** One world per independent simulation: a map, a zone, a match, a test. Keep `Entity` handles in
components, singletons and outside code; never keep raw ids.

**Cost.** Create is O(n) amortized for n components (an entity with its components lands in its final table at
once); destroy is O(c) for c columns (the table's last row fills the hole).
Reading a component through a handle is one 16-byte record, one table lookup and one load: 3.4 μs per 1,000
random handles. Memory is bounded by the peak entity count; `WorldOptions.InitialEntityCapacity` and
`world.EnsureEntityCapacity(n)` pre-size the entity records for a known peak.

**Scenarios.** Long-running servers where entities come and go for months: ids never run out, stale references are
caught, nothing grows without bound.

Guide: [Entities and components](guide/entities-and-components.md).

## Components and tags

**What it does.** A component is a struct (`IComponent`), stored in archetype tables: one table per component set,
one column per component, one row per entity. Unmanaged columns are pinned and start on a 64-byte boundary. An empty
struct is a tag (`Dead`, `Stunned`), used only to filter. Removing a component only clears a presence bit: the entity
keeps its row, and adding the component back is just as cheap.

**When to use it.** Data per entity, split by what systems read together (`Position`, `Velocity`, `Health`). Tags for
states that come and go. A field, not a tag, for a value that changes every tick.

**When not.** Behavior or references to services in components: components are data; systems hold the behavior.

**Cost.** Iteration walks columns front to back and loads only the columns it reads. Toggling a status component on
10,000 entities (remove and add back) takes 183 μs, 0 B. Gaining a component the entity's table has no column for
copies its row once.

**Scenarios.** Buffs, debuffs, deaths and status effects that flip every few ticks on thousands of units.

Guide: [Entities and components](guide/entities-and-components.md).

## Generated members

**What it does.** The source generator gives every component named C# 14 extension members, like Entitas does for
its component classes: `e.Position` (`ref`), `HasPosition`, `TryGetPosition(out p)`, `SetPosition(x, y)` (from the
constructor), `RemovePosition()`; tags get `IsDead`, `SetDead()`, `RemoveDead()`; indexed components `world.ZoneIndex`;
singletons `world.Battle`, `HasBattle`, `SetBattle(…)`, `RemoveBattle()`. Tracked and indexed components get a
`ref readonly` property, so a read never counts as a write.

**When to use it.** Everywhere code names a concrete component: it reads like fields and methods and needs no type
arguments. The generic API stays for generic code.

**When not.** Nothing to opt out of; a name the entity or world already has (`Id`, `Version`, …) is skipped.

**Cost.** None: every member is an inlined call to the generic API. The analyzer and the generator know them
(`SetX` / `RemoveX` are structural changes, the rest reads), so a `[ForEach]` method keeps its fast path.

Guide: [Entities and components](guide/entities-and-components.md#generated-members).

## Immediate structural changes

**What it does.** Creating, destroying, adding and removing apply at once, also inside a pass over the same tables.
A pass visits each entity at most once; entities that leave before their turn are skipped, entities that join wait
for the next pass. The `ref` parameters of a `[ForEach]` method stay writable when the body moves its own entity.

**When to use it.** Always: write the change where the decision is made. Later code in the same pass and later
systems in the same update see it.

**Compared with command buffers.** Libraries that forbid changes during iteration record them in a buffer and play it
back later, so a system sees a state that is about to change, and the order of effects depends on when the buffer is
flushed. EasyEcs has no buffer to flush and no deferred state.

**Cost.** A pass that may change structure checks one counter after each entity; methods that cannot change
structure (static, no `World`, the `Entity` only read) are detected by the generator and skip even that.

Guide: [Structural changes during iteration](guide/structural-changes.md).

## Systems and `[ForEach]`

**What it does.** A system derives from `EcsSystem`, has one responsibility and no state. A `[ForEach]` method is the
whole loop: its parameters are the components (`ref` to write, `in` to read), singletons, the `Entity` and the
`World`. The source generator compiles each method into a struct job that the JIT inlines into the row loop: no
delegate, no virtual call per entity. Systems run in `Priority` order, registration order breaking ties.
`OnCreate` / `OnDestroy` set up and tear down what a system owns (its singletons); `AddSystem<T>()` or
`AddSystem(instance)` adds one, `RemoveSystem<T>()` removes it, `GetSystem<T>()` finds it for tooling and tests.

**When to use it.** All per-entity logic of a server tick.

**Cost.** A `[ForEach]` system adding `Velocity` to `Position` over 10,000 entities takes 8.2 μs
(against 10.7 μs for two plain arrays with no ECS). Per system and update: three virtual calls plus one
query-cache lookup per method.

**Scenarios.** Movement, AOI, combat, status effects, death, respawn, projectiles: the
[benchmark world](../EasyEcs.Benchmark/WorldSimulation.cs) is nine such systems.

Guide: [Systems, `[ForEach]` and groups](guide/systems.md) · [ECS principles](ecs-principles.md).

## Filters

**What it does.** `With<T>` requires a component that is not passed in, `Without<T>` excludes one, `WithAny<T>`
requires at least one of a group, `Changed<T>` selects rows written since a tick. As attributes on `[ForEach]`
methods or as a `QueryFilter`. A table that cannot match is skipped once; a table where some rows lack a component
is tested 64 rows per bit word.

**When to use it.** For states (`[Without<Dead>]`), roles (`[With<Player>]`) and alternatives
(`[WithAny<Frozen>, WithAny<Stunned>]`).

**Cost.** Matching is cached per query shape (one hash probe after the first use). Rows are tested 64 at a time.

Guide: [Queries and filters](guide/queries.md).

## Ad-hoc queries

**What it does.** Iteration outside systems: `foreach (var item in world.Query<T1..Tn>(filter))` (refs and the
`Entity`), struct jobs (`world.ForEach`, `ForEachEntity`), `world.Count`, `world.Any`. All allocation-free.

**When to use it.** Tools, tests, request handlers between updates, one-off logic. Prefer `[ForEach]` systems for
per-tick logic.

**Cost.** A `Query` step reads the row's id, plus its presence bits and change ticks only when the table needs testing
(28.9 μs over 10,000 entities in two tables, against 12.6 μs for a struct job). `Count` sums table counts; `Any` stops
at the first match.

Guide: [Queries and filters](guide/queries.md).

## Batch methods

**What it does.** A `[ForEach]` method whose component parameters are `Span<T>` / `ReadOnlySpan<T>` receives whole
runs of consecutive matching rows; a table whose rows all match is a single call. `world.ForEachBatch` does the same
for ad-hoc jobs.

**When to use it.** Arithmetic over many rows that you vectorize (`Vector<T>`, `MemoryMarshal.Cast`).

**When not.** Logic that changes structure or needs the entity (batch methods take only spans and singletons).

**Cost.** One call per run of rows instead of one per entity. Moving 100,000 entities with a bounce at the map's edge
takes 64 μs as a vectorized batch method, against 251 μs per entity (`OperationBenchmark`), 0 B.

Guide: [Systems: batch methods](guide/systems.md#batch-methods).

## System groups and intervals

**What it does.** `[UpdateInGroup<TGroup>]` puts a system in a group that runs only on `world.Update<TGroup>()`.
`UpdateInterval` runs a system every N-th update of its group; `Enabled` switches it off.

**When to use it.** Different cadences: input handling on every wake-up, simulation at a fixed tick, persistence every
few seconds.

**Cost.** None beyond the systems themselves; updating an empty group is a no-op.

Guide: [Systems: groups](guide/systems.md#system-groups).

## Singletons

**What it does.** One struct per world (`ISingletonComponent`): clock, configuration, counters, a spatial grid. A
`[ForEach]` method takes it as a `ref` / `in` parameter, resolved once per pass. `SingletonRef<T>` is a stable handle.

**When to use it.** Every piece of world-level state; systems keep none.

**Cost.** One lookup per pass for a parameter; 0.80 μs for 1,000 lookups.

Guide: [Singletons](guide/singletons.md).

## Parallel systems

**What it does.** With `WorldOptions.ParallelSystems`, systems of one priority whose access does not conflict run at the
same time. The access is generated from the `[ForEach]` parameters (`ref` writes, `in` reads); a system runs alone
when it can reach the world beyond its parameters: an `Entity` or `World` parameter, an instance `[ForEach]` method,
an `OnUpdate` override, or a subclass the generator did not see. The result always equals the sequential order.

**When to use it.** Several heavy, independent systems per priority and spare cores.

**When not.** Light systems over a few thousand entities: a batch costs a thread-pool hand-off (microseconds). For a
server, one world per thread usually uses the cores better.

**Cost.** Four disjoint systems over 100,000 entities: 394 μs sequential, 243 μs in parallel on 4 cores, 0 B.

Guide: [Threading: parallel systems](guide/threading.md#parallel-systems).

## Parallel `[ForEach]`

**What it does.** `[ForEach(Parallel = true)]` cuts one method's matching rows into chunks of 16,384 rows that the
calling thread and thread-pool threads run at once, allocation-free. The analyzer rejects methods that could race
(EECS010): structural changes, batch methods, singletons taken by `ref`. Outside systems, `world.ForEachParallel`
and `world.ForEachEntityParallel` run a struct job the same way.

**When to use it.** Heavy per-entity work over tens of thousands of entities: steering, path following, AI scoring.

**Cost.** Steering 100,000 entities towards the nearest of 16 waypoints: 1.60 ms on one thread, 0.77 ms with
`Parallel = true` on 4 cores.

Guide: [Systems: parallel methods](guide/systems.md#parallel-methods),
[Queries: on several threads](guide/queries.md#on-several-threads-foreachparallel-and-foreachentityparallel).

## Change tracking

**What it does.** A component marked `ITrackChanges` records the world's change tick of each row's last write (any
access that hands out a `ref`). A `[Changed<T>]` method visits only the entities whose `T` was written since its
system last ran; `QueryFilter.Changed<T>(tick)` does the same for queries.

**When to use it.** Work that follows the few entities that changed: replicating state to clients, saving dirty
entities, updating a spatial structure for the units that moved.

**When not.** Components written on every row every tick (nothing to skip), or reads taken by `ref` (every visited
row counts as written: take them `in`).

**Cost.** 8 bytes per row. A write stores its tick and updates its word's summary: a few nanoseconds through a handle
(about 5 μs for the summaries of 1,000 scattered writes), while a query or job writing row after row notes each word
once and a run of rows fills its ticks at once. A pass skips unwritten tables, 4,096-row blocks and 64-row words from
summaries and takes a word's written rows from its summary. Replicating 10 changed entities of 100,000 takes 0.55 μs
instead of 40-110 μs for a scan of every row; 1,000 scattered changes 36 μs instead of 60-110 μs.

Guide: [Change tracking](guide/change-tracking.md).

## Dynamic buffers

**What it does.** `DynamicBuffer<T>` is a list per entity, held as a component. Its writes (`Add`, `RemoveAt`, `Clear`,
`AsSpan`, …) take the buffer by `ref`, so a list changes only through a tracked write: `[Changed<DynamicBuffer<T>>]`
visits the entities whose list changed, and a write through `in` does not compile. Snapshots copy the elements.

**When to use it.** Lists that belong to one entity and are sent or saved when they change: a bag of item ids or
stacks, a path, recent events, pending orders.

**When not.** Items with their own state that are queried on their own (one entity per item with a relation), or a
small fixed set (an `[InlineArray]` in an unmanaged component, no GC at all).

**Cost.** One object and one array per entity, made by its first write; writes within the capacity allocate nothing.
A write stamps the row once, like any tracked component: 1,000 of 100,000 bags written and sent take 55.1 μs, against
61.5 μs for a `List<T>` in a tracked component (`DynamicBufferBenchmark`).

Guide: [Dynamic buffers](guide/dynamic-buffers.md).

## Value indexes

**What it does.** A component implementing `IIndexed<TKey>` is indexed by its `Key`:
`world.Index<T, TKey>().Find(key)`, `TryFind`, `Count`. `Set`, `CreateEntity` and `CreateEntities` re-index the
entity at once; destroying it or removing the component unlinks it. Writes through a `ref`, which the index could not
see, are rejected by the analyzer (EECS015).

**When to use it.** Lookups by value: a player by name, the units of a guild, the entities in a grid cell you key by.

**When not.** Values that change on most rows every tick and are looked up rarely: every change is a `Set` and a
re-link, where a scan at lookup time may cost less.

**Cost.** `Count` is a hash probe, `Find` one step per match; a `Set` that changes the key unlinks and links the
entity (a few array writes). One node per indexed entity.

Guide: [Indexes and relations](guide/indexes-and-relations.md).

## Relations

**What it does.** An indexed component whose key is an `Entity` is a relation: `ChildOf(Entity Parent)` on each child,
`index.Find(parent)` for the children. Relation heads are an array indexed by the target's id (no hashing), and
handles carry their version, so a link to a destroyed entity never matches a later one that reuses its id.

**When to use it.** Ownership and hierarchy: inventories whose items have state of their own (durability,
enchantments: each item an entity pointing at its owner), turrets on a ship, members of a party, summoned units of a
caster.

**When not.** A bag of plain values that only its owner reads (item ids, stack counts): a
[dynamic buffer](#dynamic-buffers) is one component instead of an entity per item. Many-to-many links (one component
holds one key: give each link its own entity), and transform hierarchies that need propagation (not built in).

**Cost.** 100 items changing hands among 100,000, then 100 inventories counted: 5.3 μs.

Guide: [Indexes and relations](guide/indexes-and-relations.md#relations).

## Snapshots

**What it does.** `world.Capture()` copies entities, components and singletons into a `WorldSnapshot`;
`world.Restore(snapshot)` puts them back, any number of times. Handles of the snapshot are valid again.

**When to use it.** Undo a turn, rewind for client-side prediction or replays, reset a world between tests.

**When not.** Persistence: a snapshot lives in memory and shares managed references; write components out with your
own serializer.

**Cost.** O(entities × components) both ways: for 100,000 entities in two tables, a capture takes 0.72 ms (2.8 MB,
the new snapshot) and a restore 0.43 ms (0 B); fine every few ticks for tens of thousands of entities.

Guide: [Snapshots](guide/snapshots.md).

## Bulk create and destroy

**What it does.** `world.CreateEntities(span, a, b)` spawns a wave with the same values in one pass through the
structural gate; `world.DestroyEntities<T>(filter)` destroys every match.

**When to use it.** Waves of monsters, projectiles, loot; clearing a zone.

**Cost.** Ten rounds of creating and destroying 10,000 entities: 162 μs in batches against 381 μs one by one.

Guide: [Entities and components](guide/entities-and-components.md#entities).

## Per-system timing

**What it does.** With `WorldOptions.MeasureSystems`, every run of a system is timed: `system.Stats` holds `Runs`,
`Last`, `Total`, `Average`.

**When to use it.** A diagnostics endpoint or log line on a live server, to see which system grows.

**Cost.** Two timestamp reads per system run when on; nothing when off.

Guide: [Systems: measuring systems](guide/systems.md#measuring-systems).

## Errors

**What it does.** `world.SystemError` receives every exception a system throws (and `OnDestroy` errors during
`Dispose`); the update goes on with the next system. Without a subscriber the exception propagates out of `Update()`.
A failed run still counts as run, so `[Changed<T>]` does not deliver the same rows again.

**When to use it.** Every live server: log the failure, switch the system off (`Enabled = false`), keep the world
ticking.

**Cost.** Nothing until a system throws.

Guide: [Systems: errors](guide/systems.md#errors).

## Analyzer

**What it does.** The package bundles an analyzer (EECS001-EECS015) next to the generator: invalid `[ForEach]`
methods and systems (EECS001-005), class components (EECS006), stateful systems (EECS007), systems referencing
systems (EECS008), batch methods mixing spans with per-entity parameters (EECS009), unsafe parallel methods (EECS010),
writes through `in` parameters (EECS011), copied dynamic buffers (EECS012) and structural changes off the owner thread
(EECS013, EECS014) fail the build.

**When to use it.** Always on; it keeps a large code base decoupled.

Guide: [Analyzer rules](guide/analyzer.md).

## Threading model

**What it does.** A world has one owner thread: the one calling `Update`. Structural changes check a plain owner flag
(no atomics): one made inside a parallel batch or a parallel `[ForEach]`, or reentered, throws instead of tearing the
tables, and the analyzer reports the off-thread changes it can see (EECS013, EECS014). Queries match lock-free over
copy-on-write snapshots. `ParallelSystems` borrows thread-pool threads inside one update without changing ownership.

**When to use it.** One world per thread (one per map or zone): no synchronization at all. Input from other threads
goes through a queue you own, drained by a system.

**Cost.** Four worlds of 25,000 entities on four threads tick in 1.08 ms, against 1.89 ms for one world of 100,000.

Guide: [Threading](guide/threading.md).

## Native AOT

**What it does.** Systems, jobs and filters are generated at compile time; nothing on the hot paths uses reflection.
EasyEcs works under Native AOT and trimming with nothing to configure.

**When to use it.** Small, fast-starting server containers.

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>   <!-- nothing else to configure for EasyEcs -->
</PropertyGroup>
```
