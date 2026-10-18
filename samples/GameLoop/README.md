# GameLoop

A small game server: a fixed 30 Hz tick, clients on another thread, replication of what changed.

- **Mailbox** (`Mailbox.cs`): the network thread (`Clients.cs`, simulated clients with a fixed seed) posts messages
  into a bounded queue; it is the only thing two threads share. A full queue refuses messages instead of growing.
- **Input as data** (`Data.cs`, `Systems.cs`): `ReceiveInput` (system group `InputGroup`) turns each message into a
  request component on an entity; `Leave`, `Join`, `MoveIntent` and `Fire` systems each handle one kind of request.
  Clients are found by the indexed `Client` component (`world.ClientIndex`).
- **Simulation**: `Movement`, `Lifetime` (bullets expire), `Clock`; bullets and players come from prefabs.
- **Replication** (`ReplicationGroup`, every 3rd tick): `[Changed<Position>]` visits only the entities that moved
  since the last run and writes 12 bytes each into the `Outbox` singleton.
- **Operations**: `--ticks N` (default 90, 3 s), a status line per second, Ctrl+C stops cleanly, and per-system run
  counts and times from `WorldOptions.MeasureSystems`.

```bash
dotnet run --project samples/GameLoop                  # 3 s
dotnet run --project samples/GameLoop -- --ticks 300   # 10 s
```

Expected output (Release; the counts are deterministic up to timing, the times vary):

```
t=1s players=16 bullets=59 entities=75
t=2s players=16 bullets=60 entities=76
t=3s players=16 bullets=48 entities=64
90 ticks in 3.0 s: 638 messages (0 refused), 66 joins, 50 leaves, 171 bullets, 1918 position updates (22.5 KB) replicated
  LeaveSystem           90 runs,      5.0 us last run
  ...
  ReplicationSystem     30 runs,     18.6 us last run
stopped
```

`ReceiveInput` runs more often than the tick: the loop drains the mailbox while it waits for the next tick.
