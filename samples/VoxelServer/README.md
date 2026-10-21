# VoxelServer

A Minecraft-style server on an ECS world: 20 ticks a second like a vanilla server, chunks loaded around the players,
saved to region files behind them and read back when a player returns, water that spreads and recedes by Minecraft's
rules and carries whatever stands in it, blocks dug into items anyone can pick up, zombies at night that hunt the
players they see and catch fire under the sky by day, only what changed sent to the clients, and the world saved
when the server stops. Fifty bots play for a minute of game time, a day and a night twice. Read
[GameLoop](../GameLoop) first.

## What you learn

- **A chunk is an entity, its blocks and its water two buffers.** `Chunk` is the chunk's key, `IUniqueIndexed<ChunkPos>`,
  so `world.ChunkIndex.TryGet(pos, out chunk)` finds the chunk at a position. Its 8,192 blocks are a
  `DynamicBuffer<Block>` of a byte enum and its water a `DynamicBuffer<Fluid>` of levels in the same layout, read as
  `chunk.Block[slot]` and written through `chunk.WriteBlock()`.
- **The world follows the players, and keeps what they left.** `ChunkLoadSystem` counts the players that see each
  chunk twice a second (`UpdateInterval`) and records the missing ones as `LoadRequest` entities; `ChunkReadSystem`
  reads them back from the region files or generates them. `ChunkUnloadSystem` marks the chunks nobody saw for five
  seconds, a twentieth of them each tick (`[ForEach(Slices = 20)]`), and `ChunkSaveSystem` writes each with the
  zombies and items in it (`SaveEntities`) and destroys them. Loading and saving run in `OnUpdate`, after the pass:
  a pass may not load or save entities (EECS041), so the pass leaves the work as data.
- **One area of interest for everything that sees.** Players and zombies carry a `Vision`, set by each prefab, and
  `SightSystem` fills the `Seen` buffer of each from a grid, the entities by chunk (`InChunk`,
  `world.InChunkIndex.Find(chunk)`). A player's client shows its set; a zombie hunts the nearest player in its set.
  `Seen` is written through `Mut<T>` only when the set changed, so `ZombieTargetSystem`
  (`[Changed<DynamicBuffer<Seen>>]`) picks a target again only for the zombies whose view changed. A tenth of the
  viewers look each tick (`Slices`), so a crowd costs the same every tick.
- **Water as a stencil.** A level depends on the six blocks around it, the same rule for every block, so
  `WaterFlowSystem` steps a chunk 16 blocks at a time with `Vector128<byte>`, on a padded copy of the grid whose halo
  holds the neighbours' edges, and only on the layers water reaches. A chunk whose step changed nothing loses its
  `Flowing` tag and is not visited again until a block changes near it (`BlockUpdateSystem`, `[Changed<T>]` on the
  blocks).
- **SIMD where the data is a column.** Cooldowns, item ages and fire are counters every entity changes the same way:
  batch methods get them as spans and change 8 per instruction with `Vector<int>`. The fire goes out in the next
  system, since a batch method may not change structure.
- **State changes are data.** A dig is a flag of the client's `Input`; a dug block becomes an `Item` entity; fire is a
  `Burning` component that `SunlightSystem` adds and `ExtinguishSystem` removes; dying is a `Health` of zero that one
  system per kind handles (zombies go, players drop their inventory and respawn).
- **Send only what changed.** The buffers and the `Position` are tracked: `[ChangedAny<DynamicBuffer<Block>,
  DynamicBuffer<Fluid>>]` visits the chunks dug or flooded since the last tick and `[Changed<Position>]` the bodies that
  moved. Physics writes the position through `Mut<Position>` with `SetIfChanged`, so a body standing still is not
  sent; `ChunkViewSystem` keeps the chunks each client has in a `DynamicBuffer<InView>` and sends a chunk whole when
  it comes into view.
- **One method for several kinds.** `SendEntitiesSystem` reads the health bar through `Maybe<Health>`: players and
  zombies have one, dropped items do not, and one method sends all three.
- **A tick in three groups.** `ClientInput`, `Simulation` and `Replication` are system groups
  (`[UpdateInGroup<T>]`); the host runs each with `world.Update<TGroup>()`.
- **A scoreboard that stays sorted.** `Score` is `IIndexed<int>`: `world.ScoreIndex.Descending()` walks the players
  from the best digger down, kept in order as `DigSystem` writes the scores, never sorted.
- **Save on stop.** The world is saved without its prefabs and region files (`SaveWithout`), and a restarted world
  builds them and loads the save beside them (`LoadKeeping`).
