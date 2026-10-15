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

The enumerator also exposes `Count` (alive matches) and `IsEmpty`.

## `ForEach`: a struct job

A struct implementing `IForEach<T1..Tn>` is specialized and inlined by the JIT: no delegate, no allocation. Keep
`Execute` small and branch-light.

```csharp
private struct Integrate : IForEach<Position, Velocity>
{
    public void Execute(int entityId, ref Position p, ref Velocity v)
    {
        p.X += v.X;
        p.Y += v.Y;
    }
}

var job = new Integrate();
world.ForEach<Position, Velocity, Integrate>(ref job, Active);
```

The job is passed by `ref`, so fields it accumulates (a sum, a counter) are visible after the call. `entityId` is the
current entity's id; use `world.GetEntity(entityId)` if you need its handle.

## `Count` and `Any`

```csharp
int burning = world.Count<Burning>();             // sums archetype counts: O(matching archetypes)
bool anyActive = world.Any<Position>(Active);     // stops at the first match
```

## Cost

The first time a query shape (components + filter) is used, building its cache entry visits every archetype. After
that, each lookup is one hash probe, and iteration is O(matching archetypes + their slots). Complexities are listed
in the [API reference](../api.md#queries).

Changing entities while iterating is allowed; the rules are in [Structural changes](structural-changes.md).
