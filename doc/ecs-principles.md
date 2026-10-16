# ECS principles

[Docs index](README.md) · Related: [Getting started](getting-started.md) · [Analyzer rules](guide/analyzer.md)

EasyEcs separates **data** (entities, components, singletons) from **behavior** (systems). That separation is
what makes iteration fast (dense columns, no virtual calls per entity), and it is also what keeps a large code base
decoupled: systems never know about each other, only about data. This page explains each role and how to design
features with them. The analyzer enforces the mechanical parts ([EECS006–008](guide/analyzer.md)).

## Roles

```mermaid
flowchart LR
    subgraph Data
        E[Entity<br/>identity]
        C[Component<br/>per-entity data]
        S[Singleton<br/>per-world data]
    end
    subgraph Behavior
        A[System A]
        B[System B]
        H[System C]
    end
    A -->|reads / writes| C
    A -->|reads / writes| S
    B -->|reads / writes| C
    H -->|reads| C
    H -->|reads / writes| S
    E -.->|has| C
```

There are no arrows between systems. Every dependency goes through data, and every piece of behavior runs inside
the schedule, in the order `Priority` declares.

| Element | Is | Is not |
|---|---|---|
| **Entity** | An identity (id + version), held as a handle. | A container of logic, a base class, a place for fields. |
| **Component** | A struct of fields for one aspect of one entity (`Health`, `Velocity`). Computed getters are fine. | A class, an object with behavior, a holder of references to systems or services. |
| **Tag** | An empty component (`Dead`, `Frozen`) used as a query filter. | A boolean you flip every tick (use a field instead). |
| **Singleton** | One struct per world: clock, configuration, counters, a spatial grid. | A static field (it would be shared by every world). |
| **System** | Behavior with one responsibility. Reads and writes components and singletons. | A place to keep state, or a service other systems call. |

## Rules and how to follow them

The examples share these components:

```csharp
public record struct Position(float X, float Y) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Burning(int Ticks, int DamagePerTick) : IComponent;   // a status effect
public struct Dead : IComponent;                                           // a tag
```

### 1. One system, one responsibility

Name a system after the one thing it does. If its description needs "and", split it.

```csharp
// Before: burning AND death in one system. Death is only noticed for burning units:
// a unit killed by anything else is never marked Dead.
public partial class StatusSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Tick(Entity unit, ref Burning burning, ref Health hp)
    {
        hp.Value -= burning.DamagePerTick;
        if (--burning.Ticks == 0) unit.Remove<Burning>();
        if (hp.Value <= 0) unit.Set(new Dead());
    }
}
```

```csharp
// After: each concern can be removed, reordered or tested on its own, and death covers every damage source.
public partial class BurnSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Burn(Entity unit, ref Burning burning, ref Health hp)
    {
        hp.Value -= burning.DamagePerTick;
        if (--burning.Ticks == 0) unit.Remove<Burning>();
    }
}

public partial class DeathSystem : EcsSystem
{
    public override int Priority => 10;   // after every system that deals damage

    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp)   // `in`: it only reads Health
    {
        if (hp.Value <= 0) unit.Set(new Dead());
    }
}
```

Splitting costs nothing at run time: every `[ForEach]` method is its own inlined struct job either way, and the
per-system overhead of `Update` is three virtual calls (`UpdateInterval`, `OnUpdate`, `GeneratedUpdate`) plus one
query-cache lookup per `[ForEach]` method and one lookup per singleton parameter. When in doubt, split.

### 2. Systems are stateless

State in a system is invisible to queries, to other systems, to tests and to persistence, and it disappears or
doubles when the system is removed or added twice. Put it in data:

| State | Where |
|---|---|
| Per entity (cooldown, target, path) | a component |
| Per world (tick, score, configuration, spatial grid) | a singleton |
| Lookups by value (a player by name, the items of an owner) | an indexed component ([indexes and relations](guide/indexes-and-relations.md)), not a dictionary kept in sync by hand |
| Derived and rebuildable (scratch list reused every update) | a `readonly` field on the system is fine |

