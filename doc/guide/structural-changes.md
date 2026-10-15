# Structural changes during iteration

[Docs index](../README.md) · Related: [Queries](queries.md) · [Systems](systems.md) ·
[Architecture: iteration](../architecture.md#iteration-and-structural-changes)

A structural change is anything that changes which archetype an entity lives in: creating or destroying entities,
adding or removing components. EasyEcs is an **immediate-mode** ECS: changes apply at once, there is no command buffer
to flush, and later systems in the same update see them.

Inside `[ForEach]`, `ForEach` and `Query`, you can create and destroy entities and add or remove components. The
rules:

- **Removed entities are skipped.**
- **Entities created during the pass may or may not be visited.**
- **An entity that moves into another matching archetype during the pass may be visited again.** For example, a
  method that adds `Dead` to an entity it still matches will see that entity again.

Make such methods idempotent with a filter that excludes what the method produces. This is the usual rule for
immediate-mode ECS:

```csharp
public partial class DeathSystem : EcsSystem
{
    [ForEach, Without<Dead>]   // the job adds Dead, so it skips entities that already have it
    static void Die(Entity unit, in Health hp)
    {
        if (hp.Value <= 0) unit.Set(new Dead());
    }
}
```

Other rules:

- `ref` parameters and `item.ComponentN` refs are refreshed by the iterator after a structural change; a `ref` you
  stored yourself is not (see [`ref` and `await`](refs-and-await.md)).
- Never call `world.CompactArchetypes()` while a query is iterating; call it between updates.
- Prefer a field to adding and removing a component for state that flips every tick
  ([ECS principles, rule 5](../ecs-principles.md#5-choose-the-cheapest-representation-for-frequent-changes)).
