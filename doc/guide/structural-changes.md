# Structural changes during iteration

[Docs index](../README.md) · Related: [Queries](queries.md) · [Systems](systems.md) ·
[Architecture: iteration](../architecture.md#iteration-and-structural-changes)

A structural change is anything that changes an entity's component set: creating or destroying entities, adding or
removing components. EasyEcs is an **immediate-mode** ECS: changes apply at once, there is no command buffer to
flush, and later code in the same pass and later systems in the same update see them.

## At a glance

| | |
|---|---|
| **Use it for** | Changing entities where the decision is made: set `Dead` in the death system, destroy an expired projectile in its own loop. |
| **Avoid** | Keeping a `ref` (other than the method's own `ref` parameters) across a structural change; changing structure in batch or parallel methods. |
| **Cost** | A pass that may change structure checks one counter per entity; methods that cannot change structure skip it. |

## During a pass

Inside `[ForEach]`, `ForEach` and `Query`, you can create and destroy entities and add or remove components. The
rules:

- **A pass visits each entity at most once.**
- **Entities that stop matching before their turn are skipped**: destroyed, or a queried component removed, or an
  excluded one added.
- **Entities that join a matching table during the pass are not visited by it**: created, or moved into a matching
  archetype. The next pass sees them. An entity that gets a queried component back without moving (its row only
  flips a presence bit) is visited if the pass has not reached its row yet.

```csharp
public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]
    static void Die(Entity unit, in Health hp, ref Score score)
    {
        if (hp.Value > 0) return;
        unit.Set(new Dead());   // applied now: later systems of this update already see the corpse
        score.Deaths++;
    }
}
```

## Other rules


- Each entity gets fresh refs. The `ref` parameters of a `[ForEach]` method or `ForEach` job stay writable when the
  body changes the visited entity's archetype (the writes are carried over to its new row). Any other `ref` (from
  `entity.Get<T>()`, a `Query` item, or another entity) is only guaranteed until the next structural change: write
  through it before the change, or re-read through `entity.Get<T>()` afterwards (see
  [`ref` and `await`](refs-and-await.md)).
- Batch `[ForEach]` methods (span parameters) and `ForEachBatch` jobs receive spans into the tables, so they must not
  make structural changes.
- Don't call `Update()` / `Update<TGroup>()` or `CompactArchetypes()` while a query of the same world is iterating.
- Removing a component and adding it back is cheap: the entity keeps its row and only a presence bit flips. Gaining a
  component the entity's archetype never had copies its row once
  ([ECS principles, rule 5](../ecs-principles.md#5-choose-the-cheapest-representation-for-frequent-changes)).