```cs
// Before (EECS007): the count lives in the system, where no query, test or other system can see it.
public partial class DeathSystem : EcsSystem
{
    public int Deaths;

    [ForEach, Without<Dead>]
    void Die(Entity unit, in Health hp)
    {
        if (hp.Value > 0) return;
        unit.Set(new Dead());
        Deaths++;
    }
}
```

```csharp
// After: the count is world state; anyone can read it (world.GetSingleton<Score>().Deaths).
public record struct Score(int Deaths) : ISingletonComponent;

public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp, ref Score score)   // a singleton parameter
    {
        if (hp.Value > 0) return;
        unit.Set(new Dead());
        score.Deaths++;
    }
}
```

Readonly fields for dependencies or reusable scratch buffers are fine on a system (EECS007 allows them).

### 3. Systems never reference systems

A system that calls another system (EECS008), or reaches it through `World.GetSystem<T>()`, can no longer be
removed, reordered or tested alone. Cooperate through data instead:

- **Pipeline:** an earlier system writes a component, a later one reads it. Order them with `Priority`.
- **Reacting to a state:** a system queries the state another one wrote; the writer does not change when a reader is
  added (rule 4).

```csharp
// Reacts to Dead; DeathSystem does not know it exists.
public partial class CorpseSystem : EcsSystem
{
    public override int Priority => 20;   // after DeathSystem

    [ForEach, With<Dead>]
    static void Remove(Entity unit) => unit.Destroy();
}
```

```mermaid
flowchart LR
    I[InputSystem] -->|writes Velocity| M[MovementSystem]
    M -->|writes Position| G[AoiSystem]
    D[DeathSystem] -->|sets Dead| R[RespawnSystem]
    D -->|sets Dead| C[CorpseSystem]
```

### 4. State changes are data

When something happens to an entity, write its new state: set a tag (`Dead`) or a field. Systems that care query
that state later in the update, in the order `Priority` declares: movement and combat skip `Dead` entities,
`CorpseSystem` removes them, a `RespawnSystem` could revive players. None of them is known to `DeathSystem`.

### 5. Choose the cheapest representation for frequent changes

Removing a component, or adding back one the entity had before, flips a presence bit: the entity keeps its row.
Gaining a component its archetype has no column for copies the entity's row into another table, once. Tags for
states that come and go (`Stunned`, `Burning`, `Dead`) are therefore cheap. For values that change every tick, a field
is still cheaper than a structural change:

- a countdown field (`Burning.Ticks`) instead of re-adding a component every tick;
- change detection by change ticks: mark the component `ITrackChanges` and give the consumer `[Changed<T>]`, instead
  of adding and removing a `Changed` tag or scanning every row ([change tracking](guide/change-tracking.md)). A
  write is any `ref` access, so compare values when a write may leave the value as it was.

```csharp
public record struct Facing(float Angle) : IComponent, ITrackChanges;  // tracked: rows record their last write
public record struct NetSync(Facing Sent) : IComponent;               // what clients last heard
public record struct Outbox(int Updates) : ISingletonComponent;

public partial class SyncSystem : EcsSystem
{
    [ForEach, Changed<Facing>]   // only entities whose Facing was written since this system last ran
    static void Sync(in Facing facing, ref NetSync sync, ref Outbox outbox)
    {
        if (facing == sync.Sent) return;   // written but unchanged: nothing to send
        sync = new NetSync(facing);
        outbox.Updates++;                  // a real server queues the update here
    }
}
```

### 6. Let the schedule order effects

Changes are immediate: a component a system adds is visible to every later system of the same update, and to the
rest of the same pass. Within one pass each entity is visited at most once, and entities that join a matching table
during the pass (created, or moved into a matching archetype) wait for the next pass. So the order of effects is the order of
systems, never the order a system happens to visit entities in:

```csharp
public partial class DeathSystem : EcsSystem
{
    public override int Priority => 10;   // after combat, before respawn and loot

    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp)
    {
        if (hp.Value <= 0) unit.Set(new Dead());   // systems after DeathSystem see Dead in this update
    }
}
```

The full rules are in [Structural changes during iteration](guide/structural-changes.md).

### 7. Keep entity handles, never `ref`s, across boundaries

