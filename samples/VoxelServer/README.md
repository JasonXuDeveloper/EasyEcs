# VoxelServer

A Minecraft-style server on an ECS world: 20 ticks a second like a vanilla server, chunks loaded around the players
and freed behind them, springs whose water runs downhill and carries whoever stands in it, blocks dug into items the
digger picks up, zombies at night that hunt players and burn at sunrise, and the world saved when the server stops.
Fifty bots play for a minute of game time, a day and a night twice. Read [GameLoop](../GameLoop) first.

## What you learn

- **A chunk is an entity, its blocks a buffer.** `Chunk` is the chunk's key, `IUniqueIndexed<ChunkPos>`, so
  `world.ChunkIndex.TryGet(pos, out chunk)` finds the chunk at a position; its 8,192 blocks are a
  `DynamicBuffer<Block>` of an enum on the same entity, read as `chunk.Block[slot]` and written through
  `chunk.WriteBlock()`.
- **The world follows the players.** `ChunkLoadSystem` creates the missing chunks around every player twice a second
  (`UpdateInterval`); `ChunkUnloadSystem` destroys the chunks nobody saw for five seconds, a twentieth of the chunks
  each tick (`[ForEach(Slices = 20)]`), so no tick pays for all of them. Both change structure in the middle of their
  loop: changes apply at once, no command buffer.
- **Water is data too.** A block of water is `Block.Water` in the buffer and a `Water` entity beside it: its level
  (blocks from the spring) and its spring, found by block with `world.WaterIndex`. A cell that still has to spread
  carries `Flowing`; once it spread, the component goes and the settled lake costs nothing. Digging beside water adds
  `Flowing` back. Each cell is a child of its chunk (`ChildOf`), so unloading a chunk takes its water with it.
- **Areas of interest from the prefabs.** Players and zombies carry a `Vision`, set by each prefab: 32 blocks for a
  player, 16 for a zombie. A player's view decides which chunks are loaded around it, and the load pass counts on each
  chunk the players that see it (`Viewers`): a dug block or a step goes only to them. A zombie looks for the nearest
  player in its view through an index of the players by chunk (`InChunk`, `world.InChunkIndex.Find(chunk)`), a tenth
  of the zombies each tick (`Slices`), and stands when it sees nobody.
- **A tag picks who floats.** Players and zombies carry the `Humanoid` tag: `[With<Humanoid>]` lifts them in water and
  pushes them away from the spring, hardest next to it. Dropped items have no tag and sink.
- **State changes are data.** A dig is a flag of the client's `Input`; a dug block becomes an `Item` entity that
  names its owner by `EntityId`; a zombie is a prefab instantiated with its own `Position` and `Target`; dying is a
  `Health` of zero that one system per kind handles (zombies go, players respawn).
- **Send only what changed.** The chunk's buffer and the `Position` are tracked: `[Changed<T>]` systems visit the
  chunks that were created, dug or flooded and the bodies that moved since the last tick. `PhysicsSystem` writes the
  position through a `Mut<Position>` with `SetIfChanged`, so a player standing still is not sent. What was destroyed
  comes from the destroy log (`WorldOptions.DestroyLogCapacity`, `world.DestroyedSince`).
- **A tick in three groups.** `ClientInput`, `Simulation` and `Replication` are system groups
  (`[UpdateInGroup<T>]`); the host runs each with `world.Update<TGroup>()`, so each part could run at its own rate.
- **Threads where rows are independent.** The bots and the walk touch only their own row and no `World`:
  `[ForEach(Parallel = true)]` splits them across threads, with the same result on every run. Physics, water and
  sight look blocks and players up through the world's indexes, so they stay on one thread.
- **SIMD where the data is a column.** `SunburnSystem` is a batch method: it gets the health of a run of zombies as
  one `Span<Health>` and takes a point from 8 of them per instruction with `Vector<int>`. The other systems look
  something up per entity (a block, a cell of water, a player in a chunk), which no vector does.
- **One method for several kinds.** `SendEntitiesSystem` reads the health bar through `Maybe<Health>`: players and
  zombies have one, dropped items do not, and one method sends all three.
- **A scoreboard that stays sorted.** `Score` is `IIndexed<int>`: `world.ScoreIndex.Descending()` walks the players
  from the best digger down, kept in order as `DigSystem` writes the scores, never sorted.
