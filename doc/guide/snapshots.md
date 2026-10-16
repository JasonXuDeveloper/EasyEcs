# Snapshots

[Docs index](../README.md) · Related: [Entities and components](entities-and-components.md) ·
[API reference](../api.md#systems-singletons-maintenance)

`world.Capture()` copies the world's entities, their components and its singletons into a `WorldSnapshot`;
`world.Restore(snapshot)` puts them back. Use it to undo a turn, to rewind a simulation for client-side prediction or
replays, or to reset a world between tests.

## At a glance

| | |
|---|---|
| **Use it for** | Undoing a turn, rewinding for prediction or replays, resetting a world between tests. |
| **Avoid** | Persistence (a snapshot is in memory and shares managed references), capturing millions of entities every tick. |
| **Cost** | O(entities × components) for `Capture` and for `Restore`. |

## Usage

```csharp
var before = world.Capture();          // O(entities × components)

world.Update();                        // simulate a turn
if (!running) world.Restore(before);   // undo it: same entities, same values, same singletons

// A snapshot can be restored any number of times.
world.Restore(before);
```

What a snapshot holds, and what `Restore` does:

- **Entities and components**, copied by value. A managed reference inside a component (a `string`, an array) is
  shared with the world, not cloned: keep components plain data (collections as entities or values, [ECS principles,
  rule 8](../ecs-principles.md#8-hold-collections-as-entities-or-by-value-not-as-shared-references)).
- **Singletons**, copied by value. After `Restore` every singleton has the snapshot's value in its existing storage,
  so a `SingletonRef<T>` taken before keeps working; singletons added after the capture are removed.
- **Handles.** Handles to entities alive in the snapshot are valid again. Every other handle is stale, including
  handles to entities created after the capture, even when a later entity reuses their id.
- **Change ticks and indexes.** Every restored row of a tracked component counts as written, so `[Changed<T>]`
  methods see all of them on their next run ([change tracking](change-tracking.md)), and value indexes re-index from
  scratch on their next lookup ([indexes and relations](indexes-and-relations.md)).
- **Not systems or options.** Systems keep running as registered; archetypes created after the capture stay, empty.

Both calls take the world's structural gate and throw while a query iterates, so call them between updates (or from a
system's `OnUpdate`, outside its `[ForEach]` methods). A snapshot belongs to the world it was taken from.

Snapshots are in memory and O(entities × components) both ways: cheap enough every few ticks for tens of thousands of
entities, not every tick for millions. They are not a save format: to persist a world, write your components out with
your own serializer.
