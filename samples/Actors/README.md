# Actors

Actors on an ECS world: entities that answer messages addressed to their key, each kind of actor with its own
systems, loaded from storage when first needed and saved only when they change. Read [GameLoop](../GameLoop) and
[SaveLoad](../SaveLoad) first.

What each file teaches, with its code, is on the docs site: [Actors](https://ecs.xgamedev.net/en/docs/samples/actors)
([中文](https://ecs.xgamedev.net/zh/docs/samples/actors)).

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | the `In<TActor>` tag, the `IActor<TSelf>` interface, `Shop` and `Bank`, the persistent `Stock` and `Balance`, the in-memory `Sales` and `Closed`, the `Saving` group and its `Unsaved` list |
| [Messages.cs](Messages.cs) | the `Buy` and `Deposit` jobs, and `Mailman`, which runs each on its actor |
| [Systems/ShopSystems.cs](Systems/ShopSystems.cs) | `RestockSystem`, in the shop group |
| [Systems/BankSystems.cs](Systems/BankSystems.cs) | `InterestSystem`, in the bank group |
| [Systems/SaveSystems.cs](Systems/SaveSystems.cs) | `ListUnsavedSystem`, in the save group: the actors written since the last save |
| [Activation.cs](Activation.cs) | `Activate`: find the active actor, or make it and load its record |
| [Program.cs](Program.cs) | activations, messages, the group updates, saves, and a restart |
| [Host/Storage.cs](Host/Storage.cs) | plumbing that is not the ECS lesson: a dictionary standing in for a database, and the host's save of the listed actors |

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
saved after the messages: 2
restarted: shop 1 has 3 (sold 0), bank 7 has 1060
```

Only the new shop is saved after activation: the two records loaded with `markWritten: false` are not changes. The
third order is refused before its job runs (the shop sold out on the second one and closed), and bank 8 was never
activated. After the messages shop 1 and bank 7 are saved; shop 2, saved after activation and not written since, is
not. After the restart the shop has its saved stock but no sales: `Sales` lives only in memory.
