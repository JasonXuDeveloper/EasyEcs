# Indexes and relations

[Docs index](../README.md) · Related: [Change tracking](change-tracking.md) · [Entities and components](entities-and-components.md) ·
[API reference](../api.md#indexes)

Queries find entities by the components they have. To find them by a value (the player named "alpha", the items an
entity owns, the children of a node), let the world index the component: implement `IIndexed<TKey>` and look up
through `world.Index<T, TKey>()`.

## At a glance

| | |
|---|---|
| **Use it for** | Lookups by value (a player by name) and relations (an inventory of items with their own state: each item points at its owner; children of a node). A bag of plain item ids is a [dynamic buffer](dynamic-buffers.md). |
| **Avoid** | Dictionaries kept in sync by hand; writing an indexed component through a `ref` (EECS015: use `Set`). |
| **Cost** | `Count` a hash probe (an array access for relations), `Find` one step per match; a `Set` that changes the key moves the entity between two lists. |

## Value indexes

```csharp
public record struct Callsign(string Value) : IComponent, IIndexed<string>
{
    public string Key => Value;
}

var callsigns = world.Index<Callsign, string>();      // created on first use; keep it in a local or field
if (callsigns.TryFind("alpha", out Entity alpha)) { /* ... */ }
int named = callsigns.Count("bravo");
foreach (Entity e in callsigns.Find("charlie")) { /* every entity with that key */ }

alpha.SetCallsign("delta");                           // a write (Set): the index moves alpha at once
string name = alpha.Callsign.Value;                   // the generated property reads it (ref readonly)
int deltas = world.CallsignIndex.Count("delta");      // the generated index property
```

## Relations

A relation is a component holding an `Entity`, indexed by that entity. The index answers the reverse question (which
entities point at this one) without any bookkeeping in your systems:

```csharp
public record struct ChildOf(Entity Parent) : IComponent, IIndexed<Entity>
{
    public Entity Key => Parent;
}

var children = world.Index<ChildOf, Entity>();
var ship = world.CreateEntity(new Health(500));
world.CreateEntity(new ChildOf(ship), new Health(50));   // a turret
world.CreateEntity(new ChildOf(ship), new Health(50));

foreach (Entity turret in children.Find(ship)) turret.Get<Health>().Value -= 10;
```

An entity holds one component of each type, so a relation with many targets (an inventory, a guild's members) is one
entity per link pointing at its owner: `ItemOf(Entity Owner)` on each item, `items.Find(player)` for the inventory.
Handles carry their version, so a link to a destroyed entity never matches a later entity that reuses its id. What
happens to the children of a destroyed parent is a rule of your game, so write it as a system, like any other state
change:

```csharp
public partial class DestroyOrphans : EcsSystem
{
    // Destroy the children of dying ships before the ships go (instance method: it runs alone).
    [ForEach, With<Dead>]
    private void Cascade(Entity ship)
    {
        foreach (Entity child in World.Index<ChildOf, Entity>().Find(ship)) child.Destroy();
    }
}
```

## How it stays correct

The index follows the writes that can change a key: `Set`, `CreateEntity` and `CreateEntities` re-index the entity
on the spot, and destroying an entity or removing the component unlinks it. A write through a `ref` would change the
key behind the index's back, so the analyzer rejects it (EECS015): `Get<T>()` of an indexed component, a `ref`
local or `ref` / `Span<T>` `[ForEach]` parameter of it, and assignments to it through a query item. Read it with
`in` parameters, `Read<T>()` or the generated property (`ref readonly`), and write it with `Set` (or the generated `SetX`). `Set` with the key
the entity already has re-links nothing, and a write that keeps the key is still a `Set` (compare first to skip
it: `if (zone.Id != id) player.Set(new Zone(id));`).

The index is built by its first lookup (one walk of the matching tables) and from then on costs nothing until a
write; `world.Restore` rebuilds it from the restored world.

Costs: `Count` is a hash probe (an array access for a relation to an entity of the same world) and `Find` adds one
step per entity of the key. A `Set` of an indexed component is a plain write plus, when the key changed, an unlink
and a link (a few array writes; a hash probe for non-entity keys). A null key (the default of a `string` key) indexes the entity nowhere. The index holds one
node per indexed entity; it allocates only when it sees a new key or a larger entity id, never per lookup.

Like component values, an index belongs to the world's owner thread: use it from `OnUpdate`, instance `[ForEach]`
methods or `World`-taking methods (which run alone), not from another thread. Destroying an entity or removing an
indexed component updates the index too, so do those on the owner thread as well while the world has an index. A
lookup inside a pass that writes the indexed component sees every write of that pass; changing the component while a
`Find` loop over the same index runs is not supported (destroying entities is).
