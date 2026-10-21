# GameLoop

A small game server: clients on another thread, a fixed 30 Hz simulation tick, and replication of only what
changed. Read [HelloWorld](../HelloWorld) first.

## What you learn

- **Input as data.** The network thread posts messages to a mailbox, which `ReceiveInputSystem` keeps in a
  `readonly` field (not in a singleton: the mailbox is not world state). It turns each message into a request entity (`JoinRequest`, `MoveRequest`, ...), and one system per kind of request handles it and destroys
  it in the same tick. Systems never call each other; they share data.
- **System groups** (`ISystemGroup`, `[UpdateInGroup<T>]`) give systems their own cadence: input on every
  wake-up, the simulation on the fixed tick, replication every third tick.
- **A value index** (`IIndexed<int>` on `Client`) finds a player by client id: `world.ClientIndex.TryFind(...)`.
- **Prefabs** in a singleton: systems instantiate `prefabs.Player` and `prefabs.Bullet` with the values that differ
  per instance (`world.Instantiate(prefab, new Client(id), new Position(x, 0))`), one row copy each.
- **Change tracking** (`ITracked` on `Position`, `[Changed<Position>]`): replication visits only the
  entities that moved since it last ran. `MovementSystem` writes through `Mut<Position>` and `SetIfChanged`, so a
  player standing still is not stamped (a `ref Position` parameter would stamp every row it visits).
- **Operations**: `--ticks N`, a status line per second, Ctrl+C stops cleanly, per-system run counts and times
  from `WorldOptions.MeasureSystems`.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | requests, simulation state, singletons, the two system groups |
| [Systems/Input.cs](Systems/Input.cs) | `ReceiveInputSystem`: messages from the mailbox it holds to request entities |
| [Systems/Requests.cs](Systems/Requests.cs) | `LeaveSystem`, `JoinSystem`, `MoveSystem`, `FireSystem`: one kind of request each |
| [Systems/Simulation.cs](Systems/Simulation.cs) | `MovementSystem`, `LifetimeSystem` (bullets expire), `ClockSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `ReplicationSystem`: 12 bytes per moved entity into the outbox |
| [Program.cs](Program.cs) | creates the world, adds singletons and systems, runs the loop, prints the summary and the checks |
| [Host/](Host) | plumbing a real server has but that is not the ECS lesson: the `Mailbox` between threads, `FakeClients` standing in for the network, `FixedTick` timing, `Network.SendOutbox`, the `Report` lines, `Checks` |

## Run it

```bash
dotnet run --project samples/GameLoop                   # 3 seconds (90 ticks)
dotnet run --project samples/GameLoop -- --ticks 30     # 1 second
```

## Expected output

With `-c Release -- --ticks 30`. The fake clients use a fixed seed, so the counts are the same from run to run up
to timing; the times vary with the machine. The process exits with 0 when every check passes and with 1 otherwise.

```text
t=1s players=16 bullets=59 entities=75
30 ticks in 1.0 s
  220 messages (0 refused)
  34 joins, 18 leaves
  59 bullets fired
  378 position updates replicated (4.4 KB)
  LeaveSystem             30 runs,      4.7 us last run
  JoinSystem              30 runs,      2.2 us last run
  MoveSystem              30 runs,      6.9 us last run
  FireSystem              30 runs,     71.0 us last run
  MovementSystem          30 runs,      4.7 us last run
  LifetimeSystem          30 runs,      4.7 us last run
  ClockSystem             30 runs,      0.8 us last run
  ReceiveInputSystem     252 runs,      0.4 us last run
  ReplicationSystem       10 runs,     15.1 us last run
  ok   players have the prefab's components and their client id
  ok   bullets have the prefab's components and their lifetime
  ok   no client has two players
  ok   the mailbox refused no message
stopped
```

`ReceiveInputSystem` runs far more often than the tick: the loop drains the mailbox every time it wakes up while
it waits for the next tick.

Next: [SaveLoad](../SaveLoad), saving a world and moving entities between worlds.