- **Measured.** `WorldOptions.MeasureSystems` times every system; the run prints the average tick against the 50 ms
  budget of a 20 TPS server to stderr. `--schedule` prints the order the systems run in (`world.DescribeSchedule()`).

## Files, in reading order

| File | What it holds |
|---|---|
| [Components.cs](Components.cs) | blocks, chunks and their water, bodies, players with their `Input`, `Score`, inventory, `Seen` and `InView`, zombies, items, the singletons |
| [Groups.cs](Groups.cs) | the three parts of a tick: `ClientInput`, `Simulation`, `Replication` |
| [Rules.cs](Rules.cs) | the numbers of the game (ticks, speeds, damage, ranges) and the spawn points |
| [Terrain.cs](Terrain.cs) | chunk coordinates, terrain generation, `BlockAt`, `WaterAt`, `TopAt`, the chunks around a point, `Wake` |
| [Systems/Chunks.cs](Systems/Chunks.cs) | `ChunkLoadSystem`, `ChunkReadSystem`, `ChunkUnloadSystem`, `ChunkSaveSystem`, `ChunkViewSystem` |
| [Systems/Water.cs](Systems/Water.cs) | `BlockUpdateSystem`, `WaterFlowSystem` (the stencil), `FloatSystem` |
| [Systems/Movement.cs](Systems/Movement.cs) | `PlayerMoveSystem`, `PhysicsSystem` (steps, walls, gravity), `GridSystem` (the entities by chunk) |
| [Systems/Sight.cs](Systems/Sight.cs) | `SightSystem`: the area of interest of players and zombies |
| [Systems/Zombies.cs](Systems/Zombies.cs) | `NightSpawnSystem`, `ZombieTargetSystem`, `ZombieChaseSystem`, `ZombieAttackSystem`, `CooldownSystem`, `SunlightSystem`, `BurnSystem`, `ExtinguishSystem` |
| [Systems/Items.cs](Systems/Items.cs) | `DigSystem`, `ItemAgeSystem`, `PickupSystem` and the inventory, `ItemExpirySystem` |
| [Systems/Health.cs](Systems/Health.cs) | `ZombieDeathSystem`, `PlayerRespawnSystem` |
| [Systems/Replication.cs](Systems/Replication.cs) | `SendChunksSystem`, `SendEntitiesSystem`, `ClockSystem` |
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
tick   200 (day  ): 50 players, 84 chunks, 3338 water, 0 zombies, 60 items
tick   400 (night): 50 players, 113 chunks, 6366 water, 42 zombies, 109 items
tick   600 (day  ): 50 players, 146 chunks, 11071 water, 121 zombies, 132 items
tick   800 (day  ): 50 players, 139 chunks, 11999 water, 5 zombies, 104 items
tick  1000 (night): 50 players, 127 chunks, 10938 water, 43 zombies, 84 items
tick  1200 (day  ): 50 players, 128 chunks, 10864 water, 126 zombies, 84 items
chunks: 160 generated, 68 unloaded, 36 loaded back
blocks dug: 622; items: 473 picked up, 232 expired
zombies: 244 spawned, 118 burned, 712 hits; player deaths: 86
water: 15443 cells filled, 13723 ticks of bodies carried
view: 4184 chunks sent, 2934 forgotten; 34896 entities came into view, 10735 left it, 17610 despawned
sent: 35853 block updates, 2915396 entity updates (50668 KiB)
top diggers: player 26 (22) player 20 (22) player 35 (21)
restarted from the save: 50 players, 128 chunks, 10864 water, 126 zombies; 32 chunks in the region files
```

The world, the springs, the bots and the spawns come from integer hashes, so every run prints the same. The chunks
grow as the bots walk out; the ones left behind go to the region files and come back when a bot returns. The water
spreads from the springs and fills the pits the bots dig; zombies rise each night and burn by day.

stderr gets the timings, which depend on the machine: the average tick against the 50 ms budget, each system's
average run and its allocations, and the size of the save. The allocations are the chunks' buffers while the pool has
none to give back, the region files, and the tables, indexes and `Seen` buffers growing to their peak. On a 4-core VM,
50 players take about 1 ms a tick and 2,000 players with 4,600 zombies about 12 ms, most of it in `SightSystem`: the
bots start packed on a strip 48 blocks wide, so every view is full.

## How it differs from a vanilla server

- Days are 30 seconds, items last 15 seconds and fire hurts four times as often, so a one-minute run sees all of it.
- The server moves the players (from their input); a vanilla server trusts the client's movement and checks it.
- One distance decides what a player sees and which chunks are loaded and simulated around it.
- An entity update goes to the players that have its chunk in view, a coarser set than the ones whose `Seen` holds
  it.
- No mob cap, no light levels, no despawn of far mobs, no keep-alive and no players joining or leaving.
