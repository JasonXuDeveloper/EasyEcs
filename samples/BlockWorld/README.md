# BlockWorld

A Minecraft-style server on an ECS world: 20 ticks a second like a vanilla server, chunks loaded around the players
and freed behind them, blocks dug into items the digger picks up, zombies at night that hunt players and burn at
sunrise. Fifty bots play for a minute of game time, a day and a night twice. Read [GameLoop](../GameLoop) first.

## What you learn

- **A chunk is an entity, its blocks a buffer.** `Chunk` is the chunk's key, `IUniqueIndexed<ChunkPos>`, so
  `world.ChunkIndex.TryGet(pos, out chunk)` finds the chunk at a position; its 8,192 blocks are a
  `DynamicBuffer<Block>` on the same entity. Physics, digging and spawning read blocks through one helper,
  `Terrain.BlockAt`.
- **The world follows the players.** `ChunkLoadSystem` creates the missing chunks around every player twice a second
  and stamps the others as seen; `ChunkUnloadSystem` destroys the chunks nobody saw for five seconds. Both change
  structure in the middle of their loop: changes apply at once, no command buffer.
- **State changes are data.** A dig is a flag of the client's `Input`; a dug block becomes an `Item` entity that
  names its owner by `EntityId`; a zombie is a prefab instantiated with its own `Position` and `Target`; dying is a
  `Health` of zero that one system per kind handles (zombies go, players respawn).
- **Send only what changed.** The chunk's buffer and the `Position` are tracked: `[Changed<T>]` systems visit the
  chunks that were created or dug and the bodies that moved since the last tick. `PhysicsSystem` writes the
  position through a `Mut<Position>` with `SetIfChanged`, so a player standing still is not sent.
- **Cadence per system.** `UpdateInterval` runs chunk loading twice a second, unloading once a second and spawning
  every five seconds; everything else runs every tick.
- **Measured.** `WorldOptions.MeasureSystems` times every system; the run prints the average tick against the 50 ms
  budget of a 20 TPS server to stderr.

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | blocks and chunks, bodies, players with their `Input` and inventory, zombies, items, the `Clock`, `Prefabs` and `Stats` singletons |
| [Rules.cs](Rules.cs) | the numbers of the game (ticks, speeds, damage) and the spawn points |
| [Terrain.cs](Terrain.cs) | chunk coordinates, terrain generation, `BlockAt` |
| [Systems/Chunks.cs](Systems/Chunks.cs) | `ChunkLoadSystem`, `ChunkUnloadSystem` |
| [Systems/Movement.cs](Systems/Movement.cs) | `PlayerMoveSystem`, `PhysicsSystem` (steps, walls, gravity) |
| [Systems/Items.cs](Systems/Items.cs) | `DigSystem`, `PickupSystem`, `ItemExpirySystem` |
| [Systems/Zombies.cs](Systems/Zombies.cs) | `NightSpawnSystem`, `ZombieChaseSystem`, `SunburnSystem` |
| [Systems/Health.cs](Systems/Health.cs) | `ZombieDeathSystem`, `PlayerRespawnSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `SendChunksSystem`, `SendPositionsSystem`, `ClockSystem` |
| [Program.cs](Program.cs) | the singletons, the prefabs, the systems in tick order, the players |
| [Host/](Host) | plumbing that is not the ECS lesson: the bots that stand in for clients, the tick loop and its report, options |

## Run it

```bash
dotnet run -c Release --project samples/BlockWorld                      # 50 bots, one minute of game time
dotnet run -c Release --project samples/BlockWorld -- --players 5000    # a crowd
dotnet run -c Release --project samples/BlockWorld -- --realtime        # 20 ticks a second, like a vanilla server
```

## Expected output

```text
tick   200 (day  ): 50 players, 87 chunks, 0 zombies, 0 items
tick   400 (night): 50 players, 127 chunks, 50 zombies, 0 items
tick   600 (day  ): 50 players, 153 chunks, 150 zombies, 0 items
tick   800 (day  ): 50 players, 155 chunks, 0 zombies, 0 items
tick  1000 (night): 50 players, 173 chunks, 50 zombies, 0 items
tick  1200 (day  ): 50 players, 202 chunks, 150 zombies, 0 items
chunks: 235 loaded, 33 unloaded, 981 sent
blocks dug: 748; items: 748 picked up, 0 expired
zombies: 300 spawned, 150 burned, 151 hits; player deaths: 11
position updates sent: 89193
```

The world, the bots and the spawns come from integer hashes, so every run prints the same. The chunks grow as the
bots walk out and shrink behind them; three zombies per player rise each night and are gone a second after sunrise.

stderr gets the timings, which depend on the machine: the average tick against the 50 ms budget, then each system's
average run and its allocations. The allocations are the chunks' blocks (8 KB a chunk) and the tables growing for
the first night's zombies. On a 4-core VM, 50 players take about 0.2 ms a tick, and 5,000 players with 15,000
zombies about 2 ms.