A `ref` from `Get<T>()` or a `Query` item is valid until the next structural change (any entity's); the `ref`
parameters of a `[ForEach]` method stay valid through its own body's changes. None is valid across `await`. Keep `Entity` handles (validated on every access) and re-read components when you need them.
See [`ref` and `await`](guide/refs-and-await.md).

### 8. Choose how to hold a collection

A component may hold a `List<T>`, a `Dictionary<K, V>` or an array, but its row stores only the reference. The world
sees a write when code takes the component by `ref`, not when the collection behind it changes. So:

- **Change it only through `ref`** (a `ref` parameter, `Get`, `TryGetRef`): the row is stamped for `[Changed<T>]` and
  indexes, and parallel systems schedule the write. Changing it through an `in` parameter is a race and goes
  unnoticed; the analyzer reports it (EECS011).
- **Snapshots share it**: `Capture` copies the reference, not the collection, so restoring brings back the list as it
  is now. Snapshots are for plain data; persistence serializes the collection anyway. A `Buffer<T>` has neither
  problem: its writes need a `ref`, and snapshots copy its elements.

Then pick the representation by what the items are:

| Items | Hold them as | Why |
|---|---|---|
| Things with their own state, queried or changed on their own (inventory items, guild members, buffs) | **one entity per item** with a relation to its owner ([indexes and relations](guide/indexes-and-relations.md#relations)) | each item has its own components, change ticks, queries and parallel schedule, copied by value in snapshots |
| A list that belongs to one entity, sent or saved when it changes (a bag of loot, a path, recent hits) | **a [`Buffer<T>`](guide/buffers.md)** component | every write needs a `ref`, so each change is tracked; snapshots copy the elements; a write through `in` does not compile |
| A small, bounded set of values (cooldown slots, the last few inputs) | **an `[InlineArray]` buffer** in an unmanaged component | no GC, copied by value everywhere, every change a `ref` write |
| Data read and written as a whole, usually with the save (quest progress, settings, unlocked achievements) | **a collection in a component**, changed through `ref` | one row per player instead of thousands of entities; loaded and saved with the player |
| Read-mostly data replaced as a whole (a patrol route) | **an immutable collection** (`ImmutableArray<T>`) | assigning a new one through `ref` is a tracked write; a snapshot keeps the old one |

```csharp
[System.Runtime.CompilerServices.InlineArray(4)]
public struct Slots4 { private int _first; }                                 // 4 ints stored inline

public struct Cooldowns : IComponent, ITrackChanges { public Slots4 Ticks; }  // unmanaged: nothing to share
public struct Quests : IComponent, ITrackChanges                             // saved and loaded with the player
{
    public System.Collections.Generic.Dictionary<int, int> Progress;
}

public record struct ItemOf(Entity Owner) : IComponent, IIndexed<Entity>     // an inventory: items point at players
{
    public Entity Key => Owner;
}

public partial class CooldownSystem : EcsSystem
{
    [ForEach]
    static void Tick(ref Cooldowns cooldowns)                                 // ref: scheduled and stamped as a write
    {
        for (int i = 0; i < 4; i++)
            if (cooldowns.Ticks[i] > 0) cooldowns.Ticks[i]--;
    }
}

public static class PlayerRules
{
    public static void Advance(Entity player, int quest)
    {
        var progress = player.Get<Quests>().Progress;                         // Get: the row is stamped
        progress[quest] = progress.TryGetValue(quest, out int step) ? step + 1 : 1;
    }

    public static void Give(World world, Entity player, int item) =>
        world.CreateEntity(new ItemOf(player), new Health(item));             // a new row: tracked, indexed, snapshotted

    public static int Carried(World world, Entity player) => world.Index<ItemOf, Entity>().Count(player);
}
```

## Checklist for a new feature

1. List the data: which components (per entity) and singletons (per world) does it need?
2. List the behaviors, one system each, named by what it does.
3. Order systems by data flow (`Priority`): producers before consumers.
4. Express cross-module reactions as systems querying the state another system wrote, never as calls between
   systems.
5. Build: the analyzer rejects what breaks these rules.
6. Pick how each collection is held ([rule 8](#8-choose-how-to-hold-a-collection)).
6. Test each system alone: create a world, add the system and the data it reads, `Update()`, assert on data.
