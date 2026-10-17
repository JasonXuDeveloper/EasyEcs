# Design questions

[Docs index](README.md) · Related: [ECS principles](ecs-principles.md) · [Threading](guide/threading.md) ·
[Snapshots](guide/snapshots.md)

- [Why are there no events?](#why-are-there-no-events)
- [Why is there no serialization?](#why-is-there-no-serialization)
- [Is it thread-safe?](#is-it-thread-safe)
- [How do I change a component?](#how-do-i-change-a-component)

## Why are there no events?

EasyEcs has no synchronous callbacks on purpose (it had them once). A callback runs in the middle of another system:
the control flow is hidden, a callback can re-enter the code that raised it, and the scheduler cannot see what it reads
and writes, so it could not run systems in parallel safely. Everything an event would say is data instead:

| The event you want | Write it as |
|---|---|
| An entity died, others should react | add a `Dead` tag; systems that care filter `[With<Dead>]` |
| A component was changed | mark it `ITrackChanges`; the consumer takes `[Changed<T>]` |
| A one-off message (a hit landed, a player input) | an entity holding the message, destroyed after its readers ran, or a queue in a singleton |
| Input from another thread (network, IO) | a mailbox of yours that one system drains on the world's thread ([threading](guide/threading.md#input-from-other-threads)) |

```csharp
public record struct Hit(Entity Target, int Damage) : IComponent;   // a message is an entity

public partial class ApplyHitsSystem : EcsSystem
{
    [ForEach]
    static void Apply(in Hit hit)
    {
        ref var hp = ref hit.Target.TryGetRef<Health>(out bool found);
        if (found) hp = hp with { Value = hp.Value - hit.Damage };
    }
}

public sealed class ClearHitsSystem : EcsSystem
{
    public override int Priority => 100;                                // after every reader
    protected override void OnUpdate() => World.DestroyEntities<Hit>();  // all messages, one pass
}
```

What this buys:
- The order of effects is the order of `Priority`, visible in one place.
- The generator knows what every system reads and writes, so `ParallelSystems` batches them safely.
- Tests and snapshots see the "events", because they are data.

## Why is there no serialization?

Two different needs hide behind the word:

- **Undo, rollback, prediction, replays in memory:** built in. `world.Capture()` / `world.Restore(snapshot)` copy
  every entity, component and singleton ([snapshots](guide/snapshots.md)). It is not a file format. A managed
  reference in a component (a `List<T>`) is copied as a reference; a `DynamicBuffer<T>` copies its elements.
- **Saving to disk or a database:** yours. Components are plain data structs, so the serializer you already use (Nino,
  MessagePack, System.Text.Json, ...) writes them as they are. The save format, its versioning and which components to
  keep belong to your game; a built-in format would decide them for you.

To save only what changed, ask for the rows written since the last save:

```csharp
private static readonly QueryFilter Saved = QueryFilter.Create().Changed<Health>(0);   // the tick is set per call
ulong savedAt = world.ChangeTick;   // remember it when you save
// ... ticks ...
int dirty = world.Count<Health>(Saved.ChangedSince(savedAt));
foreach (var item in world.Query<Health>(Saved.ChangedSince(savedAt)))
{
    // write item.Entity and item.Component1 with your serializer
}
```

## Is it thread-safe?

The rules are explicit, and the absence of callbacks is what keeps them simple:

- **A world has one owner thread.** Its systems, queries and component reads and writes all run there. On a server,
  run one world per thread (one per map or zone).
- **Structural changes are the owner thread's too.** Adding or removing components and creating or destroying
  entities check a plain owner flag: one made while another runs, or while the world runs work on several threads,
  throws instead of tearing the tables. The analyzer reports the off-thread changes it can see (EECS013, EECS014).
- **The parallelism the framework adds is checked.** `ParallelSystems` batches only systems whose generated access does
  not conflict; the analyzer makes sure a `[ForEach(Parallel = true)]` method changes no structure and reads singletons
  only.

What to watch for:
- Never change a world from another thread: post the message to its owner thread, where a system handles it.
- A value index (`Index<T, TKey>`) is not synchronized: use it on the owner thread only.
- Never keep a `ref` across an `await`: keep the `Entity` handle and get the component again ([`ref` and
  `await`](guide/refs-and-await.md)).

## How do I change a component?

Through a `ref`. In a system, a `ref` parameter is a write and `in` is a read:

```csharp
public partial class BurnSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Burn(ref Health hp, in Burning burning) => hp = hp with { Value = hp.Value - burning.DamagePerTick };
}
```

Outside systems:

```csharp
entity.Get<Health>().Value -= 10;                            // Get returns ref T: this writes the component
ref var hp = ref entity.Get<Health>();                       // keep the ref for several writes
hp.Value = 100;
ref var mana = ref entity.TryGetRef<Mana>(out bool found);   // when the entity may not have it
if (found) mana.Value += 5;
entity.Set(new Position(0, 0), new Velocity(1, 0));          // replace or add several at once: one transition
foreach (var item in world.Query<Position, Velocity>())
    item.Component1.X += item.Component2.X;                  // Component1..n are refs
```

```csharp
public record struct Mana(int Value) : IComponent;
```

Pitfalls:
- `var hp = entity.Get<Health>(); hp.Value--;` changes a copy: nothing is written back. Keep a `ref var`.
- Read with `in` or `entity.Read<T>()`, not `ref`. Every `ref` access to an `ITrackChanges` component counts as a
  change, so replication would send rows nobody changed.
- Change a collection inside a component through a `ref` to the component. Through an `in` parameter, change
  tracking and the parallel scheduler would miss it; the analyzer reports it ([EECS011](guide/analyzer.md#eecs011)).
  For a list per entity, a [`DynamicBuffer<T>`](guide/dynamic-buffers.md) makes every write a tracked one.
