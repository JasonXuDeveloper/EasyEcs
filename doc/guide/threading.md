# Threading

[Docs index](../README.md) · Related: [Architecture: threading](../architecture.md#threading) ·
[Systems](systems.md#one-update)

## At a glance

| | |
|---|---|
| **Use it for** | One world per thread (one per map or zone); `ParallelSystems` for heavy independent systems; parallel `[ForEach]` for one heavy method. |
| **Avoid** | Several threads of your own mutating one world (EECS014 reports the ones it sees); component values touched outside the owner thread. |
| **Cost** | No locks or atomics on the hot paths: queries match lock-free, a structural change checks and sets a plain owner flag; a parallel batch costs a thread-pool hand-off (microseconds). |

- [Terms: process, world, owner thread](#terms-process-world-owner-thread)
- [Recommended: one world per thread](#recommended-one-world-per-thread)
- [Order of systems](#order-of-systems)
- [Parallel systems](#parallel-systems)
- [Parallel `[ForEach]` methods](#parallel-foreach-methods)
- [What the world synchronizes](#what-the-world-synchronizes)
- [Input from other threads](#input-from-other-threads)
- [Compared with other ECS libraries](#compared-with-other-ecs-libraries)

## Terms: process, world, owner thread

| Term | Meaning |
|---|---|
| **Process** | Your server. It can hold any number of worlds; nothing is shared between them except type indices. |
| **World** | One isolated set of entities, components, singletons and systems (`new World()`). |
| **Owner thread** | The thread that calls `world.Update()` and touches the world's data. One at a time per world. |
| **`ParallelSystems`** | Inside one `Update()` call, the world borrows thread-pool threads to run a batch of non-conflicting systems; the owner thread takes part and `Update()` returns only when the batch is done. Ownership does not change. |

So it is not "one process per world": a process runs many worlds side by side (typically one per thread), and each world
can additionally spread one update over several cores with `ParallelSystems`.

```mermaid
flowchart TB
    subgraph P [process]
        subgraph S1 [owner thread 1]
            U1["world 1: Update()"]
        end
        subgraph S2 [owner thread 2]
            U2["world 2: Update()"] -->|ParallelSystems: a batch of non-conflicting systems| TP[thread-pool helpers]
        end
    end
```

## Recommended: one world per thread

Worlds share nothing, so a server that splits its entities over several worlds, each owned by one thread (for example
one world per map or zone), needs no synchronization at all: a world's thread is its owner. `MultiWorldBenchmark` measures exactly this layout (one world per
core, each on its own long-lived thread): see [Performance](../performance.md#benchmarks).

```mermaid
flowchart LR
    T1[Thread 1] --> W1[World 1]
    T2[Thread 2] --> W2[World 2]
    T3[Thread N] --> W3[World N]
```

Hand work to a world from another thread the way you would to any single-owner structure: post it to the owning
thread (a queue the owner thread drains), which applies it before or between updates.

## Order of systems

The order is explicit and deterministic: `Update()` runs systems in ascending `Priority`, and systems with the same
priority in registration order. Systems never call each other; a system that consumes what another produces gets a
higher priority ([ECS principles](../ecs-principles.md#3-systems-never-reference-systems)).

Structural changes apply immediately, so a later system in the same update already sees an entity that an earlier
one created, destroyed or tagged. There are no command buffers to play back and no "end of frame" sync points.

Dependencies are expressed through data and `Priority`, never through `before`/`after` links between systems:

- **A produces, B consumes a component** (A writes `Velocity`, B reads it): give B a higher priority, or register it
  after A. Even at the same priority with `ParallelSystems`, they conflict on `Velocity`, so they are in different
  batches and A finishes first.
- **A changes structure, B reacts** (A adds `Dead`, B destroys dead entities): A needs an `Entity` parameter to add the
  tag, so it runs alone; B runs after it.
- **A and B touch disjoint data**: their order cannot change the result, so `ParallelSystems` may run them together.

In every case the result of an update equals running the systems one by one in `Priority` / registration order.

## Parallel systems

With `WorldOptions.ParallelSystems = true`, systems of the same priority that do not conflict run at the same time.
Nothing is left to discipline: the generator declares every `[ForEach]` system's access from its parameters, and the
scheduler only puts non-conflicting systems together.

| `[ForEach]` parameter | Declares |
|---|---|
| `ref T` component / singleton | writes `T` |
| `in T` component / singleton | reads `T` |
| `[With<T>]` / `[Without<T>]` / `[WithAny<T>]` | nothing (filters read the archetype and presence bits, which no parallel system changes) |
| `[Changed<T>]` | reads `T` (its change ticks), so it never shares a batch with a writer of `T` |
| `Entity` or `World`, an instance method, an `OnUpdate` override, no `[ForEach]` method, or a subclass that adds no `[ForEach]` method of its own | runs alone |

Two systems conflict when one writes a component or singleton the other reads or writes, or when either runs alone.
A system "runs alone" when it can reach the world beyond its parameters: through an `Entity` it can read any
component or make structural changes, so it never shares a batch.

The systems of one priority are split into **batches of consecutive, pairwise non-conflicting systems**. Batches run
one after another; the systems of a batch run at the same time.

```csharp
public record struct Body(float X, float Y, float Vx, float Vy) : IComponent;
public record struct Vitals(int Health, int Max) : IComponent;
public record struct Alarms(int Count) : ISingletonComponent;

public partial class MoveBodies : EcsSystem        // writes Body
{
    [ForEach] static void Move(ref Body b) { b.X += b.Vx; b.Y += b.Vy; }
}

public partial class RegenerateHealth : EcsSystem  // writes Vitals
{
    [ForEach] static void Regenerate(ref Vitals v) => v.Health = Math.Min(v.Health + 1, v.Max);
}

public partial class LowHealthAlarm : EcsSystem    // reads Vitals: conflicts with RegenerateHealth
{
    [ForEach] static void Count(in Vitals v, ref Alarms alarms) { if (v.Health < 10) alarms.Count++; }
}
```

```mermaid
flowchart LR
    subgraph B1 [batch 1: at the same time]
        M[MoveBodies<br/>writes Body]
        R[RegenerateHealth<br/>writes Vitals]
    end
    subgraph B2 [batch 2]
        A[LowHealthAlarm<br/>reads Vitals, writes Alarms]
    end
    B1 --> B2
```

Because batches are consecutive, registration order keeps its meaning: of two conflicting systems, the one registered
first always finishes first, and the result is the same as a sequential update. A system that should run alone or in
another order gets its own priority.

Execution is allocation-free: the calling thread works through a batch together with up to
`MaxDegreeOfParallelism - 1` reusable thread-pool work items (no `Parallel.For` closures, no tasks). Lower
`MaxDegreeOfParallelism` when several worlds, or IO threads, share the cores: a batch then takes fewer of them. The first
exception thrown in a batch is rethrown on the calling thread after the batch (later ones are dropped); with a
`SystemError` subscriber every exception is reported instead, possibly from a thread-pool thread.

When to turn it on: a world with several heavy, independent systems per priority and spare cores. Each batch costs a
hand-off to the thread pool (microseconds), so it pays for systems that iterate thousands of entities, not for a
handful. For a server, one world per thread usually uses the cores better: see
[Performance](../performance.md#benchmarks) (`ParallelSystemsBenchmark`, `MultiWorldBenchmark`).

## Parallel `[ForEach]` methods

`Parallel` runs several systems at once; `[ForEach(Parallel = true)]` splits one method's rows across threads (see
[Systems](systems.md#parallel-methods)). The two compose: inside a parallel batch, a parallel method whose chunk
runner is busy with another system's method runs on its own thread instead of waiting. Both use the same allocation-free
runner, and the calling thread always takes part.

## What the world synchronizes

| Operation | Safety |
|---|---|
| Structural changes (create/destroy, `Add`/`Set`/`Remove`), adding/removing systems and singletons, `Capture`/`Restore` | Owner thread only, one at a time. They check and set the world's owner flag (no atomic instruction): one started while another runs, or while the world runs a parallel batch or a parallel `[ForEach]`, throws `InvalidOperationException` instead of tearing the tables. |
| Query matching, `Has`, `Get` lookups | Lock-free over immutable, copy-on-write archetype snapshots (the first use of a query shape takes a compare-and-swap gate once to build its cache entry, so the threads of a parallel batch can race to it). |
| A singleton a system resolves first (`[ForEach]` singleton parameters, `GetOrAddSingleton`, `SingletonRef`) | The same compare-and-swap gate: systems of one batch may add it at once. |
| Component and singleton **values** | Not synchronized, by design: the owner thread, or the parallel scheduler, decides who touches them. |
| Value index lookups, and the unlinking done by destroying an entity or removing an indexed component | Not synchronized: owner thread only while the world has an index ([indexes and relations](indexes-and-relations.md)). |

Structural changes are the owner thread's, like component values: a `ref` or a running query on one thread would not
survive a change made by another thread anyway. So use one owner thread per world, with `ParallelSystems` for systems
the scheduler checked; do not mutate one world from several threads of your own.

What catches a change made off the owner thread:

| Where the change comes from | Caught by |
|---|---|
| A `[ForEach]` method that takes no `Entity` or `World` (or only reads its `Entity`), changing structure through a handle a component holds or a world kept elsewhere | [EECS013](analyzer.md#eecs013) at compile time; the runtime throws if it runs in a parallel batch. |
| A system of a parallel batch, or a parallel `[ForEach]` job, that changes structure through a helper method | The runtime: the world marks the batch and the chunks as a parallel region, and every structural change in one throws, deterministically. |
| A lambda handed to `Task.Run`, `Parallel.*`, `ThreadPool`, `new Thread` or `new Timer` that changes a world (or an entity) the code around it also changes, or hands off several times | [EECS014](analyzer.md#eecs014) at compile time. A world handed to a thread that then owns it (a world per thread) is not reported. |
| Your own threads in any other way | Nothing reliable: the owner flag may throw, but two threads can also pass it together. Hand the change to the owner as data ([below](#input-from-other-threads)). |

## Input from other threads

A world has one owner thread, and EasyEcs adds no cross-thread queue of its own: structural changes are immediate on
that thread, so there is nothing to defer. Input from other threads (network, timers, a database) goes through a queue
the caller owns, and a system on the owner thread drains it into data:

```csharp
using System.Collections.Concurrent;

public record struct MoveRequest(int PlayerId, float X, float Y);           // a message, not a component
public record struct MoveIntent(int PlayerId, float X, float Y) : IComponent;

public sealed class ReceiveMoves(ConcurrentQueue<MoveRequest> inbox) : EcsSystem
{
    private readonly ConcurrentQueue<MoveRequest> _inbox = inbox;          // filled by the network threads

    protected override void OnUpdate()
    {
        while (_inbox.TryDequeue(out var move))
            World.CreateEntity(new MoveIntent(move.PlayerId, move.X, move.Y));   // input becomes data
    }
}
```

Register it with `world.AddSystem(new ReceiveMoves(inbox))`, before the systems that consume `MoveIntent` (and destroy
it when handled). The queue's type, bound and back-pressure are the application's choice. Other libraries meet the
same need with thread-safe command buffers (Arch, Friflo's synced buffer, DefaultEcs' recorder, Unity's parallel
writer), which exist mainly so parallel jobs can record structural changes; Bevy uses this same pattern, a channel
drained by a system.

## Compared with other ECS libraries

| Library | Order of systems | Systems in parallel | How conflicts are avoided |
|---|---|---|---|
| **EasyEcs** | `Priority`, then registration order | Non-conflicting systems of one priority (`Parallel`); within one method (`[ForEach(Parallel = true)]`, chunks of rows) | Access generated from `ref`/`in` parameters; conflicting systems never share a batch; the analyzer rejects parallel methods that could race (EECS010) and writes through `in` parameters (EECS011) |
| Entitas | Order added to `Feature`/`Systems` | No; a `JobSystem` splits one system's entities across threads | You keep job bodies independent |
| Arch | Your own call order (Arch.System: group order) | Within one query (`ParallelQuery`, chunk jobs) | You keep parallel query bodies independent |
| Friflo.Engine.ECS | `SystemRoot` / `SystemGroup` order | Within one query (`RunParallel`, chunk tasks) | You keep parallel query bodies independent |
| DefaultEcs | `SequentialSystem` order | `ParallelSystem` (all children at once) and per-set runners | You put only independent systems into a `ParallelSystem` |
| Bevy (Rust) | Ambiguous unless ordered (`before`/`after`) | Any systems whose access does not conflict | Access read from parameter types by the scheduler |
| Unity DOTS | `UpdateBefore`/`UpdateAfter` attributes in groups | Jobs | Read/write handles plus the job safety system (dependencies) |

EasyEcs takes the access-based model of Bevy and Unity DOTS, but keeps the order total and deterministic: within a
priority it only runs systems together when their access proves that the order cannot matter.
