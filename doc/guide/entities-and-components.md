# Entities and components

[Docs index](../README.md) · Related: [Queries](queries.md) · [`ref` and `await`](refs-and-await.md) ·
[API reference](../api.md#entities)

## At a glance

| | |
|---|---|
| **Use it for** | Per-entity data (`Position`, `Health`), states as tags (`Dead`, `Stunned`), handles that outlive their entity safely. |
| **Avoid** | Classes or behavior in components; raw ids kept without their version. |
| **Cost** | Create / destroy O(1) amortized; `Get` through a handle is one 16-byte record, one table lookup, one load; removing a component and adding it back flips a presence bit. |

## Entities

An `Entity` is an identity, nothing more: a 16-byte handle holding the world, an id and a version. It holds no game
data and no behavior of its own.

```csharp
Entity e = world.CreateEntity();                                   // empty
Entity u = world.CreateEntity(new Position(0, 0), new Health(100)); // 1-9 components, placed in one step

bool alive = u.IsAlive;
u.Destroy();                       // or world.DestroyEntity(u); a stale handle is ignored
```

Waves of spawns and clean-ups have batch forms. Each takes the structural gate once for the whole batch:

```csharp
var wave = new Entity[500];
world.CreateEntities(wave, new Position(0, 0), new Health(100));  // the same values for each; handles written to wave
int cleared = world.DestroyEntities<Health>(QueryFilter.Create().With<Dead>());  // every dead entity with Health
```

- **Ids are recycled** (LIFO, so recently freed slots are still warm in cache). `Entity.Id` is unique only among
  live entities.
- **Every access validates the version.** When an entity is destroyed its version is bumped, so every outstanding
  handle becomes stale: `Get`, `Has`, `Set`, `Remove` on it throw `InvalidOperationException` ("Entity has been
  destroyed"), while `Destroy` on it is ignored.
- Equality includes the version: two handles are equal only if they name the same incarnation.
- **Keep the `Entity`, never a raw id.** To look an id up, use `world.TryGetEntity(id, out e)` /
  `world.GetEntity(id)`, which return the current incarnation.
- `world.EntityCount` is the number of live entities; `world.Entities` enumerates them (diagnostics, O(capacity)).
- `world.EnsureEntityCapacity(n)` or `WorldOptions.InitialEntityCapacity` pre-sizes storage so it never grows at
  run time.

## Components

A component is a struct implementing `IComponent`: per-entity data, fields only (computed getters are fine). The
analyzer rejects classes ([EECS006](analyzer.md#eecs006)).

```csharp
public record struct Health(int Value) : IComponent;
public record struct Name(string Value) : IComponent;   // managed references are allowed
```

| Operation | Effect |
|---|---|
| `ref T Get<T>()` | Ref into storage; throws if the entity does not have `T`. |
| `bool Has<T>()` | Whether the entity has `T`. |
| `bool TryGet<T>(out T value)` | A **copy** of `T` if present. |
| `ref T Add<T>()` | Adds `T` (or resets it to `default` if present) and returns a ref. |
| `ref T GetOrAdd<T>()` | Ref to `T`, adding it with `default` first if missing. |
| `Entity Set(in T1 … in Tn)` | Adds or overwrites 1-9 values, with at most one row copy; chainable. |
| `void Remove<T1..Tn>()` | Removes 1-9 components; the entity keeps its row. Missing ones are ignored. |

Always mutate through a `ref`; see [`ref` and `await`](refs-and-await.md).

Components live in archetype tables, one row per entity. Removing a component keeps the entity in its table and only
marks the component absent, so states that come and go never move data; adding back a component the table has a
column for is just as cheap. Gaining a component the entity's table has no column for copies its row into another
table, once per new combination of components.

When a component that contains managed references is removed (or its entity destroyed), its value is cleared so the
GC can collect what it pointed to. Unmanaged components are not cleared: `Add` and `Set` overwrite them anyway.

### Tags

A tag is an empty component: `public struct Dead : IComponent;`. Tags exist only to be filtered on
(`[Without<Dead>]`, `QueryFilter.Create().With<Stunned>()`). Use them for states (dead, stunned, burning): adding
and removing a tag the entity had before flips a bit. For a value that changes every tick, a field is still cheaper:
see
[ECS principles, rule 5](../ecs-principles.md#5-choose-the-cheapest-representation-for-frequent-changes).

### Design tips

- Keep components small (a few fields, well under a 64-byte cache line) and split rarely-used data into its own
  component: iteration only loads the columns it reads.
- Spawn with all components at once (`CreateEntity(a, b, c)`) and change several with `Set(a, b)` /
  `Remove<A, B>()`: at most one row copy instead of several.
- Up to 65,536 component types are supported. The first 256 fit in each archetype mask inline; beyond that a mask
  carries an overflow array (at least 4 × 256 bits, doubled when needed).
