# Dynamic buffers

[Docs index](../README.md) · Related: [Change tracking](change-tracking.md) ·
[Entities and components](entities-and-components.md) · [Snapshots](snapshots.md) ·
[ECS principles, rule 8](../ecs-principles.md#8-choose-how-to-hold-a-collection) · [API reference](../api.md#dynamic-buffers)

A `DynamicBuffer<T>` is a list per entity, held as a component: a bag of loot, a path, the hits of this tick. It is a
component like any other (add it, query it, take it as a `[ForEach]` parameter), and every write to the list is a
tracked change: `[Changed<DynamicBuffer<T>>]` visits only the entities whose list changed.

## At a glance

| | |
|---|---|
| **Use it for** | Lists that belong to one entity and are replicated or saved when they change: a bag of item ids or stacks, a path, recent events, pending orders. |
| **Avoid** | Items with their own state, queried or changed on their own (one entity per item with a [relation](indexes-and-relations.md#relations)); small fixed sets (a fixed `[InlineArray]` array in an unmanaged component); data read and saved as a whole (a collection in a component). See [choosing](#choosing-how-to-hold-a-list). |
| **Cost** | One object and one array per entity, made by its first write; adding within the capacity allocates nothing; a write stamps the row once, like any tracked component. 1,000 of 100,000 bags written and sent: 55.1 μs, against 61.5 μs for a `List<T>` in a tracked component (`DynamicBufferBenchmark`). |

## Example

```csharp
public record struct Loot(int Item);
public record struct Sent(int Items) : IComponent;

public partial class Pickup : EcsSystem
{
    [ForEach, With<Player>]
    private static void Collect(ref DynamicBuffer<Loot> bag)           // ref: a tracked write
    {
        if (bag.Count == 8) bag.RemoveAt(0);                    // a full bag drops its oldest item
        bag.Add(new Loot(1));
    }
}

public partial class SendBags : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Loot>>]                             // only bags written since the last run
    private static void Send(in DynamicBuffer<Loot> bag, ref Sent sent)
    {
        int items = 0;
        foreach (var loot in bag) items += loot.Item;           // in: read-only, never a write
        sent = new Sent(items);
    }
}

public struct Player : IComponent;
```

Create the entities with an empty buffer; each entity gets its own list on its first write:

```csharp
var players = new Entity[1_000];
world.CreateEntities(players, new DynamicBuffer<Loot>(), new Sent(), new Player());   // one value, a list per entity
foreach (var player in players) player.Get<DynamicBuffer<Loot>>().EnsureCapacity(8);  // reserve room: no growth later
```

## Choosing how to hold a list

| The list | Hold it as |
|---|---|
| Plain values only its owner uses, sent or saved when it changes: item ids or stacks, waypoints, the hits of a tick | **`DynamicBuffer<T>`** |
| Items with state of their own (durability, buffs with timers), that systems query or change one by one | **one entity per item** with a [relation](indexes-and-relations.md#relations) to the owner |
| A few values with a fixed maximum (4 cooldown slots, the last 8 inputs) | **a fixed `[InlineArray]` array** in an unmanaged component: no GC object at all |
| Data read and written as a whole with the save (quest progress, settings) | **a collection in a component**, changed through `ref` |

A player's bag of loot ids is a buffer; a sword with durability and enchantments is an entity that points at its
owner. The full table, with read-mostly and immutable data, is
[ECS principles, rule 8](../ecs-principles.md#8-choose-how-to-hold-a-collection).

## Reading and writing

| Read (`in`, `Read<T>()`, a `ref`) | Write (a `ref` only: `ref` parameter, `Get<T>()`, `TryGetRef<T>`) |
|---|---|
| `Count`, `Capacity`, `bag[i]` (read-only), `AsReadOnlySpan()`, `foreach` | `Add`, `AddRange`, `Insert`, `RemoveAt`, `RemoveAtSwapBack`, `Clear`, `EnsureCapacity`, `TrimExcess`, `AsSpan()` (writable) |

The writes are extension methods that take the buffer by `ref`, so the compiler rejects them on an `in` parameter or
on `Read<T>()`: a list cannot change without a tracked write. `Clear` keeps the capacity, so a list refilled every
tick allocates nothing once it reached its size; `TrimExcess` gives back what a list that once grew large no longer
needs (an empty list then holds no array).

- **Spans are views.** A span from `AsSpan()` / `AsReadOnlySpan()` (and a `foreach`) reads the list's current array:
  once `Add`, `AddRange`, `Insert`, `EnsureCapacity` or `TrimExcess` reallocates it, take a new span.
- **No inline capacity.** Every list lives in its own array on the heap, made by the first write; reserve room with
  `EnsureCapacity` where the size is known (a full bag never grows). For a few values with a fixed maximum use a fixed
  `[InlineArray]` array instead.
- **Parallel methods may write it.** A `ref DynamicBuffer<T>` parameter of a `[ForEach(Parallel = true)]` method
  writes only its own entity's list, like any component.

## Copies

The list lives in an object the component refers to, so a copy of a `DynamicBuffer<T>` value would refer to the same
list: writes through the copy would not be stamped, and two entities would share one list. The analyzer rejects every
copy (EECS012): a `DynamicBuffer<T>` field, property, by-value parameter or return, local, assignment or stored
argument. Use `ref` and `in` parameters, `ref` locals and `Entity.Get` / `Read`. A new value is `new DynamicBuffer<T>()`
(or `default`), which holds no list.

```csharp
ref var bag = ref player.Get<DynamicBuffer<Loot>>();    // a ref local: fine
// var copy = player.Read<DynamicBuffer<Loot>>();       // EECS012: a copy
```

## Moves, removal and snapshots

A row that moves to another table (a component added, a status removed) keeps its list. Removing the
`DynamicBuffer<T>` component or destroying the entity drops the list (the garbage collector frees it); adding the
component back starts with no list. `world.Capture()` copies the elements, and `Restore` copies them back: a world and
its snapshot never share a list, and a snapshot can be restored many times.
