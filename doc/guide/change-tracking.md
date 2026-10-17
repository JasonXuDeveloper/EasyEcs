# Change tracking

[Docs index](../README.md) · Related: [Systems, `[ForEach]` and groups](systems.md) · [Queries](queries.md) ·
[Indexes and relations](indexes-and-relations.md) · [API reference](../api.md#queries)

Some systems only care about what changed: replicating state to clients, saving dirty entities, rebuilding a spatial
index for entities that moved. Mark the component `ITrackChanges` and give the method `[Changed<T>]`: it visits only
the entities whose `T` was written since the system last ran.

## At a glance

| | |
|---|---|
| **Use it for** | Following the few entities that changed: replication to clients, saving dirty entities, re-indexing moved units. |
| **Avoid** | Components rewritten on every row every tick (nothing to skip); `ref` parameters that only read a tracked component (they count as writes). |
| **Cost** | 8 bytes per row; a write stores its tick and updates its 64-row word's summary (a few ns through a handle); a pass skips unwritten tables, 4,096-row blocks and 64-row words and runs the method for changed rows only. |

## Example

```csharp
public record struct Health(int Value) : IComponent, ITrackChanges;
public record struct Replica(int Value) : IComponent;

public partial class Replicate : EcsSystem
{
    // Only entities whose Health was written since this system's previous run (all of them on its first run).
    [ForEach, Changed<Health>]
    private static void Copy(in Health health, ref Replica replica) => replica = new Replica(health.Value);
}
```

## What counts as a write

Every row of a tracked component records the world's change tick of its last write. A write is any access that hands
out a `ref`, whether or not the code stores through it:

| Write | Not a write |
|---|---|
| `ref T` parameters of `[ForEach]` methods (`Span<T>` for batch methods) and of struct jobs (`Writes`) | `in T` parameters, `ReadOnlySpan<T>` |
| `Query` items (the enumerator stamps every row it visits) | `TryGet<T>(out T)` (a copy), `Read<T>()` (read-only) |
| `entity.Get<T>()`, `TryGetRef<T>(out found)` when found, `GetOrAdd<T>()`, `Add<T>()`, `Set(...)` | `Has<T>()` |
| `CreateEntity(...)`, `CreateEntities(...)` with the component | removing the component |
| `world.Restore(snapshot)` (every restored row) | |

So declare read-only access as `in`: a `ref` parameter that only reads marks every row it visits as changed. A
collection inside a component changes without the row noticing: change it only through a `ref` access, which stamps
the row (EECS011 reports changes through `in`; [ECS principles, rule 8](../ecs-principles.md#8-choose-how-to-hold-a-collection)).
A [`DynamicBuffer<T>`](dynamic-buffers.md) is a list that cannot change any other way: its writes need a `ref`.

## Ticks

`world.ChangeTick` grows by one per batch of systems, once after each update and once per index lookup that catches
up outside a pass ([indexes](indexes-and-relations.md)), so writes between updates are newer than every system's last
run. `system.LastRunTick` is the tick a system last ran at; a `[Changed<T>]` method selects rows written after it, so
a system's own writes of one run are not changes for its next run. Systems that write `T` never run in the same
parallel batch as a `[Changed<T>]` reader (the method declares a read of `T`).

Outside systems, filter by a tick you read earlier:

```csharp
ulong mark = world.ChangeTick;
// ... writes ...
var changed = QueryFilter.Create().Changed<Health>(mark);   // rows written at or after mark
int count = world.Count<Health>(changed);
foreach (var item in world.Query<Health>(changed)) { /* ... */ }
```

Writes between two updates share one tick, so a mark taken in the middle of them also selects the writes made before
it since the last update: take the mark right after an update (or a group's update), and update again before the next
mark. A filter has one tick: several `Changed` filters require all of them, and must name the same tick (else
`ArgumentException`). A filter kept in a field takes a new tick per use with `ChangedSince`, without being rebuilt:

```csharp
public static class Replication
{
    private static readonly QueryFilter HealthChanged = QueryFilter.Create().Changed<Health>(0);

    public static int ChangedSince(World world, ulong mark) => world.Count<Health>(HealthChanged.ChangedSince(mark));
}
```

The tick is part of the call because a change is relative to someone's last look: `[Changed<T>]` methods use their
system's last run, ad-hoc queries the mark the caller took. Tick 0 selects every row having `T`.

## Cost

A tracked component carries a hidden column of 8-byte ticks in every table that has it, and each write stores one
tick and updates the summary of its 64-row word (a run of rows stores them with one vectorized fill; through a handle,
keeping the summary of 1,000 scattered writes takes about 5 μs). Each table also keeps the latest tick of the column and of
each block of 4,096 rows, and per 64-row word the rows written at its latest tick, so a `Changed` pass skips a table
nobody wrote with one load, then unwritten blocks and words (4 per SIMD compare), and takes the written rows of a word
from its summary; only a word whose rows were written at different ticks since the mark, or row by row by a query
or a per-entity job, compares its rows (4 per SIMD compare). The method runs only for changed rows. How much of the walk is skipped depends on how the writes spread: a few writes, or writes to
neighbouring rows, skip almost everything, while 1,000 writes scattered over 100,000 rows touch most words, and then
the saving is the method calls (see the [performance guide](../performance.md#benchmarks)). Components without
`ITrackChanges` pay nothing: the checks are JIT-time constants.
