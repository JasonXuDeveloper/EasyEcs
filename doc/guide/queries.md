# Queries and filters

[Docs index](../README.md) · Related: [Systems](systems.md) · [Structural changes](structural-changes.md) ·
[API reference](../api.md#queries)

A query selects the entities that have a set of components (1 to 9), optionally filtered. Every query API is
allocation-free.

## Which API

| Need | Use |
|---|---|
| Per-entity logic of a system | a [`[ForEach]` method](systems.md#foreach-methods) (generated struct job) |
| Ad-hoc iteration, fastest | `world.ForEach<T1..Tn, TJob>(ref job, filter)` with a struct job |
| Ad-hoc loop that needs the `Entity` | `foreach (var item in world.Query<T1..Tn>(filter))` |
| "How many …?" | `world.Count<T1..Tn>(filter)` |
| "Is there any …?" | `world.Any<T1..Tn>(filter)` (early exit) |

## Filters

`QueryFilter` adds required components that are not passed in (`With<T>`) and excluded components (`Without<T>`).
Build each filter once and keep it in a `static readonly` field (the generator does this for `[ForEach]`):

```csharp
// A field of your class: build each filter once.
private static readonly QueryFilter Active = QueryFilter.Create().Without<Frozen>().Without<Dead>();
```

In a `[ForEach]` method the same filters are attributes: `[ForEach, With<Player>, Without<Dead>]`. Both can be
repeated.

## `Query`: a foreach loop

```csharp
foreach (var item in world.Query<Position, Velocity>(Active))
{
    ref var p = ref item.Component1;       // Component1..n are refs into storage
    p.X += item.Component2.X;
    if (p.X > 100) item.Entity.Set(new Dead());
}
```

The enumerator and its items are ref structs holding refs into the current table: a loop over a query cannot
`await`, and an item cannot be stored (keep `item.Entity` instead). The enumerator also exposes `Count` (alive matches) and `IsEmpty`.

## `ForEach`: a struct job

A struct implementing `IForEach<T1..Tn>` is specialized and inlined by the JIT: no delegate, no allocation. Keep
`Execute` small and branch-light.

```csharp
public struct Integrate : IForEach<Position, Velocity>
{
    public void Execute(int entityId, ref Position p, ref Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}
```

```csharp
var job = new Integrate();
world.ForEach<Position, Velocity, Integrate>(ref job, Active);
```

The job is passed by `ref`, so fields it accumulates (a sum, a counter) are visible after the call. `entityId` is the
current entity's id. Two variants:

- `IForEachEntity<T1..Tn>` with `world.ForEachEntity` receives the `Entity` handle instead of the id, built from the
  table without a lookup.
- `IForEachBatch<T1..Tn>` with `world.ForEachBatch` receives runs of consecutive rows as spans (`Execute(Span<T1>,
  …)`), for SIMD. The job must not make structural changes.

A job that makes no structural change can say so by implementing `INoStructuralChanges`: the pass then skips the
bookkeeping that keeps changes safe. Only implement it when the job honors it.

## `Count` and `Any`

```csharp
int burning = world.Count<Burning>();             // sums table counts: O(matching archetypes), plus counting
                                                  // presence bits in tables where some rows lack Burning
bool anyActive = world.Any<Position>(Active);     // stops at the first match
```

## Cost

The first time a query shape (components + filter) is used, building its cache entry visits every archetype. After
that, each lookup is one hash probe, and iteration is O(matching archetypes + their rows). Complexities are listed
in the [API reference](../api.md#queries).

Changing entities while iterating is allowed; the rules are in [Structural changes](structural-changes.md).