- **Save on stop.** The world is saved without its prefabs (`SaveWithout`), and a restarted world builds its prefabs
  and loads the save beside them (`LoadKeeping`).
- **Measured.** `WorldOptions.MeasureSystems` times every system; the run prints the average tick against the 50 ms
  budget of a 20 TPS server to stderr. `--schedule` prints the order the systems run in (`world.DescribeSchedule()`).

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | blocks and chunks, water, bodies, players with their `Input`, `Score` and inventory, zombies, items, the `Clock`, `Prefabs` and `Stats` singletons |
| [Groups.cs](Groups.cs) | the three parts of a tick: `ClientInput`, `Simulation`, `Replication` |
| [Rules.cs](Rules.cs) | the numbers of the game (ticks, speeds, damage, water) and the spawn points |
| [Terrain.cs](Terrain.cs) | chunk coordinates, terrain generation, `BlockAt`, pouring water into a block |
| [Systems/Chunks.cs](Systems/Chunks.cs) | `ChunkLoadSystem`, `ChunkUnloadSystem` |
| [Systems/Water.cs](Systems/Water.cs) | `WaterFlowSystem`, `FloatSystem` |
| [Systems/Movement.cs](Systems/Movement.cs) | `PlayerMoveSystem`, `PhysicsSystem` (steps, walls, gravity), `GridSystem` (the players by chunk) |
| [Systems/Items.cs](Systems/Items.cs) | `DigSystem`, `PickupSystem`, `ItemExpirySystem` |
| [Systems/Zombies.cs](Systems/Zombies.cs) | `NightSpawnSystem`, `ZombieSightSystem`, `ZombieChaseSystem`, `SunburnSystem` |
| [Systems/Health.cs](Systems/Health.cs) | `ZombieDeathSystem`, `PlayerRespawnSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `SendChunksSystem`, `SendEntitiesSystem`, `SendDespawnsSystem`, `ClockSystem` |
| [Program.cs](Program.cs) | the world and its options, the prefabs, the systems in tick order, the players, the top diggers, the save on stop |
| [Host/](Host) | plumbing that is not the ECS lesson: the bots that stand in for clients, the tick loop and its report, options |

## Run it

```bash
dotnet run -c Release --project samples/VoxelServer                      # 50 bots, one minute of game time
dotnet run -c Release --project samples/VoxelServer -- --players 5000    # a crowd
dotnet run -c Release --project samples/VoxelServer -- --realtime        # 20 ticks a second, like a vanilla server
dotnet run -c Release --project samples/VoxelServer -- --schedule        # the order the systems run in
```

## Expected output

```text
tick   200 (day  ): 50 players, 82 chunks, 1961 water, 0 zombies, 0 items
tick   400 (night): 50 players, 119 chunks, 3070 water, 50 zombies, 0 items
tick   600 (day  ): 50 players, 135 chunks, 4322 water, 150 zombies, 0 items
tick   800 (day  ): 50 players, 142 chunks, 4054 water, 0 zombies, 0 items
tick  1000 (night): 50 players, 171 chunks, 5311 water, 50 zombies, 0 items
tick  1200 (day  ): 50 players, 170 chunks, 4774 water, 150 zombies, 0 items
chunks: 232 loaded, 62 unloaded, 25227 sent
blocks dug: 638; items: 638 picked up, 0 expired
zombies: 300 spawned, 150 burned, 361 hits; player deaths: 39
water: 7625 cells poured, 5005 ticks of humanoids carried
sent: 2501294 entity updates (43967 KiB), 3701 despawns
top diggers: player 6 (22) player 24 (21) player 22 (21)
restarted from the save: 50 players, 170 chunks, 4774 water, 150 zombies
```

The world, the springs, the bots and the spawns come from integer hashes, so every run prints the same, on one thread
or many. The chunks grow as the bots walk out and shrink behind them, and the water with them; three zombies per
player rise each night and are gone a second after sunrise.

stderr gets the timings, which depend on the machine: the average tick against the 50 ms budget, each system's
average run and its allocations, and the size of the save. The allocations are the blocks of the chunks loaded while no
unloaded chunk had a list to give back (8 KB a chunk) and the tables and indexes growing to their peak. On a 4-core VM, 50 players take about
0.45 ms a tick, and 5,000 players with 15,000 zombies and 37,000 blocks of water about 4 ms.
