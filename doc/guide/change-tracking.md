# Change tracking

[Docs index](../README.md) · Related: [Systems, `[ForEach]` and groups](systems.md) · [Queries](queries.md) ·
[API reference](../api.md#queries)

Some systems only care about what changed: replicating state to clients, saving dirty entities, rebuilding a spatial
index for entities that moved. Mark the component `ITrackChanges` and give the method `[Changed<T>]`: it visits only
the entities whose `T` was written since the system last ran.

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
| `Query` items (the enumerator stamps every row it visits) | `TryGet<T>(out T)` (a copy) |
| `entity.Get<T>()`, `GetOrAdd<T>()`, `Add<T>()`, `Set(...)` | `Has<T>()` |
| `CreateEntity(...)`, `CreateEntities(...)` with the component | removing the component |

So declare read-only access as `in`: a `ref` parameter that only reads marks every row it visits as changed.

## Ticks

`world.ChangeTick` grows by one per batch of systems, once after each update and once per index lookup that catches
up ([indexes](indexes-and-relations.md)), so writes between updates are newer than every system's last run.
`system.LastRunTick` is the tick a system last ran at; a `[Changed<T>]` method selects rows written after it, so a
system's own writes of one run are not changes for its next run. Systems that write `T` never run in the same parallel
batch as a `[Changed<T>]` reader (the method declares a read of `T`).

Outside systems, filter by a tick you read earlier:

```csharp
ulong mark = world.ChangeTick;
// ... writes ...
var changed = QueryFilter.Create().Changed<Health>().ChangedSince(mark);   // rows written at or after mark
int count = world.Count<Health>(changed);
foreach (var item in world.Query<Health>(changed)) { /* ... */ }
```

`Changed<T>()` without `ChangedSince` selects every row having `T`. Several `Changed` filters require all of them.

## Cost

A tracked component carries a hidden column of 8-byte ticks in every table that has it, and each write stores one
tick (a run of rows stores them with one vectorized fill). A `Changed` filter tests 4 rows per SIMD compare, 64 rows
per word, so a pass over 100,000 rows of which 1,000 changed costs far less than visiting every row (see the
[performance guide](../performance.md#benchmarks)). Components without `ITrackChanges` pay nothing: the checks are
JIT-time constants.
