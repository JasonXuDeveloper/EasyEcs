# HelloWorld

The smallest complete EasyEcs program. Start here.

## What you learn

- **Components** are plain data on entities (`Position`, `Velocity`); a component with no fields is a **tag**
  (`Player`) that systems filter on.
- A **singleton** (`Clock`) holds one value per world.
- **Systems** have one job each and no state of their own. A `[ForEach]` method runs once per entity that has its
  component parameters: `ref` for what it writes, `in` for what it reads. `[With<Player>]` adds a filter.
- `world.Update()` runs every system once, in the order they were added.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | the two components, the `Player` tag and the `Clock` singleton |
| [Systems.cs](Systems.cs) | `MoveSystem` moves, `ReportSystem` prints the players, `ClockSystem` advances the clock |
| [Program.cs](Program.cs) | creates the world, adds the singleton and the systems, creates two entities, runs three updates |

## Run it

```bash
dotnet run --project samples/HelloWorld
```

## Expected output

```text
tick 0: player at (1, 2)
tick 1: player at (2, 4)
tick 2: player at (3, 6)
2 entities after 3 ticks
```

`ReportSystem` runs before `ClockSystem`, so the first update prints tick 0. The second entity moves too, but it has
no `Player` tag, so it is never printed.

Next: [GameLoop](../GameLoop), a small server with input from another thread.
