# EasyEcs

**EasyEcs is a high-performance archetype Entity Component System (ECS) for C# and .NET 10,
for anything that updates many entities every frame or tick: game servers, Godot 4 games, simulations and real-time
backends.** It runs a [Minecraft-style server](https://ecs.xgamedev.net/en/docs/samples/voxel-server) (chunks, flowing
water, zombies, replication) and a [Godot 4 game](https://ecs.xgamedev.net/en/docs/samples/godot) the same way. A warmed-up world of 100,000 entities runs nine simulation
systems in 1.72 ms with 0 B allocated. Across 37 head-to-head benchmarks against Friflo.Engine.ECS, Arch and
DefaultEcs, EasyEcs leads in 24, is within measurement error in 11 and trails in 2
([full comparison](https://ecs.xgamedev.net/en/docs/comparison#feature-by-feature)). Write plain structs and
`[ForEach]` methods; a source generator builds the iteration jobs and an analyzer catches mistakes at compile time.

**Documentation: [ecs.xgamedev.net](https://ecs.xgamedev.net)** (English · [中文](https://ecs.xgamedev.net/zh))

[![NuGet](https://img.shields.io/nuget/v/EasyEcs?logo=nuget&color=004880)](https://www.nuget.org/packages/EasyEcs)
[![Downloads](https://img.shields.io/nuget/dt/EasyEcs?logo=nuget)](https://www.nuget.org/packages/EasyEcs)
[![CI](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/EasyEcs/ci.yml?branch=master&logo=github&label=CI)](https://github.com/JasonXuDeveloper/EasyEcs/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/github/license/JasonXuDeveloper/EasyEcs)](https://github.com/JasonXuDeveloper/EasyEcs/blob/master/LICENSE)

**Try it in your browser:** set the [live particle demo](https://ecs.xgamedev.net/en/docs/demo) to **100k**, then
move your pointer across the particles. The demo's C# runs through Mono AOT to WebAssembly; its batched drag and
movement systems took about 0.1 ms on an M4 during this capture. The rest of the systems and rendering are separate
from that reading. The [batch system source](demo/EasyEcs.Demo/Systems/MovementBatchSystem.cs) shows the
`[ForEach]` + Span + SIMD path.

<a href="https://ecs.xgamedev.net/en/docs/demo"><img src="docs/public/demo-vortex-100k.png" alt="EasyEcs browser demo with 100k particles forming a vortex and live ECS timings" width="560"></a>

One server tick of 9 systems (movement, AOI, combat, status effects, death, respawn, projectiles):

| Tick | Entities | Time | Allocated |
|---|---:|---:|---:|
| One world | 100,000 | **1.72&nbsp;ms** | **0 B** |
| Four worlds, one per thread | 100,000 | **0.92&nbsp;ms** | **0 B** |
| [Battle server](https://ecs.xgamedev.net/docs/example), every feature | 100,000 | **2.97&nbsp;ms** | **0 B** |

<sub>BenchmarkDotNet 0.15.2 (`EasyEcs.Benchmark`), 4-core Intel Xeon VM at 2.1 GHz (Ubuntu 24.04), .NET 10.0.13; entities spawn, die, gain and lose components every tick.
The battle server turns every feature on: parallel steering and systems, replication, indexes, relations, timings.
A `[ForEach]` system adding `Velocity` to `Position` costs 0.9 ns per entity (one archetype), faster than two
plain arrays.
[Details](https://ecs.xgamedev.net/docs/performance#benchmarks).</sub>

```bash
dotnet add package EasyEcs        # net10.0 · source generator, analyzer and Nino included
```

## Why EasyEcs

- **Fast and garbage-free.** Archetype tables with 64-byte-aligned columns that iteration and SIMD walk front to back,
  16-byte entity records (4 per cache line), JIT-inlined struct jobs instead of delegates, lock-free queries. After warm-up nothing allocates: a unit test guards the 0 B
  budget of updates, queries and structural changes on every run.
- **Structural changes are immediate and cheap.** Add and remove components, spawn and destroy, even in the middle of
  a pass; no command buffer to flush. Removing a status effect and adding it back flips a presence bit, the entity
  never moves. State can be tags, not flags.
- **Write the logic, not the plumbing.** A `[ForEach]` method is the whole system; filters are attributes
  (`[With<T>]`, `[Without<T>]`, `[WithAny<T>]`), singletons are parameters, and attributes on a type declare what it is
  (`[Sum]` for a total, `[UniqueKey]` for an index key, `[Group]`, `[UpdateInterval(n)]` on a system): the generator
  writes the rest.
- **Every core and every SIMD lane, without writing either.** A `[ForEach]` method over numbers becomes a vector loop,
  the way Burst compiles a job, with the same results bit for bit. `Parallel = true` spreads it over the cores, and its
  parameters do the work that usually needs locks: `Reduce<T>` for totals, `Emit<T>` for records out of a pass,
  `Lookup<T>` for other entities, `Scratch<T>` for working memory, `EntityRandom` for random numbers that do not depend
  on the thread count. A grid (water, light, heat) is shaded like a GPU draws: tiles, a halo, a stencil.
- **Built-in change tracking, removal detection, indexes and relations.** `[Changed<T>]` visits only the rows written
  since a system last ran, including writes through `ref` (or only the rows whose value changed, through `Mut<T>`),
  `[ChangedAny<A, B>]` those where any of several changed, `[Removed<T>]` the entities that lost a component, and a
  destroy log the entities destroyed since each client's tick;
  `IIndexed<TKey>` components are found by value, and an `IIndexed<EntityId>` component or an `IRelation` buffer
  (`Link`) makes a relation, kept current by every `Set`.
  No events and nothing to keep in sync by hand.
- **Prefabs that stay out of your queries.** A `Prefab` is a recipe of component values that belongs to no world:
  `world.Instantiate(prefab)` spawns from it (a whole wave in one pass), and no system ever has to filter templates
  out. `entity.Clone()` copies a living entity.
- **Mistakes fail the build, slow code gets a hint.** The bundled analyzer (EECS002-EECS089, with code fixes) reports
  invalid `[ForEach]` methods and systems, class components, stateful systems, systems that reference each other,
  parallel code that would race, writes through read-only parameters, copied dynamic buffers, structural changes off
  the owner thread, indexed components written behind their index's back and ordering cycles. It also names the code
  that works but costs: a method that lost its vector loop and why, per-row lookups, allocations per update, a
  hand-written total, key or job that an attribute or a parameter declares.
- **Decoupled by construction.** Systems cooperate only through data, in registration order unless `[UpdateBefore<T>]` /
  `[UpdateAfter<T>]` say otherwise; system groups give you
  separate cadences (input on every wake, simulation on a fixed tick).
- **Built for processes that run for months.** Ids are recycled behind version-checked handles, memory is bounded by
  the peak entity count, a structural change off the owner thread throws or is reported at compile time instead of
  tearing the tables; the supported model is one owner thread per world (see [Threading](https://ecs.xgamedev.net/docs/guide/threading)),
  and one world per thread scales across cores.

## Features

Every feature has a guide page with a full example; [Features](https://ecs.xgamedev.net/docs/features) lists them all
with what each costs and when to use it, and [API and complexity](https://ecs.xgamedev.net/docs/api) lists every call.

- **Entities and data**: [worlds, entities, components and tags](https://ecs.xgamedev.net/docs/guide/entities-and-components),
  [prefabs and cloning](https://ecs.xgamedev.net/docs/guide/prefabs), [singletons](https://ecs.xgamedev.net/docs/guide/singletons),
  [dynamic buffers](https://ecs.xgamedev.net/docs/guide/dynamic-buffers), [immediate structural changes](https://ecs.xgamedev.net/docs/guide/structural-changes),
  [bulk changes](https://ecs.xgamedev.net/docs/guide/bulk), [cleanup components and disable](https://ecs.xgamedev.net/docs/guide/cleanup-and-disable).
- **Systems and queries**: [systems](https://ecs.xgamedev.net/docs/guide/systems) of `[ForEach]` methods with filters, `Maybe<T>`,
  batch methods, slices, `[UpdateInterval]`, `[Consume]`, groups, ordering and generic systems;
  [ad-hoc queries and struct jobs](https://ecs.xgamedev.net/docs/guide/queries), `[ForEntity]` for one entity.
- **Compute**: [vector loops, parallel passes and batch methods](https://ecs.xgamedev.net/docs/guide/high-performance);
  [reductions, scratch, lookups, emitted records, deterministic random numbers](https://ecs.xgamedev.net/docs/guide/threading#reductions);
  [grids shaded by tiles](https://ecs.xgamedev.net/docs/guide/grids).
- **[Change tracking](https://ecs.xgamedev.net/docs/guide/change-tracking)**: `[Changed<T>]`, `[ChangedAny<..>]`, `Mut<T>`,
  `[Removed<T>]` and the destroy log.
- **Finding entities**: [value and unique indexes, ordered ranges, relations](https://ecs.xgamedev.net/docs/guide/indexes-and-relations),
  [hierarchy](https://ecs.xgamedev.net/docs/guide/hierarchy), [table groups](https://ecs.xgamedev.net/docs/guide/groups).
- **State over time**: [snapshots](https://ecs.xgamedev.net/docs/guide/snapshots), [saves and restarts](https://ecs.xgamedev.net/docs/guide/saves),
  [migration, records and plugin unloading](https://ecs.xgamedev.net/docs/guide/migration-and-unload).
- **[Threads](https://ecs.xgamedev.net/docs/guide/threading)**: one owner thread per world, parallel systems and parallel
  `[ForEach]` methods, Native AOT.
- **Tools**: [per-system timing, schedule, introspection, trimming](https://ecs.xgamedev.net/docs/guide/diagnostics),
  [the analyzer](https://ecs.xgamedev.net/docs/guide/analyzer) (EECS002-EECS089, with code fixes).

## Example

Data is plain structs, each system is one `[ForEach]` method, and systems cooperate only through the data they write:

```csharp
using EasyEcs;

public record struct Position(float X, float Y) : IComponent;
public record struct Velocity(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent, ITracked;         // tracked: rows record their last write
public struct Dead : IComponent;                                       // a tag
public record struct Clock(float Dt) : ISingletonComponent;           // one value per world

[EcsSystem]
public ref struct MovementSystem
{
    [ForEach, Without<Dead>]                                           // in reads, ref writes
    static void Move(ref Position p, in Velocity v, in Clock clock)
    {
        p.X += v.X * clock.Dt;
        p.Y += v.Y * clock.Dt;
    }
}

[EcsSystem, UpdateAfter<MovementSystem>]                               // registration order otherwise
public ref struct DeathSystem
{
    [ForEach, Without<Dead>, Changed<Health>]                          // only healths written since its last run
    static void Die(Entity unit, in Health hp)
    {
        if (hp.Value <= 0) unit.SetDead();                             // immediate: later systems see it this tick
    }
}
```

The host owns the world and ticks it:

```csharp
using var world = new World();
world.SetClock(1 / 30f);                                               // singletons before the first update
world.AddSystem<MovementSystem>();
world.AddSystem<DeathSystem>();

var hero = world.CreateEntity(new Position(0, 0), new Velocity(1, 0), new Health(100));
world.Update();                                                        // 0 B per update once warmed up
hero.SetHealth(0);                                                     // a tracked write: DeathSystem sees it
world.Update();
System.Console.WriteLine(hero.IsDead);                                 // True
```

[A full battle server](https://ecs.xgamedev.net/docs/example) uses every feature
(parallel steering, replication by change tracking and removal detection, NPC kinds as prefabs, bags of loot in
dynamic buffers, indexes, relations, snapshots, system groups, struct jobs) in one world; it is the battle server benchmark below and the world of the scenario tests. The step-by-step tutorial is
[Getting started](https://ecs.xgamedev.net/docs/getting-started).

## When to use it

| Good fit | Probably not |
|---|---|
| Game servers and simulations on .NET 10: MMO maps and zones, AOI, combat, voxel worlds, grids of water or heat | Unity or other runtimes below .NET 10 (`net10.0` only; Unity support is [being evaluated](https://ecs.xgamedev.net/en/docs/roadmap#unity)) |
| Godot 4 .NET games: the project targets `net10.0` ([sample](samples/Godot)) | |
| Worlds where state flips constantly (buffs, deaths, spawns): structural changes are immediate and cheap | Code that needs structural changes recorded from many threads at once (use one owner thread and a queue) |
| Long-running processes where GC pauses and unbounded growth matter | Code that wants objects with behavior, or systems calling each other |
| One world per thread, many worlds per process; `ParallelSystems` for heavy independent systems | Many threads mutating one world's component values without coordination |

## Performance

BenchmarkDotNet 0.15.2 with the repository's job (`BenchmarkConfig.cs`), 4-core Intel Xeon VM at 2.1 GHz (Ubuntu 24.04), .NET 10.0.13, project `EasyEcs.Benchmark`. The headline benchmark is a realistic server tick,
not a toy loop ([what it simulates](https://ecs.xgamedev.net/docs/performance#benchmarks)). "Allocated" is per tick.

| Benchmark | Entities | Mean | Allocated |
|---|---:|---:|---:|
| WorldSimulationBenchmark.Tick | 10,000 | 158 μs | 0 B |
| WorldSimulationBenchmark.Tick | 100,000 | 1.72 ms | 0 B |
| MultiWorldBenchmark.TickAllWorlds (4 worlds × 25k) | 100,000 | 0.92 ms | 0 B |
| BattleServerBenchmark.Tick (the [battle server example](https://ecs.xgamedev.net/docs/example), every feature) | 100,000 | 2.97 ms | 0 B |

More benchmarks, the micro benchmarks and how to run them are in [Performance](https://ecs.xgamedev.net/docs/performance):

```bash
dotnet run -c Release --project benchmarks/EasyEcs.Benchmark -- --filter '*'
```

## Compared with other ECS libraries

37 head-to-head benchmarks (project `EasyEcs.Comparison`, same VM) port the same work to Friflo.Engine.ECS 3.6, Arch
2.1 and DefaultEcs 0.17, each library its own idiomatic way, and check in setup that every library computes the same
result. EasyEcs is faster in 24, within the measurement error in 11 and slower in 2. "Its time vs EasyEcs" is the
time of the fastest other library over the time of EasyEcs (2× = it takes twice as long); the site sorts the same
data live ([feature by feature](https://ecs.xgamedev.net/docs/comparison#feature-by-feature)).

**Clear leads**, where the next-fastest library takes at least 1.25 times as long:

| What is measured | EasyEcs | Next fastest | Its time vs EasyEcs |
|---|---:|---|---:|
| [Save 10,000 entities and load them back](https://ecs.xgamedev.net/docs/comparison#saves) | 146 μs | DefaultEcs 4.33 ms, 4.37 MB allocated | **30×** |
| [Add a component to 100,000 entities by query, then remove it](https://ecs.xgamedev.net/docs/comparison#bulk) | 78.7 μs | Arch 1.21 ms | **15×** |
| [Spawn 10,000 entities beside 100,000, then destroy them](https://ecs.xgamedev.net/docs/comparison#bulk) | 138 μs | Friflo.Engine.ECS 380 μs | **2.75×** |
| [Query 100,000 players by value: a rating range, zone sums](https://ecs.xgamedev.net/docs/comparison#indexes-and-relations) | 127 μs | Friflo.Engine.ECS 317 μs | **2.49×** |
| [Create 1,000 parent-child squads, destroy them in cascade](https://ecs.xgamedev.net/docs/comparison#hierarchy) | 452 μs | Friflo.Engine.ECS 1.06 ms | **2.35×** |
| [Count 100 inventories through an index (1 item traded)](https://ecs.xgamedev.net/docs/comparison#indexes-and-relations) | 425 ns | DefaultEcs 838 ns | **1.97×** |
| [Add a status component to 5% of 100,000 entities, then remove it](https://ecs.xgamedev.net/docs/comparison#operations) | 517 μs | Friflo.Engine.ECS 1.00 ms | **1.94×** |
| [Find the 10 of 100,000 entities written this tick](https://ecs.xgamedev.net/docs/comparison#change-tracking) | 507 ns | Arch 966 ns | **1.91×** |
| [Spawn 5,000 entities from a prefab, then destroy them](https://ecs.xgamedev.net/docs/comparison#operations) | 164 μs | Friflo.Engine.ECS 276 μs | **1.68×** |
| [Create 100,000 three-component entities one by one in a fresh world, room reserved first](https://ecs.xgamedev.net/docs/comparison#cold-create) | 2.60 ms | Arch 4.21 ms | **1.62×** |
| [Iterate one component over 100,000 entities](https://ecs.xgamedev.net/docs/comparison#operations) | 37.7 μs | Friflo.Engine.ECS 59.9 μs | **1.59×** |
| [Put 10,000 of 100,000 entities to sleep, wake the last ones](https://ecs.xgamedev.net/docs/comparison#disabling) | 478 μs | DefaultEcs 745 μs | **1.56×** |
| [Create 100,000 three-component entities in a fresh world in one call (the others' fastest way)](https://ecs.xgamedev.net/docs/comparison#cold-create) | 2.71 ms | Arch 4.21 ms | **1.56×** |
| [Count 100 inventories through an index (100 items traded)](https://ecs.xgamedev.net/docs/comparison#indexes-and-relations) | 5.11 μs | DefaultEcs 7.87 μs | **1.54×** |
| [Iterate with a filter: the players among 100,000 entities](https://ecs.xgamedev.net/docs/comparison#operations) | 11.3 μs | Friflo.Engine.ECS 16.6 μs | **1.46×** |
| [Find the 1,000 of 100,000 entities written this tick](https://ecs.xgamedev.net/docs/comparison#change-tracking) | 38.5 μs | Friflo.Engine.ECS 53.7 μs | **1.40×** |
| [Iterate with the entity handle of each row](https://ecs.xgamedev.net/docs/comparison#operations) | 49.0 μs | Friflo.Engine.ECS 67.2 μs | **1.37×** |

<details>
<summary><b>The 13 close results and the 7 where another library is faster</b>, with the reason for each</summary>

**Close**, EasyEcs ahead by less than that. The full server tick is one of these: most of a tick is iteration over
dense columns, bound by memory more than by the library, and Arch and DefaultEcs take 3.42× and 3.71× as long there
and allocate 535 KB and 61 KB per tick (EasyEcs and Friflo: 0 B):

| What is measured | EasyEcs | Next fastest | Its time vs EasyEcs |
|---|---:|---|---:|
| [Four independent systems over 100,000 entities, one after another](https://ecs.xgamedev.net/docs/comparison#parallel) | 395 μs | DefaultEcs 471 μs | 1.19× |
| [Rewire 100 of 10,000 friend lists, walk 100 follower sets](https://ecs.xgamedev.net/docs/comparison#indexes-and-relations) | 22.0 μs | Friflo.Engine.ECS 26.1 μs | 1.19× |
| [Move 100,000 entities with SIMD over spans](https://ecs.xgamedev.net/docs/comparison#operations) | 69.5 μs | Friflo.Engine.ECS 80.7 μs | 1.16× |
| [Create 5,000 entities one by one, then destroy them](https://ecs.xgamedev.net/docs/comparison#operations) | 190 μs | Friflo.Engine.ECS 215 μs | 1.13× |
| [Add and remove a tag in the passes that find them (1,000 of 100,000)](https://ecs.xgamedev.net/docs/comparison#structural-changes) | 163 μs | Friflo.Engine.ECS 183 μs | 1.12× |
| [Put 100 of 100,000 entities to sleep, wake the last ones](https://ecs.xgamedev.net/docs/comparison#disabling) | 41.4 μs | Friflo.Engine.ECS 45.9 μs | 1.11× |
| [Four independent systems over 100,000 entities, in parallel](https://ecs.xgamedev.net/docs/comparison#parallel) | 370 μs | DefaultEcs 405 μs | 1.09× (within error) |
| [The full server tick: nine systems over 100,000 entities](https://ecs.xgamedev.net/docs/comparison#the-same-server-tick) | 1.68 ms | Friflo.Engine.ECS 1.83 ms | 1.08× |
| [Find the 1,000 of 100,000 entities that lost a component](https://ecs.xgamedev.net/docs/comparison#removal-detection) | 66.7 μs | Friflo.Engine.ECS 71.7 μs | 1.08× (within error) |
| [The full server tick: nine systems over 10,000 entities](https://ecs.xgamedev.net/docs/comparison#the-same-server-tick) | 175 μs | Friflo.Engine.ECS 183 μs | 1.05× (within error) |
| [Iterate two components and a singleton](https://ecs.xgamedev.net/docs/comparison#operations) | 212 μs | Friflo.Engine.ECS 222 μs | 1.05× (within error) |
| [Scan 100,000 entities for the 10 written, without a change filter](https://ecs.xgamedev.net/docs/comparison#change-tracking) | 38.2 μs | Friflo.Engine.ECS 39.9 μs | 1.04× (within error) |
| [Move 100,000 entities: two components, per entity](https://ecs.xgamedev.net/docs/comparison#operations) | 253 μs | Friflo.Engine.ECS 257 μs | 1.02× (within error) |

**Where another library is faster**, with the reason from the benchmark's source:

| What is measured | EasyEcs | Faster library | Its time vs EasyEcs | Why |
|---|---:|---|---:|---|
| [Iterate three components over 100,000 entities](https://ecs.xgamedev.net/docs/comparison#operations) | 71.0 μs | Friflo.Engine.ECS 70.7 μs | 0.99× (within error) | The same span loop over the same dense columns in both libraries: the loop is bound by memory, not by the library. |
| [Add and remove a tag in the passes that find them (10 of 100,000)](https://ecs.xgamedev.net/docs/comparison#structural-changes) | 78.2 μs | Friflo.Engine.ECS 75.4 μs | 0.96× (within error) | Friflo defers the changes to its command buffer, so both of its passes are plain span loops. A `[ForEach]` that may change structure checks after every row that its table did not move, and over 100,000 rows that costs more than 10 deferred changes; from about 1,000 changes on, applying them at once is faster. |
| [Iterate in batches, reading entity ids only where needed](https://ecs.xgamedev.net/docs/comparison#operations) | 38.5 μs | Friflo.Engine.ECS 37.0 μs | 0.96× (within error) | The same loop over the same rows: Friflo's is written inline in the benchmark, EasyEcs' is generated from a batch `[ForEach]` method and runs as a system group. |
| [Steer 100,000 agents on all cores (parallel query)](https://ecs.xgamedev.net/docs/comparison#parallel) | 766 μs | Arch 685 μs | 0.89× (within error) | Arch's and Friflo's runners keep worker threads of their own, which win when passes run back to back, as BenchmarkDotNet runs them. EasyEcs wakes thread-pool helpers only while they pay: in a real server tick a dedicated crew measured worse tail latency and more CPU per tick (D22). |
| [Scan 100,000 entities for the 1,000 written, without a change filter](https://ecs.xgamedev.net/docs/comparison#change-tracking) | 61.4 μs | Friflo.Engine.ECS 53.7 μs | 0.88× | Not the way to do it in EasyEcs: the component is `ITracked`, so each of the 1,000 writes also stamps its row's change tick, which `[Changed<T>]` needs and the scan ignores; Friflo's component is not tracked. `[Changed<T>]` does the same job faster than every library's scan. |
| [Find the 10 of 100,000 entities that lost a component](https://ecs.xgamedev.net/docs/comparison#removal-detection) | 830 ns | Friflo.Engine.ECS 697 ns | 0.84× (within error) | Friflo's `OnComponentRemoved` event pushes each id into a list you keep as the removal happens, a cost per removal; `[Removed<T>]` finds the removals through per-block summaries, a fixed cost per pass that 10 removals do not cover. From 1,000 removals on, EasyEcs leads, with no subscription to write. |
| [Create 100,000 three-component entities one by one in a fresh world, nothing reserved](https://ecs.xgamedev.net/docs/comparison#cold-create) | 7.13 ms | Arch 4.77 ms | 0.67× | A fresh EasyEcs world grows its table and entity storage by doubling, and the arrays past 85,000 bytes land on the large object heap; together they pass the budget that sets off a full collection, once per new world (D28). Arch keeps entities in 16 KB chunks, which never reach that heap. Reserve the table with `Reserve<T1..Tn>(count)` or create the entities with `CreateEntities`, and EasyEcs is the fastest of the four. |

</details>

What each library has built in:

| | EasyEcs | Friflo.Engine.ECS | Arch | DefaultEcs |
|---|:-:|:-:|:-:|:-:|
| Structural changes inside a query loop, applied at once | ✅ | ❌ command buffer | ⚠️ unguarded | ⚠️ unsafe |
| Change tracking ("rows written since") | ✅ | ❌ | ❌ | ✅ explicit notify |
| Removal detection ("entities that lost a component since") | ✅ | ⚠️ events | ⚠️ events or a marker tag | ✅ `WhenRemoved` sets |
| Prefabs and cloning | ✅ prefabs outside the world, `Clone` | ⚠️ clone an entity of the store | ❌ | ⚠️ `CopyTo` an entity |
| Value index | ✅ | ✅ | ❌ | ✅ |
| Relations with reverse lookup | ✅ | ✅ | ❌ | ❌ |
| In-memory world snapshot | ✅ | ❌ | ❌ | ❌ |
| Systems, groups, parallel systems | ✅ | ✅ (no parallel systems) | ❌ core | ✅ |
| Source-generated systems + analyzer | ✅ | ❌ | ❌ | ❌ |
| 0 B per tick | ✅ | ✅ | ❌ | ❌ |
| Events | ❌ (state is data) | ✅ | ✅ | ✅ |
| Serialization | ✅ `Save` / `Load` (Nino) | ✅ JSON | ❌ | ✅ text, binary |
| Godot 4 (.NET) | ✅ net10.0 project | ✅ | ✅ | ✅ |
| Unity / netstandard | ❌ net10.0 | ✅ | ✅ | ✅ |

## Documentation

The documentation lives at **[ecs.xgamedev.net](https://ecs.xgamedev.net)**; its sources are the markdown files in
[`docs/content`](docs/content).

| | |
|---|---|
| [Getting started](https://ecs.xgamedev.net/docs/getting-started) | Step-by-step tutorial: entities, components, tags, queries, systems, singletons, groups, the update loop. |
| [Features](https://ecs.xgamedev.net/docs/features) | Every feature: what it does, when to use it, when not, what it costs, which scenarios it serves. |
| [Comparison](https://ecs.xgamedev.net/docs/comparison) | Features and benchmarks against Friflo.Engine.ECS, Arch and DefaultEcs. |
| [Guide](https://ecs.xgamedev.net/docs#guide) | One page per concept: entities and components, prefabs and cloning, queries, systems, singletons, structural changes, change tracking and removal detection, dynamic buffers, indexes and relations, hierarchy, groups, cleanup and disable, bulk changes, snapshots, saves, migration and unloading, `ref` and `await`, threading, high-performance code, grids, hosting, testing, troubleshooting, analyzer rules. |
| [ECS principles](https://ecs.xgamedev.net/docs/ecs-principles) | How to design features with entities, components, singletons and systems. |
| [Design questions](https://ecs.xgamedev.net/docs/faq) | Why there are no events, what is thread-safe, how to change a component. |
| [Samples](https://ecs.xgamedev.net/en/docs/samples) | Runnable programs in learning order: hello world, a game loop, saving and loading, one world per thread, hot reload, actors, a Minecraft-style server, a Godot project. |
| [API reference](https://ecs.xgamedev.net/docs/api) | Every public API with its complexity. |
| [Performance guide](https://ecs.xgamedev.net/docs/performance) | Which API to use when, do's and don'ts, benchmarks. |
| [Architecture](https://ecs.xgamedev.net/docs/architecture) | Storage, memory layout, archetypes, id recycling, iteration safety, generators. |
| [Migration guide](https://ecs.xgamedev.net/en/docs/migration) | What to change when upgrading: between 4.x releases, from v3 to v4 and from v2 to v3. |

The [documentation index](https://ecs.xgamedev.net/docs) suggests a reading order.

### With an AI assistant

Install the EasyEcs skill, and Claude Code, Cursor, Codex, Copilot or any agent that reads
[Agent Skills](https://agentskills.io) follows the EasyEcs rules when it writes your ECS code:

```bash
npx skills add JasonXuDeveloper/EasyEcs
```

[Using EasyEcs with AI assistants](https://ecs.xgamedev.net/en/docs/ai#agent-skill) covers the skill, `llms.txt`, the
Markdown copy of every page.

## What's new in 4.8

- **An agent skill.** `npx skills add JasonXuDeveloper/EasyEcs` installs the EasyEcs rules for Claude Code, Cursor,
  Codex, Copilot and other agents; it loads when a task touches EasyEcs code
  ([AI assistants](https://ecs.xgamedev.net/en/docs/ai#agent-skill)).
- **Systems are static code.** A system is an `[EcsSystem]` ref struct of static methods: `[ForEach]` methods and
  `[OnCreate]` / `[OnUpdate]` / `[OnDestroy]` hooks that take the `World` or the system's `SystemHandle` when they need
  them. No object per system, no virtual call: an assembly's systems run through one generated switch, and a generated
  pass finds its query by its job type. Classes deriving from `EcsSystem` change (a code change, with a code fix on
  the compiler's error: see the [migration guide](https://ecs.xgamedev.net/en/docs/migration#v4-8-systems)), and
  EECS074 to EECS077 keep the new form ([systems](https://ecs.xgamedev.net/en/docs/guide/systems)).
- **SIMD without writing it.** A `[ForEach]` method over numbers runs as a vector loop the generator writes from its
  body, the way Burst compiles a job, with the same results bit for bit: components of 1 to 4 fields, narrow lanes
  (`byte`, `short`), `Vector2` / `Vector3` / `Vector4` fields, `Maybe<T>` parameters, parallel methods, totals into a
  `Reduce<T>` of `[Sum]` / `[Min]` / `[Max]` fields, and on AVX-512 components of up to 16 words. EECS055 names why a method stayed scalar, `SimdMode = SimdMode.Require` makes
  that a build error, and `FloatMode.Fast` takes the vector approximations of `Sin`, `Exp` and the rest
  ([vector loops](https://ecs.xgamedev.net/en/docs/guide/systems#vector-loops)).
- **Parallel passes that need no locks.** `[ForEach]` parameters do per chunk what shared state would do with locks:
  `Reduce<T>` for totals (OpenMP's `reduction`), `Emit<T>` for records a later system applies, `Lookup<T>` to read
  other entities, `Scratch<T>` for a thread's working memory, `EntityRandom` for random numbers that are the same on
  any number of threads; `world.ForParallel` runs a loop over other data on the same threads, and `ChunkRows` spreads
  a few heavy rows ([threading](https://ecs.xgamedev.net/en/docs/guide/threading#reductions)). EECS060 and EECS061
  report parallel code that races, EECS086 to EECS089 a hand-written version of these parameters.
- **Grids shaded like a GPU draws.** A grid cut into tiles is shaded by a method that takes a `Tile<TCell, TTile>`:
  the generator finds the neighbors, `Pad` adds their edges (the halo, as wide and with as many neighbors as the
  stencil reads), and quiet tiles sleep until an edge beside them changes; `IDirectIndexed<TKey>` finds a tile by one
  array read ([grids](https://ecs.xgamedev.net/en/docs/guide/grids)).
- **Declare it, the generator writes it.** `[Sum]`, `[Min]`, `[Max]` and the others on a singleton's fields declare a
  reduction; `[LookupKey]`, `[UniqueKey]` and `[Group]` on a member declare an index key or a group; `[GroupBy]`,
  `[WhenTargetDestroyed]` and `[NotSaved]` on a type declare every world's setup for it
  ([declared parts](https://ecs.xgamedev.net/en/docs/guide/entities-and-components#declared-parts)).
  `[UpdateInterval(n, Phase = p)]` on a system declares its cadence; `[ForEntity]` writes the job and the entry point that
  run a method on one entity; `[Consume]` destroys the rows a pass handled
  ([systems](https://ecs.xgamedev.net/en/docs/guide/systems#consume)).
- **Servers that reach their peak and stop allocating.** `Reserve(prefab, n)` covers index keys, the retired table and
  free ids too; `DynamicBuffer<T>.FillPool` and `MaxPooledLength` keep the lists of buffers that come and go (chunks
  around players) in a pool shared across threads; `prefab.SetAbsent<T>()` gives instances a column for a state they
  enter later, so entering it moves no row; restores and loads reuse what the last one allocated
  ([prefabs](https://ecs.xgamedev.net/en/docs/guide/prefabs#reserve)).
- **Smaller additions.** `ReadOnlyMaybe<T>`, `buffer.SetCount(n)`, `entity.GetOrAdd(value)`,
  `SaveEntitiesWithout`, `world.CollectIds`, enum elements in a `DynamicBuffer`, generic systems closed in another
  assembly under Native AOT, `<IlcInstructionSet>native</IlcInstructionSet>` in the templates for the `Vector512`
  loops, `entity.Run(ref job, in filter, since)` for a static filter, and the generators do about half the work per
  edit. A `Mut<T>` built by hand for a row of a batch job takes the run's `MutColumn`
  ([migration guide](https://ecs.xgamedev.net/en/docs/migration#v4-8-mut-column)).
- **A Minecraft-style sample and a Samples section.** [VoxelServer](https://ecs.xgamedev.net/en/docs/samples/voxel-server)
  runs chunks, flowing water, areas of interest, zombies, replication and saves at 20 ticks a second; the
  [Samples](https://ecs.xgamedev.net/en/docs/samples) section of the site walks through every sample.

Earlier releases are in the [changelog](https://ecs.xgamedev.net/en/docs/changelog), and what to change when upgrading in
the [migration guide](https://ecs.xgamedev.net/en/docs/migration).

## License

[MIT](https://github.com/JasonXuDeveloper/EasyEcs/blob/master/LICENSE)
