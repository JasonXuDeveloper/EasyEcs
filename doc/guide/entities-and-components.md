# Entities and components

[Docs index](../README.md) · Related: [Queries](queries.md) · [`ref` and `await`](refs-and-await.md) ·
[API reference](../api.md#entities)

## At a glance

| | |
|---|---|
| **Use it for** | Per-entity data (`Position`, `Health`), states as tags (`Dead`, `Stunned`), handles that outlive their entity safely. |
| **Avoid** | Classes or behavior in components; raw ids kept without their version. |
| **Cost** | Create O(n) amortized (n components), destroy O(c) (c columns); `Get` through a handle is one 16-byte record, one table lookup, one load; removing a component and adding it back flips a presence bit. |

## Entities

An `Entity` is an identity, nothing more: a 16-byte handle holding the world, an id and a version. It holds no game
data and no behavior of its own.

```csharp
Entity e = world.CreateEntity();                                   // empty
Entity u = world.CreateEntity(new Position(0, 0), new Health(100)); // 1-9 components, placed in one step

bool alive = u.IsAlive;
u.Destroy();                       // a stale handle is ignored
```

Waves of spawns and clean-ups have batch forms. Each takes the structural gate once for the whole batch:

```csharp
var wave = new Entity[500];
world.CreateEntities(wave, new Position(0, 0), new Health(100));  // the same values for each; handles written to wave
int cleared = world.DestroyEntities<Health, Dead>();  // every dead entity with Health, one pass
```

- **Ids are recycled** (LIFO, so recently freed slots are still warm in cache). `Entity.Id` is unique only among
  live entities.
- **Every access validates the version.** When an entity is destroyed its version is bumped, so every outstanding
  handle becomes stale. A stale or `default` handle answers questions with false (`IsAlive`, `Has`, `TryGet`,
  `TryGetRef`), throws `InvalidOperationException` on accesses and changes (`Get`, `Read`, `Add`, `Set`, `Remove`:
  "Entity is not alive"), and ignores `Destroy`. It never reaches the entity that reuses its id.
- Equality includes the version: two handles are equal only if they name the same incarnation.
- **Keep the `Entity`, never a raw id.** To look an id up, use `world.TryGetEntity(id, out e)` /
  `world.GetEntity(id)`, which return the current incarnation.
- `world.EntityCount` is the number of live entities; `world.Entities` enumerates them (diagnostics, O(capacity)).
- `world.EnsureEntityCapacity(n)` or `WorldOptions.InitialEntityCapacity` pre-sizes the entity records, so they
  never grow at run time (tables grow on their own).

An id that came from outside (a network message, a log line) turns back into a handle, and a handle tells whether it
still names a living entity:

```csharp
world.EnsureEntityCapacity(10_000);                 // a server that knows its peak never grows the entity array
var target = world.CreateEntity(new Health(100));
int fromClient = target.Id;                         // what a client sends back

if (world.TryGetEntity(fromClient, out var found) && found.Has<Health>())
    found.Get<Health>().Value -= 10;                // the current incarnation of that id

target.Destroy();
bool stale = !target.IsAlive;                       // true: every handle of a destroyed entity is stale
int alive = world.EntityCount;
foreach (var e in world.Entities) _ = e.Id;         // diagnostics: walks the whole id range
```

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
| `ref readonly T Read<T>()` | Read-only ref; throws if missing. Not a write, so tracked components stay unchanged. |
| `ref T TryGetRef<T>(out bool found)` | Ref into storage if present; a null ref when not (check `found`). |
| `bool Has<T1..Tn>()` | Whether the entity has all of them. |
| `bool TryGet<T>(out T value)` | A **copy** of `T` if present. |
| `ref T Add<T>()` | Adds `T` (or resets it to `default` if present) and returns a ref. |
| `ref T GetOrAdd<T>()` | Ref to `T`, adding it with `default` first if missing. |
| `Entity Set(in T1 … in Tn)` | Adds or overwrites 1-9 values, with at most one row copy; chainable. |
| `Entity Remove<T1..Tn>()` | Removes 1-9 components; the entity keeps its row. Missing ones are ignored; chainable. |

Always mutate through a `ref`; see [`ref` and `await`](refs-and-await.md). The `ref T` operations count as a write
of a tracked component ([change tracking](change-tracking.md)) whether or not you store through them, so read with
`Read` or `TryGet`, and write through `Get`, `TryGetRef` or `GetOrAdd`:

```csharp
if (world.TryGetEntity(unit.Id, out var hit))          // e.g. an id a client sent
{
    ref var hp = ref hit.TryGetRef<Health>(out bool alive);
    if (alive) hp.Value -= 10;                             // one lookup, written in place
    float x = hit.Read<Position>().X;                      // read-only: no change recorded
}
```

### Generated members

The source generator gives `Entity` and `World` named members for every component and singleton (C# 14 extension
members), so the common calls read like fields and methods:

| Declared | Generated | Same as |
|---|---|---|
| `record struct Position(float X, float Y) : IComponent` | `e.Position` (`ref`), `e.HasPosition`, `e.TryGetPosition(out p)`, `e.GetOrAddPosition()`, `e.SetPosition(x, y)`, `e.RemovePosition()` | `Get`, `Has`, `TryGet`, `GetOrAdd`, `Set(new Position(x, y))`, `Remove` |
| a tracked or indexed component (`Armor : ITrackChanges`) | `e.Armor` is `ref readonly`; the rest as above | `Read`: reading it is never a write |
| a tag (`struct Dead : IComponent`) | `e.IsDead`, `e.SetDead()`, `e.RemoveDead()` | `Has`, `Set(new Dead())`, `Remove` |
| an indexed component (`Zone : IIndexed<int>`) | `world.ZoneIndex` | `world.Index<Zone, int>()` |
| a singleton (`Battle : ISingletonComponent`) | `world.Battle` (`ref`), `world.HasBattle`, `world.SetBattle(…)`, `world.RemoveBattle()` | `GetSingleton`, `HasSingleton`, `GetOrAddSingleton() = …`, `RemoveSingleton` |

```csharp
hero.Position.X += 1;                                      // ref: written in place
if (hero.TryGetVelocity(out var v)) { /* a copy, or false when missing */ }
hero.SetVelocity(1, 0).SetFrozen();                        // constructor arguments; a tag; chainable
int armor = hero.Armor.Value;                              // tracked: ref readonly, never a write
hero.SetArmor(5);                                          // its writes are explicit (or Get<Armor>())
```

The property is `Get` (or `Read`), so it throws when the entity lacks the component, like `Get`: check `HasX` or use
`TryGetX` when it is optional (`TryGetRef<T>(out found)` for optional access by `ref`). `SetX` has one overload per
public constructor (record structs: the primary constructor), or takes every field when the struct has no constructor
and only public, writable fields. Writes are methods, not property setters: C# rejects a setter on a handle that is
not a variable, such as a `foreach` variable. Everything is an inlined call to the generic API, so generated and generic
code cost the same; the generic API stays for generic code. The analyzer and the generator know the generated members:
`SetX` / `RemoveX` count as structural changes, the others as reads. Generic components, components nested in types
other code cannot see and names the receiver already has (`Entity.Id`, ...) get no member; the project needs C# 14
(the .NET 10 default).

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
