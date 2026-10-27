# VoxelServer

A Minecraft-style server on an ECS world: 20 ticks a second like a vanilla server, chunks loaded around the players,
saved to region files behind them and read back when a player returns, water that spreads and recedes by Minecraft's
rules and carries whatever stands in it, blocks dug into items anyone can pick up, zombies at night that follow
the nearest player's scent around walls and pits and catch fire under the sky by day, only what changed sent to the clients, and the world saved
when the server stops. Fifty bots play for a minute of game time, a day and a night twice. Read
[GameLoop](../GameLoop) first.

What each file teaches, with its code, is on the docs site:
[VoxelServer](https://ecs.xgamedev.net/en/docs/samples/voxel-server)
([中文](https://ecs.xgamedev.net/zh/docs/samples/voxel-server)).

## Files, in reading order

| File | What it holds |
|---|---|
| [Components/](Components) | the data, one file per part of the world: [Chunks.cs](Components/Chunks.cs) (blocks, chunks, load requests, `Fresh`, `Unloading`, `Viewers`), [Water.cs](Components/Water.cs) (the water, its height map and the data of its passes), [Bodies.cs](Components/Bodies.cs), [Players.cs](Components/Players.cs) (`Input`, `Score`, the inventory, `ViewCenter`, `Visible`), [Mobs.cs](Components/Mobs.cs) (zombies, items), [Scent.cs](Components/Scent.cs) (the zombies' distance field: `Ground`, `Footing`, `Scent`, `Trail`, `Footprint`), [Singletons.cs](Components/Singletons.cs) |
| [Groups.cs](Groups.cs) | the three parts of a tick: `ClientInput`, `Simulation`, `Replication` |
| [Rules.cs](Rules.cs) | the numbers of the game (ticks, speeds, damage, ranges) and the spawn points |
| [Terrain.cs](Terrain.cs) | chunk coordinates, terrain generation, `Nearby` (the blocks, water and height map around a body, one chunk lookup), the square of chunks around a point and the strip a view gains |
| [Systems/Chunks.cs](Systems/Chunks.cs) | `ChunkReadSystem`, `ChunkGenerateSystem`, `ChunkUnloadSystem`, `ChunkSaveSystem`: a chunk's life |
| [Systems/Water/](Systems/Water) | one system per file: [HeightMap.cs](Systems/Water/HeightMap.cs) and [Wake.cs](Systems/Water/Wake.cs) (a block update), then the passes: [Flow.cs](Systems/Water/Flow.cs) (the shader, which reads its halo from the neighbors), [Sleep.cs](Systems/Water/Sleep.cs), [Commit.cs](Systems/Water/Commit.cs); [Float.cs](Systems/Water/Float.cs) |
| [Systems/Scent/](Systems/Scent) | the zombies' distance field, one system per file: [Footing.cs](Systems/Scent/Footing.cs) (where a zombie can stand), [Wake.cs](Systems/Scent/Wake.cs) (the ground or a player's column changed), [Hunt.cs](Systems/Scent/Hunt.cs) (the field sleeps by day), then the passes: [Step.cs](Systems/Scent/Step.cs) (the shader, settling a chunk in place), [Settle.cs](Systems/Scent/Settle.cs) |
| [Systems/Movement.cs](Systems/Movement.cs) | `PlayerMoveSystem`, `PhysicsSystem` (steps, walls, gravity), `GridSystem` (the entities by chunk) |
| [Systems/Aoi.cs](Systems/Aoi.cs) | the areas of interest: `AoiViewSystem` (a player's view), `AoiChunkSystem` (a chunk loaded), `AoiSpawnSystem` (an entity crossing), `AoiDespawnSystem` (an entity gone) |
| [Systems/Zombies.cs](Systems/Zombies.cs) | `NightSpawnSystem`, `ZombieTargetSystem`, `ZombieChaseSystem` (down the scent's trail), `ZombieAttackSystem`, `SunlightSystem`, `FireDamageSystem`, `ExtinguishSystem` |
| [Systems/Items.cs](Systems/Items.cs) | `DigSystem`, `PickupSystem` and the inventory, `ItemExpirySystem` (the ordered index of drop ticks) |
| [Systems/Health.cs](Systems/Health.cs) | `ZombieDeathSystem`, `PlayerRespawnSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `SendChunksSystem`, `ChunkSentSystem`, `SendEntitiesSystem`, `ClockSystem` (the first system of the simulation) |
| [Program.cs](Program.cs) | the world and its options, the prefabs, the systems in tick order, the players, the top diggers, the save on stop |
| [Host/](Host) | plumbing that is not the ECS lesson: the bots that stand in for clients, the region files in memory, the tick loop and its report, options |

## Run it

```bash
dotnet run -c Release --project samples/VoxelServer                      # 50 bots, one minute of game time
dotnet run -c Release --project samples/VoxelServer -- --players 2000    # a crowd
dotnet run -c Release --project samples/VoxelServer -- --realtime        # 20 ticks a second, like a vanilla server
dotnet run -c Release --project samples/VoxelServer -- --schedule        # the order the systems run in
```

## Expected output

```text
tick   200 (day  ): 50 players, 97 chunks, 4094 water, 0 zombies, 68 items
tick   400 (night): 50 players, 121 chunks, 8506 water, 46 zombies, 107 items
tick   600 (day  ): 50 players, 148 chunks, 9809 water, 127 zombies, 112 items
tick   800 (day  ): 50 players, 157 chunks, 10880 water, 0 zombies, 117 items
tick  1000 (night): 50 players, 168 chunks, 14045 water, 41 zombies, 89 items
tick  1200 (day  ): 50 players, 172 chunks, 12680 water, 123 zombies, 79 items
chunks: 196 generated, 60 unloaded, 36 loaded back
blocks dug: 603; items: 350 picked up, 238 expired
zombies: 250 spawned, 125 burned, 334 hits; player deaths: 35
water: 16069 cells filled, 9952 ticks of bodies carried
view: 4072 chunks sent, 2827 forgotten; 40547 entities came into view, 12605 left it, 21503 despawned
sent: 33714 block updates, 2713660 entity updates (47285 KiB)
top diggers: player 36 (26) player 42 (22) player 48 (21)
restarted from the save: 50 players, 172 chunks, 12680 water, 123 zombies; 24 chunks in the region files
```

The world, the springs, the bots and the spawns come from integer hashes, so every run prints the same. The chunks
grow as the bots walk out; the ones left behind go to the region files and come back when a bot returns. The water
spreads from the springs and fills the pits the bots dig; zombies rise each night and burn by day.

stderr gets the timings, which depend on the machine: the average tick against the 50 ms budget, each system's
average run and its allocations, and the size of the save. The allocations are the chunks' buffers while the pool has
none to give back, the region files, and the tables and indexes growing to their peak. On a 4-core VM, 50 players
take well under 1 ms a tick and 2,000 players with their zombies under 3 ms, most of it in the physics, the water and
the pickups: the bots start packed on a strip 48 blocks wide, so every view is full.

## How it differs from a vanilla server

- Days are 30 seconds, items last 15 seconds and fire hurts four times as often, so a one-minute run sees all of it.
- The server moves the players (from their input); a vanilla server trusts the client's movement and checks it.
- One distance decides which chunks are loaded and simulated around a player and which entities it sees: those of
  its chunks. A vanilla server tracks each entity within a range of its kind, checked every tick.
- No mob cap, no light levels, no despawn of far mobs, no keep-alive and no players joining or leaving.
