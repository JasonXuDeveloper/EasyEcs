# Prefabs and cloning

[Docs index](../README.md) · Related: [Entities and components](entities-and-components.md) ·
[Change tracking](change-tracking.md) · [API reference](../api.md#prefabs-and-cloning)

A server spawns the same kinds of entities again and again: a goblin, a fireball, a loot chest. A `Prefab` is the
recipe of such a kind, a set of component values that belongs to no world. `world.Instantiate(prefab)` creates an
entity with a copy of every value. `entity.Clone()` does the same from a living entity: a new entity of the same world
with the entity's current components.

## At a glance

| | |
|---|---|
| **Use it for** | Spawning kinds of entities defined once (NPC types, projectiles, items), from code or from configuration; copying a living entity as it is now (a mirror image, a unit that splits in two). |
| **Avoid** | A prefab that changes while another thread instantiates it; an `Entity` field in a prefab meant to point at an entity of the target world (set relations after instantiating). |
| **Cost** | One row copy, O(components); the target table is found once per prefab table and cached. `Instantiate(prefab, span)` spawns a wave with one pass through the structural gate and at most one table growth. |

## Prefabs

Build a prefab once, keep it, and instantiate it wherever an entity of that kind is needed:

```csharp
public record struct Position(float X, float Y) : IComponent;
public record struct Health(int Value, int Max) : IComponent, ITrackChanges;
public record struct Attack(int Damage, int Cooldown) : IComponent;
public struct Npc : IComponent;                                         // a tag

public static class Kinds
{
    // Recipes: no world holds them, so no query ever sees them. The generated writers return the prefab, so they chain.
    public static readonly Prefab Goblin = new Prefab()
        .SetPosition(0, 0).SetHealth(60, 60).SetAttack(4, 0).SetNpc();
    public static readonly Prefab Troll = new Prefab()
        .Set(new Position(), new Health(300, 300), new Attack(12, 0)).SetNpc();   // or several values in one call
}
```

```csharp
var goblin = world.Instantiate(Kinds.Goblin);        // a new entity with a copy of every value
goblin.Position = new Position(100, 40);             // then write only what differs (generated member)

var wave = new Entity[500];
world.Instantiate(Kinds.Troll, wave);                // 500 trolls: one gate pass, at most one table growth
for (int i = 0; i < wave.Length; i++) wave[i].Position = new Position(i * 8, 0);
```

| `Prefab` member | Effect |
|---|---|
| `new Prefab()` | An empty recipe. |
| `Set(in T1 … in Tn)` → `Prefab` | Adds 1-9 values, or replaces the values of their types; chainable. |
| `Remove<T1..Tn>()` → `Prefab` | Drops 1-9 components the prefab has (missing ones are ignored); chainable. |
| `Has<T>()` | Whether the prefab has `T`. |
| `Read<T>()` → `ref readonly T` | The prefab's value of `T`; throws if it has none (change it with `Set`). |

### Generated members

Like an entity, a prefab gets [named members](entities-and-components.md#generated-members) for every component, so a
recipe reads like the entities it makes. The writers return the prefab, so they chain:

| Declared | Generated on `Prefab` | Same as |
|---|---|---|
| `record struct Health(int Value, int Max) : IComponent` | `prefab.Health` (`ref readonly`), `prefab.HasHealth`, `prefab.SetHealth(value, max)`, `prefab.RemoveHealth()` | `Read`, `Has`, `Set(new Health(value, max))`, `Remove` |
| a tag (`struct Npc : IComponent`) | `prefab.IsNpc`, `prefab.SetNpc()`, `prefab.RemoveNpc()` | `Has`, `Set(new Npc())`, `Remove` |

```csharp
var elite = new Prefab().SetPosition(0, 0).SetHealth(500, 500).SetAttack(20, 0).SetNpc();
int max = elite.Health.Max;                          // read-only: change it with SetHealth
if (elite.HasAttack) elite.SetAttack(25, 0);         // replaces the value
elite.RemoveNpc();                                   // the recipe drops the tag
```

The property is always read-only (`Read`): a prefab changes only through `Set` / `Remove` and their generated forms.
There is no `TryGetX` or `GetOrAddX` on a prefab. `SetX` has the same overloads as on an entity (one per public
constructor, or one taking the public fields). A prefab belongs to no world, so its writers are not structural changes of any world: the analyzer does
not report them in `[ForEach]` methods or on other threads (still, build and change prefabs at startup, as below).

A prefab is a plain object: build it from code, or from configuration at startup (one `Set` per configured component),
and keep it in a `static readonly` field or a table of kinds. Changing a prefab changes the entities instantiated
afterwards, not the ones that exist. One prefab may serve worlds on several threads (one world per thread) as long as
nobody changes it meanwhile.

### What a copy gets

`Instantiate` and `Clone` copy every component value as it is:

- **Values** are copied like any struct. A managed reference inside a component (a `string`, an array, a `List<T>`)
  is copied as a reference, so the copies share what it points to.
- **A [`DynamicBuffer<T>`](dynamic-buffers.md)** is the exception: every copy gets a list of its own with the same
  elements.
- **[Value indexes](indexes-and-relations.md)** learn the new entity at once, like after `CreateEntity`.
- **[Tracked components](change-tracking.md)** count as written now: `[Changed<T>]` methods see every new instance,
  so replication sends it like any other new entity. A component the copy lacks is not a removal: `[Removed<T>]` does
  not report it.
- **Absent components stay absent.** A clone of an entity whose `Burning` was removed has no `Burning` either.
- **An `Entity` field** is copied as the handle it holds. In a prefab it names no entity of the target world (the
  prefab belongs to no world), so give each instance its relations after instantiating:
  `shot.Set(new OwnedBy(player))`.

### Why a prefab is not an entity

Some ECS libraries keep prefabs as entities of the world: Unity DOTS and Flecs tag them, and every query has to
exclude that tag (they add the exclusion to every query by default); Friflo clones an ordinary entity of the store,
which its queries see like any other unless it is disabled. Either way the templates live among the entities that systems move, damage and
replicate. A `Prefab` in EasyEcs is data, not an entity: it lives outside every world, no
query can see it, systems need no filter for it, and one prefab can serve several worlds.

## Cloning a living entity

`entity.Clone()` creates a new entity of the same world with a copy of the entity's current components, under the
same rules as above (its own buffers, indexed, tracked components written now). Use it for runtime copies of a live
entity's state: a mirror image of a unit, a slime that splits in two, a snapshot of a player taken for a duel replay.
To create entities of a kind, use a prefab: it does not depend on the state of some entity that may change or die.

```csharp
public Entity Mirror(Entity unit) => unit.IsAlive ? unit.Clone() : default;   // health, statuses, bag, zone
```

`Clone` throws for a handle that is not alive.

## Rules and costs

- `Instantiate` and `Clone` are structural changes: owner thread only, allowed in the middle of a pass (the new
  entity is not visited by the pass that created it), and the analyzer treats them like `CreateEntity`
  ([EECS013](analyzer.md#eecs013), [EECS014](analyzer.md#eecs014)).
- A copy is one row copy, O(components). The first instance of a prefab looks up the target table with the prefab's
  component set; the world caches it, so later instances of the same prefab go straight to it. A clone stays in its
  original's table.
- `Instantiate(prefab, span)` takes the structural gate once for the whole wave and grows the target table at most
  once, like [`CreateEntities`](entities-and-components.md#entities). Use it for waves; use `CreateEntities` when
  the values are not a fixed kind.
- Instantiating allocates nothing after warm-up, except the list each `DynamicBuffer<T>` copy gets.

The [battle server](../example.md) defines its NPC kinds as prefabs (`Grunt`, `Brute`), instantiates them in bulk
when the battle starts and one by one when an NPC respawns, and `Mirror` clones a living unit.
