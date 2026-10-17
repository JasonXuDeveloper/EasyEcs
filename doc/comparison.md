# Comparison with other ECS libraries

[Docs index](README.md) · Related: [Features](features.md) · [Performance guide](performance.md)

EasyEcs against the three most used .NET ECS libraries: [Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)
3.6.0, [Arch](https://github.com/genaray/Arch) 2.1.0 and [DefaultEcs](https://github.com/Doraku/DefaultEcs) 0.17.2.
The feature table comes from each package's public API and XML docs; the numbers come from the
`EasyEcs.Comparison` project, which ports the same work to each library through its recommended fast path and
checks that every library computes the same result.

- [Features](#features)
- [How changes are made](#how-changes-are-made)
- [The same server tick](#the-same-server-tick)
- [Operations](#operations)
- [Change tracking](#change-tracking)
- [Indexes and relations](#indexes-and-relations)
- [Their features in EasyEcs](#their-features-in-easyecs)
- [Which one to pick](#which-one-to-pick)

## Features

| Feature | EasyEcs | Friflo.Engine.ECS | Arch | DefaultEcs |
|---|---|---|---|---|
| Storage | archetype tables (SoA), presence bits per column | archetypes | archetypes in 16 KB chunks | a pool per component type |
| Structural changes inside a query loop | ✅ immediate, each entity visited once | ❌ throws `StructuralChangeException` (can be switched off); `CommandBuffer` | ⚠️ not guarded; `CommandBuffer` | ⚠️ documented unsafe; `EntityCommandRecorder` |
| Thread-safe command buffer | – (not needed: one owner thread, a queue you own) | ✅ `CommandBufferSynced` | ❌ | ❌ (not documented) |
| Tags | ✅ empty components; removing one flips a bit | ✅ `ITag` (part of the archetype) | ❌ | ❌ (enable / disable instead) |
| Singletons | ✅ `ISingletonComponent`, `[ForEach]` parameters | ⚠️ `UniqueEntity` | ❌ | ✅ `World.Set<T>` / `Get<T>` |
| Systems and scheduling | ✅ `EcsSystem`, `Priority`, groups, intervals | ✅ `SystemRoot`, `SystemGroup`, `QuerySystem` | ❌ (Arch.Extended) | ✅ `ISystem<T>`, `SequentialSystem`, `AEntitySetSystem` |
| Source-generated systems | ✅ `[ForEach]` → inlined struct jobs | ❌ | ❌ in core (Arch.Extended) | ❌ |
| Generated named API per component | ✅ `e.Position`, `HasX`, `SetX(…)`, `IsDead`, `world.Battle`, `world.ZoneIndex` (like Entitas) | ❌ | ❌ | ❌ |
| Compile-time analyzer | ✅ EECS001-EECS015 | ❌ | ❌ | ❌ |
| Chunk / span iteration | ✅ batch `[ForEach]` methods, `ForEachBatch` | ✅ chunks | ✅ chunks | – |
| Struct query jobs | ✅ `IForEach`, `IForEachEntity`, `IForEachBatch` | ✅ | ✅ `IForEach`, inline queries | – |
| Bulk create and destroy | ✅ `CreateEntities`, `DestroyEntities(filter)` | ✅ | ✅ | – |
| Parallel systems | ✅ non-conflicting systems of one priority, access generated | ❌ | ❌ | ✅ `ParallelSystem<T>` (you keep them independent) |
| Parallel query | ✅ `[ForEach(Parallel = true)]`, analyzer-checked | ✅ `RunParallel`, `ParallelJobRunner` | ✅ `ParallelQuery`, `JobScheduler` | ✅ `DefaultParallelRunner` |
| Change tracking (rows written since a tick) | ✅ `ITrackChanges`, `[Changed<T>]`, every `ref` write | ❌ (component add / remove events only) | ❌ (`OnComponentSet` events) | ✅ `WhenChanged<T>()` after `NotifyChanged<T>()` |
| Value index | ✅ `IIndexed<TKey>`, follows every `Set` (ref writes rejected at build time) | ✅ `IIndexedComponent<TKey>`, also range queries | ❌ | ✅ `EntityMap` / `EntityMultiMap` |
| Relations with reverse lookup | ✅ an `Entity` key | ✅ `ILinkComponent`, `IRelation<TKey>`, hierarchy | ❌ | ❌ |
| Per-system timing | ✅ `MeasureSystems`, `system.Stats` | ✅ system performance monitor | ❌ | – |
| Per-system error handling | ✅ `SystemError` | – | – | – |
| Events (added, removed, created) | – (state changes are data) | ✅ | ✅ | ✅ |
| In-memory snapshot of a world | ✅ `Capture` / `Restore` | ❌ (JSON serializer) | ❌ | ❌ (text and binary serializers) |
| Serialization | ❌ (your serializer) | ✅ JSON | ❌ | ✅ text, binary |
| Zero allocation per server tick | ✅ | ✅ | ❌ | ❌ |
| Native AOT | ✅ | – | – | – |
| Target frameworks | net10.0 | netstandard2.1, net6.0-8.0 | netstandard2.1, net6.0, net8.0 | netstandard1.1-2.1 |
| Unity | ❌ | ✅ | ✅ | ✅ |

✅ built in · ⚠️ partly or with caveats · ❌ not available · – not applicable or not checked

## How changes are made

The biggest difference is not speed but how a system changes the world. Friflo, Arch and DefaultEcs forbid (or do
not guard) structural changes while a query iterates, so a system records them in a command buffer, which is played
back later: until then, the entity still has its old components, and the order of effects depends on when buffers
are flushed. EasyEcs applies every change immediately, also in the middle of a pass over the same table, and still
visits each entity at most once:

```csharp
public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp)
    {
        if (hp.Value <= 0) unit.Add<Dead>();   // applied now: later systems already see the corpse
    }
}
```

The ports below follow each library's model (Friflo plays its command buffer back after every system that records
changes), so the measured worlds behave the same.

## The same server tick

`EcsComparisonBenchmark` runs the headline world of EasyEcs (30% players, 70% NPCs, projectiles; movement, AOI,
combat, status effects, death, respawn, shooting) in each library. Setup warms every world up and checks that its
counters (hits, deaths, spawns, AOI changes, entities) match an EasyEcs reference run within 5%.

| Library | 10,000 entities | 100,000 entities | Allocated per tick (100k) |
|---|---:|---:|---:|
| **EasyEcs** | 153 μs (1.00) | 1.81 ms (1.00) | 0 B |
| Friflo.Engine.ECS | 166 μs (1.08) | 1.84 ms (1.02) | 0 B |
| Arch | 442 μs (2.88) | 5.12 ms (2.84) | 535 KB |
| DefaultEcs | 291 μs (1.90) | 5.76 ms (3.19) | 61 KB |

<sub>BenchmarkDotNet, the repository's job (`BenchmarkConfig.cs`), .NET 10, 4-core Xeon VM at 2.1 GHz; ratio against EasyEcs in parentheses.</sub>

EasyEcs and Friflo are within a few percent (EasyEcs 2-8% ahead in this run; Friflo was up to 5% ahead in earlier
runs on this VM). Both allocate nothing per tick. Arch and DefaultEcs take 1.9-3.2x as long and allocate
every tick. EasyEcs is ahead in the systems that spawn, expire and toggle components; Friflo in the systems that pass
the entity to the job (see `Entity` below).

## Operations

`OperationBenchmark` isolates single operations on the headline world (100,000 entities), EasyEcs against Friflo:

| Operation | EasyEcs | Friflo | |
|---|---:|---:|---|
| Iterate one component | 38.6 μs | 58.3 μs | EasyEcs 1.51x faster |
| Iterate two components | 246 μs | 251 μs | about even |
| Two components, SIMD batches | 62.8 μs | 85.0 μs | EasyEcs 1.35x faster |
| Iterate three components | 66.4 μs | 68.3 μs | about even |
| Filtered (`With` a tag on a few entities) | 11.3 μs | 15.1 μs | EasyEcs 1.33x faster |
| Iterate with the entity handle, per entity | 48.5 μs | 62.7 μs | EasyEcs 1.29x faster |
| Iterate with the entity handle, in batches (`EntitySpan` / chunks) | 37.8 μs | 37.0 μs | about even |
| Iterate with a singleton | 210 μs | 225 μs | EasyEcs 1.07x faster |
| Toggle a status component (remove + add) | 548 μs | 980 μs | EasyEcs 1.79x faster |
| Create and destroy | 181 μs | 202 μs | EasyEcs 1.12x faster |

All rows allocate 0 B. Toggling is where tables with presence bits pay off: EasyEcs flips a bit and the entity stays
in its row, Friflo moves the entity to another archetype and back. A per-entity job receives a version-checked handle built
from the row's `EntitySlot` (id and version in one 8-byte load) on every row; a batch job's `EntitySpan` builds one only
for the rows it indexes, like Friflo's chunk entities.

## Change tracking

`ChangeTrackingComparison`: 100,000 entities, 10 or 1,000 of them get a new health per tick, and a system copies
health into a replica. Each library runs it its own way: the built-in feature where it has one (EasyEcs
`[Changed<T>]`, DefaultEcs `WhenChanged<T>()`), else a scan comparing values or a dirty tag added on write.

| Library, approach | 10 written | 1,000 written | Allocated |
|---|---:|---:|---:|
| **EasyEcs `[Changed<T>]`** | **0.57 μs** | **33.8 μs** | 0 B |
| EasyEcs, scan comparing values | 37.1 μs | 61.8 μs | 0 B |
| DefaultEcs `WhenChanged<T>()` | 37.0 μs | 71.0 μs | 0 B |
| Friflo, scan | 39.0 μs | 53.8 μs | 0 B |
| Friflo, dirty tag | 1.24 μs | 128 μs | 0 B |
| Arch, scan | 90.2 μs | 111 μs | 0 B |
| Arch, dirty tag | 0.96 μs | 83.4 μs | 0 B |

`[Changed<T>]` is the fastest at both rates and needs no code at the write site: a write through `ref` is the
change. A dirty tag is cheap when few entities change but moves each written entity between archetypes twice, so it
falls behind a plain scan at 1,000 writes; a scan costs the whole table every tick.

## Indexes and relations

`IndexComparison`: an inventory relation, 100,000 items each pointing at one of 1,000 owners; 1 or 100 items change
hands, then 100 inventories are counted. EasyEcs uses a relation (`IIndexed<Entity>`), Friflo a link component
(`ILinkComponent`, `GetIncomingLinks`), DefaultEcs an `EntityMultiMap`. Arch has no index.

| Library, approach | 1 trade | 100 trades | Allocated |
|---|---:|---:|---:|
| **EasyEcs** relation (`IIndexed<Entity>`) | 0.41 μs | 5.4 μs | 0 B |
| Friflo link component | 1.22 μs | 8.2 μs | 0 B |
| DefaultEcs `EntityMultiMap` | 0.79 μs | 9.6 μs | 0 B |

All three answer "which items does this owner hold" without a scan, and all three update the map on every set. EasyEcs
re-links the item inside `Set` (one inlined hook, gone at JIT time for components without an index) and keeps
relation heads in an array indexed by the owner's id, so neither a trade nor a count hashes anything.

## Their features in EasyEcs

What the other libraries offer, how EasyEcs does the same job, and where the
[battle server example](example.md) (`EasyEcs.Benchmark/BattleServer.cs`) shows it.
Some are built in under another name; some are done with data instead of a mechanism (EasyEcs has no events or
command buffers on purpose); a few are not offered.

| Their feature | In | EasyEcs | In the battle server |
|---|---|---|---|
| Command buffer for changes during a query | Friflo, Arch, DefaultEcs | not needed: add, remove, create and destroy apply at once, also mid-pass | `CombatSystem` adds statuses, `DeathSystem` adds `Dead`, `ProjectileSystem` spawns and expires |
| Thread-safe command buffer | Friflo | a queue you own, drained by a system on the world's thread ([threading](guide/threading.md)) | one world per thread in the usage code |
| Component added / removed / set events | Friflo, Arch, DefaultEcs | state changes are data: the change is a component that the next system queries | `DeathSystem` adds `Dead`, `RespawnSystem` queries `With<Dead>`; neither knows the other |
| Reactive sets (`WhenAdded`, `WhenChanged`) and `NotifyChanged` | DefaultEcs | `[Changed<T>]` and `QueryFilter.Changed<T>(since)`; a `ref` write is the change, nothing to notify | `ReplicationSystem`, `Unsaved` |
| Publish / subscribe messages | DefaultEcs | data: a singleton or components that systems read | `Battle` counters |
| Tags | Friflo | empty components; removing one flips a bit, the entity keeps its row | `Player`, `Npc`, `Dead`, `Frozen` |
| Unique entity / world-level component | Friflo, DefaultEcs | singletons, as `[ForEach]` parameters or a `SingletonRef<T>` | `Battle`, `Waypoints`, the host's `SingletonRef<Battle>` |
| Enable / disable an entity or component | Friflo, DefaultEcs | add or remove a tag (`Without<Frozen>` skips it); `system.Enabled` for systems | `Frozen`, a failing system switched off |
| Systems, groups, ordering | Friflo, DefaultEcs, Arch.Extended | `EcsSystem`, `Priority`, `ISystemGroup`, `UpdateInterval`, `OnCreate` / `OnDestroy` | every system; `Replication`; `SteeringSystem.OnCreate` |
| Struct query jobs (`IForEach`, inline queries) | Arch, Friflo | `IForEach`, `IForEachEntity`, `IForEachBatch` (`[ForEach]` generates them) | `Census` |
| Chunk iteration | Arch, Friflo | batch methods (`Span<T>` per run of rows) | `MovementSystem`, `ManaSystem`, `SumHealth` |
| Parallel query | Friflo, Arch, DefaultEcs | `[ForEach(Parallel = true)]`, `ForEachParallel`, `ForEachEntityParallel` | `SteeringSystem`, `NewRound` |
| Parallel systems | DefaultEcs | `WorldOptions.ParallelSystems`: systems of one priority whose generated access does not conflict | `MovementSystem` and `ManaSystem` |
| Bulk create and destroy | Friflo, Arch | `CreateEntities`, `DestroyEntities(filter)` | the players in the constructor, the projectiles in `NewRound` |
| Indexed components | Friflo, DefaultEcs | `IIndexed<TKey>`: `Find`, `TryFind`, `Count` (exact keys; no range queries) | `Zone`: `PlayersIn`, `Despawn` |
| Links and relations | Friflo | `IIndexed<Entity>`: the reverse lookup comes from the index | `OwnedBy`: a respawn recalls its projectiles |
| Parent / child hierarchy | Friflo | a relation to the parent (`Parent : IIndexed<Entity>`, children by `Find`); no built-in transform propagation | `OwnedBy` is such a link |
| Serialization | Friflo (JSON), DefaultEcs (text, binary) | in-memory `Capture` / `Restore`; for storage, your serializer over a `Query` | `Save` / `Load` |
| System performance monitor | Friflo | `WorldOptions.MeasureSystems`, `system.Stats` | `Slowest` |
| Error handling per system | – | `World.SystemError`: report, switch the system off, keep running | `OnSystemError` |
| Shared components (`SetSameAs`) | DefaultEcs | not built in: keep shared data in a singleton and an id into it in the component | – |
| Unity, older runtimes | all three | not offered: net10.0 only | – |

## Which one to pick

| Pick | When |
|---|---|
| **EasyEcs** | A .NET 10 server or simulation where state flips constantly and you want changes applied where they are decided, systems generated and checked at compile time, change tracking, indexes, relations and snapshots built in, and 0 B per tick. |
| **Friflo.Engine.ECS** | You need Unity or older runtimes, events, hierarchies or JSON serialization, and accept command buffers for changes during iteration. |
| **Arch** | You need Unity or older runtimes and a small core with chunk-based storage, adding systems and generators from Arch.Extended. |
| **DefaultEcs** | You need netstandard support down to 1.1, reactive sets (`WhenAdded`, `WhenChanged`) and built-in serializers, and the world is small enough that its per-tick allocations and slower iteration do not matter. |
