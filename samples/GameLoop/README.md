# GameLoop

A small game server: clients on another thread, a fixed 30 Hz simulation tick, and replication of only what
changed. Read [HelloWorld](../HelloWorld) first.

What each file teaches, with its code, is on the docs site: [GameLoop](https://ecs.xgamedev.net/en/docs/samples/game-loop)
([中文](https://ecs.xgamedev.net/zh/docs/samples/game-loop)).

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | requests, simulation state, singletons, the two system groups |
| [Systems/Input.cs](Systems/Input.cs) | `ReceiveInputSystem`: messages from the mailbox in singleton `Inbox` to request entities |
| [Systems/Requests.cs](Systems/Requests.cs) | `LeaveSystem`, `JoinSystem`, `MoveSystem`, `FireSystem`: one kind of request each |
| [Systems/Simulation.cs](Systems/Simulation.cs) | `MovementSystem`, `LifetimeSystem` (bullets expire), `ClockSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `ReplicationSystem`: 16 bytes per moved entity into the outbox |
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
  378 position updates replicated (5.9 KB)
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
