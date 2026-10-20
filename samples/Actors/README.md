# Actors

Actors on an ECS world: entities that answer messages addressed to their key, each kind of actor with its own
systems, loaded from storage when first needed and saved only when they change. Read [GameLoop](../GameLoop) and
[SaveLoad](../SaveLoad) first.

## What you learn

- **One type per kind of actor.** `Shop` and `Bank` each implement one generic interface, `IActor<TSelf>`, and so are
  at once the actor's key component (indexed: `world.ShopIndex.TryFind(key, ...)`), the system group of the kind
  (`world.Update<Shop>()`), and that group's filter (`IGroupWith<In<Shop>>`: the shop systems see only shops).
  Attributes cannot name a type parameter; the interface does it once for every kind.
- **A message is a job on one entity.** `entity.Run(ref job, filter)` gives the job all its components in one
  lookup, refuses a closed shop through a `static readonly` filter, and keeps the job's refs valid when the job
  closes the shop in the middle (a structural change).
- **Activation and records.** An actor's entity is made when it is first needed; its record, a `ComponentSet` of the
  persistent components, is loaded onto it with `LoadEntitiesInto(..., markWritten: false)`: it replaces the
  prefab's defaults and loading is not a change. A new actor has no record, so its defaults are its first state and
  the next save writes them.
- **Save what changed.** The persistent components are `ITrackChanges`; the host saves the actors written since its
  last save, and an actor nobody wrote costs nothing.

The key is implemented explicitly (`Shop IIndexed<Shop>.Key => this;`): a public `Key => this` property on a record
struct makes its `ToString()` print `Key` forever.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | the `In<TActor>` tag, the `IActor<TSelf>` interface, `Shop` and `Bank`, the persistent `Stock` and `Balance`, the in-memory `Sales` and `Closed` |
| [Messages.cs](Messages.cs) | the `Buy` and `Deposit` jobs, and `Mailman`, which runs each on its actor |
| [Systems/ShopSystems.cs](Systems/ShopSystems.cs) | `RestockSystem`, in the shop group |
| [Systems/BankSystems.cs](Systems/BankSystems.cs) | `InterestSystem`, in the bank group |
| [Activation.cs](Activation.cs) | `Activate`: find the active actor, or make it and load its record |
| [Program.cs](Program.cs) | activations, messages, the two group updates, saves, and a restart |
| [Host/Storage.cs](Host/Storage.cs) | plumbing that is not the ECS lesson: a dictionary standing in for a database, and the host's "save what changed" |

## Run it

```bash
dotnet run --project samples/Actors
```

## Expected output

```text
activated: shop 1 has 2, shop 2 (new) has 5, bank 7 has 1000
saved after activation: 1
buy 1 at shop 1: sold, 1 left, closed: False
buy 1 at shop 1: sold, 0 left, closed: True
buy 1 at shop 1: refused, 0 left, closed: True
deposit 50 at bank 7: True
deposit 50 at bank 8: False
after the updates: shop 1 has 3 (sold 2), bank 7 has 1060
saved after the messages: 3
restarted: shop 1 has 3 (sold 0), bank 7 has 1060
```

Only the new shop is saved after activation: the two records loaded with `markWritten: false` are not changes. The
third order is refused by the filter (the shop closed on the second one), and bank 8 was never activated. After the
restart the shop has its saved stock but no sales: `Sales` lives only in memory.
