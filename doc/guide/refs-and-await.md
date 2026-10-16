# `ref` everywhere, and `ref` across `await`

[Docs index](../README.md) · Related: [Entities and components](entities-and-components.md) ·
[Structural changes](structural-changes.md)

Components are structs stored in arrays. Always mutate them through a `ref`; changing a local copy changes nothing in
storage.

## At a glance

| | |
|---|---|
| **Use it for** | Mutating components in place through `ref`. |
| **Avoid** | Modifying copies, keeping `ref`s in fields or across `await`. |
| **Cost** | A `ref` is a pointer into a column: no copy, no lookup. |

## Copies and refs

```csharp
var copy = entity.Get<Health>();       // ❌ a copy: the change below is lost
copy.Value -= 10;

ref var hp = ref entity.Get<Health>(); // ✅ a ref into storage
hp.Value -= 10;
entity.Get<Health>().Value -= 10;      // ✅ same thing, inline
```

**A `ref` into ECS storage is valid only until the next structural change.** Components live in archetype tables:
an entity's row is copied to another table when it gains a new component, the last row of a table fills the row of an
entity that left it, and a full table is reallocated when it grows. An old `ref` then silently points at a stale copy
or at another entity's data. Which change moves which row is an implementation detail, and it may come from your code
or from another system, so treat every structural change, of any entity, as possibly invalidating your refs. That is why `ref` and `await` must not mix:

- C# forbids `ref` locals that live across an `await`. Don't smuggle refs through `Unsafe`, pointers or `Span`
  fields either.
- While you `await`, other code runs. It may add entities or components (reallocating storage), or destroy your
  entity and recycle its id.
- After an `await`, **re-acquire** the data through the `Entity` handle. The handle is version-checked and throws
  if the entity was destroyed.

```csharp
// Account is a component (record struct Account(long Id) : IComponent); IShop is your service.
async ValueTask HealFromStoreAsync(Entity player, IShop shop)
{
    long id = player.Get<Account>().Id;            // read what you need before awaiting
    int heal = await shop.RedeemPotionAsync(id);   // the world may change meanwhile
    if (player.IsAlive && player.Has<Health>())    // re-validate after the await
        player.Get<Health>().Value += heal;        // a fresh ref into current storage
}
```

## Rules

- Use `ref` freely inside `[ForEach]`, `ForEach` and `Query` bodies: each entity gets fresh refs.
  - The `ref` parameters of a `[ForEach]` method (or `ForEach` job) stay writable even when the body changes the
    archetype of the entity it visits: the pass copies those writes to the entity's new row after the body returns.
    `in` parameters keep showing the values from before the change.
  - Any other `ref` (from `entity.Get<T>()`, `item.ComponentN` in a `Query` loop, or into another entity) is only
    guaranteed until the next structural change. Write through it before the change, or re-read through
    `entity.Get<T>()` afterwards.
- Never keep a `ref` in a field or across an `await`. Keep the `Entity` instead.
- Read what you need before awaiting; after the await, write back through a newly acquired ref.
- Don't store raw entity ids without their version: ids are recycled.

Singletons are the exception to "storage moves": a singleton's storage is stable, so `world.SingletonRef<T>()` can be
kept (see [Singletons](singletons.md#stable-handles)).
