# Queries and filters

[Docs index](../README.md) · Related: [Systems](systems.md) · [Structural changes](structural-changes.md) ·
[API reference](../api.md#queries)

A query selects the entities that have a set of components (1 to 9), optionally filtered. Every query API is
allocation-free.

## At a glance

| | |
|---|---|
| **Use it for** | Iteration outside systems (tools, tests, request handlers between updates), counts and existence checks. |
| **Avoid** | Per-tick logic outside systems: write a `[ForEach]` method instead, it is generated, ordered and scheduled. |
| **Cost** | Allocation-free; one hash probe per lookup after a query shape's first use, then O(matching tables + rows); filtered rows are tested 64 at a time. |

## Which API

| Need | Use |
|---|---|
| Per-entity logic of a system | a [`[ForEach]` method](systems.md#foreach-methods) (generated struct job) |
| Ad-hoc iteration, fastest | `world.ForEach<T1..Tn, TJob>(ref job, filter)` with a struct job |
| Ad-hoc loop that needs the `Entity` | `foreach (var item in world.Query<T1..Tn>(filter))` |
| "How many …?" | `world.Count<T1..Tn>(filter)` |
| "Is there any …?" | `world.Any<T1..Tn>(filter)` (early exit) |
| Only the rows written since a tick | `QueryFilter.Create().Changed<T>(tick)`, or `[Changed<T>]` in a system ([change tracking](change-tracking.md)) |
| Entities by a value, or pointing at an entity | `world.Index<T, TKey>().Find(key)` ([indexes and relations](indexes-and-relations.md)) |

## Filters

`QueryFilter` adds required components that are not passed in (`With<T>`), excluded components (`Without<T>`), and
a group of components of which the entity needs at least one (`WithAny<T>`, repeated for each member of the group).
Build each filter once and keep it in a `static readonly` field (the generator does this for `[ForEach]`):

```csharp
// A field of your class: build each filter once.
private static readonly QueryFilter Active = QueryFilter.Create().Without<Frozen>().Without<Dead>();
private static readonly QueryFilter Impaired = QueryFilter.Create().WithAny<Frozen>().WithAny<Stunned>();  // either one
```

In a `[ForEach]` method the same filters are attributes: `[ForEach, With<Player>, Without<Dead>]`,
`[ForEach, WithAny<Frozen>, WithAny<Stunned>]`. All can be repeated. `Changed<T>(since)` (and
`[Changed<T>]`) select rows whose tracked component was written since a tick: see [change tracking](change-tracking.md).

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
`await`, and an item cannot be stored (keep `item.Entity` instead). To count or test for matches, use `world.Count` /
`world.Any`.

## `ForEach`: a struct job

A struct implementing `IForEach<T1..Tn>` is specialized and inlined by the JIT: no delegate, no allocation. Keep
`Execute` small and branch-light.

```csharp
public struct Integrate : IForEach<Position, Velocity>
{
    public void Execute(ref Position p, ref Velocity v)
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

The job is passed by `ref`, so fields it accumulates (a sum, a counter) are visible after the call. It receives the
components only, so the pass never loads the entity rows. Two variants:

- `IForEachEntity<T1..Tn>` with `world.ForEachEntity` also receives the `Entity` handle (its `Id` and `Version`),
  built from the table without a lookup: use it when the job needs the entity.
- `IForEachBatch<T1..Tn>` with `world.ForEachBatch` receives runs of consecutive rows as spans (`Execute(Span<T1>,
  …)`), for SIMD. The job must not make structural changes.

```csharp
public struct KillTheWounded : IForEachEntity<Health>          // the handle, for structural changes
{
    public void Execute(Entity unit, ref Health hp)
    {
        if (hp.Value <= 0) unit.Set(new Dead());
    }
}

public struct Drift : IForEachBatch<Position, Velocity>       // spans of consecutive rows
{
    public void Execute(Span<Position> p, Span<Velocity> v)
    {
        for (int i = 0; i < p.Length; i++) p[i].X += v[i].X;
    }
}
```

```csharp
var kill = new KillTheWounded();
world.ForEachEntity<Health, KillTheWounded>(ref kill, QueryFilter.Create().Without<Dead>());
var drift = new Drift();
world.ForEachBatch<Position, Velocity, Drift>(ref drift, Active);
```

A job that makes no structural change can say so by implementing `INoStructuralChanges`: the pass then skips the
bookkeeping that keeps changes safe. Only implement it when the job honors it.

### What a job declares: `Writes`

A static member tells the pass what a hand-written job writes. `[ForEach]` methods get it from their parameters; a job
you write keeps the safe default unless you override it:

| Member | Default | Meaning | When it is wrong |
|---|---|---|---|
| `static int Writes` | `-1` (all) | bit k - 1 set: the job writes component k through its ref. Those columns count as changed for `[Changed<T>]` (tracked components only), and when the job moves its entity to another archetype they are carried over to the new row | a write the mask leaves out is not stamped (watchers miss it) and is lost if the job also moved the entity |

A read-only job declares it, and a census of the world stamps nothing:

```csharp
public struct SumHealth : IForEach<Health>, INoStructuralChanges
{
    public long Total;

    public static int Writes => 0;          // reads only: nothing stamped, nothing carried over

    public void Execute(ref Health health) => Total += health.Value;
}
```

```csharp
var census = new SumHealth();
world.ForEach<Health, SumHealth>(ref census);
long total = census.Total;
```

### On several threads: `ForEachParallel` and `ForEachEntityParallel`

A job that implements `INoStructuralChanges` and writes only its own row can run on several threads.
`ForEachParallel` (an `IForEach` job) and `ForEachEntityParallel` (an `IForEachEntity` job) cut the matching rows into
chunks of `World.ChunkRows` rows. The calling thread and up to `WorldOptions.MaxDegreeOfParallelism - 1`
thread-pool threads run them, each with its own copy of the job, so state a job accumulates in its fields is not
merged back. Fewer than two chunks run on the calling thread. The call allocates nothing after the first call per job
type.

```csharp
public struct Heal : IForEachEntity<Health>, INoStructuralChanges
{
    public static int Writes => 1;          // writes Health (component 1)

    public void Execute(Entity entity, ref Health health) => health.Value = Math.Min(health.Value + 10, 100);
}
```

```csharp
var heal = new Heal();
world.ForEachEntityParallel<Health, Heal>(ref heal, Active); // each row runs once, on one of the threads
```

The job must not read what another row's job writes (another entity, a singleton it also writes), and nothing else
may change the world during the call. `[ForEach(Parallel = true)]` compiles a system method to the same chunked run.

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
