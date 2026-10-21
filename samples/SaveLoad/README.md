# SaveLoad

Taking entities out of a world and putting them into another: a restart, a player changing servers, a player
walking into the next zone. Read [GameLoop](../GameLoop) first.

## What you learn

1. **A whole world**: `world.Save(writer)` to a file and `world.Load(bytes)` into a new world. Ids, components,
   dynamic buffers and singletons come back as they were.
2. **A group of entities**: `SaveEntities` of a player with the swords that are its children (`ChildOf`), then
   `LoadEntities` into another world, as another server would. The arrivals get new ids, and the links between
   them follow.
3. **Moving live entities**: `MoveEntities` hands a player from one world to another of the same thread in one
   call, without encoding anything.

Unmanaged components are saved as their bytes. A managed component (`Name` holds a string) is a `[NinoType]`,
serialized by [Nino](https://github.com/JasonXuDeveloper/Nino), whose generator comes with the EasyEcs package.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | `Position`, `Health`, `Sword`, the bag element `Item`, the `Clock` singleton, the `[NinoType]` `Name` |
| [Players.cs](Players.cs) | `Players.Spawn`: a player with a bag and its swords as children |
| [Program.cs](Program.cs) | the three steps, each checked |
| [Support.cs](Support.cs) | plumbing that is not the ECS lesson: `Checks` prints a line per check, `WorldText` prints a world so two can be compared |

## Run it

```bash
dotnet run --project samples/SaveLoad
```

## Expected output

The process exits with 0 when every check passes and with 1 otherwise.

```text
zone:     [alice@(1,2) hp 100 item1x3 item2x1 sword+10; bob@(5,5) hp 100 item1x3 item2x1 sword+11 sword+10; carol@(-3,7) hp 100 item1x3 item2x1] clock 1234
reloaded: [alice@(1,2) hp 100 item1x3 item2x1 sword+10; bob@(5,5) hp 100 item1x3 item2x1 sword+11 sword+10; carol@(-3,7) hp 100 item1x3 item2x1] clock 1234 (425 bytes on disk)
  ok   a reload has the same entities, values and clock
  ok   a handle's id names the same entity after a reload
  ok   the copy has bob's values
  ok   his swords point at his new id
other:    [bob@(5,5) hp 100 item1x3 item2x1 sword+10 sword+11] clock 0
  ok   carol left the zone
  ok   and arrived with her values
zone:     [alice@(1,2) hp 100 item1x3 item2x1 sword+10] clock 1234
other:    [bob@(5,5) hp 100 item1x3 item2x1 sword+10 sword+11; carol@(-3,7) hp 100 item1x3 item2x1] clock 0
  ok   alice stayed
all checks passed
```

The other world's clock is 0: `SaveEntities` carries entities, not singletons.

Next: [MultiWorld](../MultiWorld), one world per thread.
