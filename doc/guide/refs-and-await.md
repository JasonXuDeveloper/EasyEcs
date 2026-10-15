# `ref` everywhere, and `ref` across `await`

[Docs index](../README.md) · Related: [Entities and components](entities-and-components.md) ·
[Structural changes](structural-changes.md)

Components are structs stored in arrays. Always mutate them through a `ref`; changing a local copy changes nothing in
storage.

```csharp
var copy = entity.Get<Health>();       // ❌ a copy: the change below is lost
copy.Value -= 10;

ref var hp = ref entity.Get<Health>(); // ✅ a ref into storage
hp.Value -= 10;
entity.Get<Health>().Value -= 10;      // ✅ same thing, inline
```

**A `ref` into ECS storage is valid only until the next structural change.** Storage arrays grow, and are
reallocated, when entities, component types or archetypes are added, whether by your code or by another system. An
old `ref` then silently points at the stale copy. That is why `ref` and `await` must not mix:

- C# forbids `ref` locals that live across an `await`. Don't smuggle refs through `Unsafe`, pointers or `Span`
  fields either.
- While you `await`, other code runs. It may add entities or components (reallocating storage), or destroy your
  entity and recycle its id.
- After an `await`, **re-acquire** the data through the `Entity` handle. The handle is version-checked and throws
  if the entity was destroyed.

```csharp
async ValueTask HealFromStoreAsync(Entity player, IShop shop)
{
    long id = player.Get<Account>().Id;            // read what you need before awaiting
    int heal = await shop.RedeemPotionAsync(id);   // the world may change meanwhile
    if (player.IsAlive && player.Has<Health>())    // re-validate after the await
        player.Get<Health>().Value += heal;        // a fresh ref into current storage
}
```

## Rules

- Use `ref` freely inside `[ForEach]`, `ForEach` and `Query` bodies. They are synchronous and refresh their view of
  storage after structural changes.
- Never keep a `ref` in a field or across an `await`. Keep the `Entity` instead.
- Read what you need before awaiting; after the await, write back through a newly acquired ref.
- Don't store raw entity ids without their version: ids are recycled.

Singletons are the exception to "storage moves": a singleton's storage is stable, so `world.SingletonRef<T>()` can be
kept (see [Singletons](singletons.md#stable-handles)).
