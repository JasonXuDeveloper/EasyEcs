# GameLoop

A small game server: clients on another thread, a fixed 30 Hz simulation tick, and replication of only what
changed. Read [HelloWorld](../HelloWorld) first.

## What you learn

- **Input as data.** The network thread posts messages to a mailbox. `ReceiveInputSystem` turns each message into
  a request entity (`JoinRequest`, `MoveRequest`, ...), and one system per kind of request handles it and destroys
  it in the same tick. Systems never call each other; they share data.
- **System groups** (`ISystemGroup`, `[UpdateInGroup<T>]`) give systems their own cadence: input on every
  wake-up, the simulation on the fixed tick, replication every third tick.
- **A value index** (`IIndexed<int>` on `Client`) finds a player by client id: `world.ClientIndex.TryFind(...)`.
- **Prefabs** in a singleton: systems instantiate `prefabs.Player` and `prefabs.Bullet`.
- **Change tracking** (`ITrackChanges` on `Position`, `[Changed<Position>]`): replication visits only the
  entities that moved since it last ran.
- **Operations**: `--ticks N`, a status line per second, Ctrl+C stops cleanly, per-system run counts and times
  from `WorldOptions.MeasureSystems`.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | requests, simulation state, singletons, the two system groups |
| [Systems/Input.cs](Systems/Input.cs) | `ReceiveInputSystem`: messages to request entities |
| [Systems/Requests.cs](Systems/Requests.cs) | `LeaveSystem`, `JoinSystem`, `MoveSystem`, `FireSystem`: one kind of request each |
| [Systems/Simulation.cs](Systems/Simulation.cs) | `MovementSystem`, `LifetimeSystem` (bullets expire), `ClockSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `ReplicationSystem`: 12 bytes per moved entity into the outbox |
| [Program.cs](Program.cs) | creates the world, adds singletons and systems, runs the loop, prints the summary |
| [Host/](Host) | plumbing a real server has but that is not the ECS lesson: the `Mailbox` between threads, `FakeClients` standing in for the network, `FixedTick` timing, `Network.SendOutbox`, the `Report` lines |

## Run it

```bash
dotnet run --project samples/GameLoop                   # 3 seconds (90 ticks)
dotnet run --project samples/GameLoop -- --ticks 30     # 1 second
```

## Expected output

With `-c Release -- --ticks 30`. The fake clients use a fixed seed, so the counts are the same from run to run up
to timing; the times vary with the machine.

```text
t=1s players=16 bullets=59 entities=75
30 ticks in 1.0 s
  222 messages (0 refused)
  35 joins, 19 leaves
  59 bullets fired
  456 position updates replicated (5.3 KB)
  LeaveSystem             30 runs,     25.4 us last run
  JoinSystem              30 runs,     87.0 us last run
  MoveSystem              30 runs,    165.8 us last run
  FireSystem              30 runs,      8.6 us last run
  MovementSystem          30 runs,     10.3 us last run
  LifetimeSystem          30 runs,      4.9 us last run
  ClockSystem             30 runs,      0.8 us last run
  ReceiveInputSystem     248 runs,     10.5 us last run
  ReplicationSystem       10 runs,     11.4 us last run
stopped
```

`ReceiveInputSystem` runs far more often than the tick: the loop drains the mailbox every time it wakes up while
it waits for the next tick.

Next: [SaveLoad](../SaveLoad), saving a world and moving entities between worlds.
