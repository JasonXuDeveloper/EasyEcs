using EasyEcs;
using Nino.Core;
using VoxelServer;

// A Minecraft-style server: 20 ticks a second, chunks loaded around the players, blocks dug into items, springs whose
// water runs and carries whoever stands in it, zombies at night. Options: --players N (50), --ticks N (1200: one
// minute, a day and a night twice), --realtime, --schedule (print the order the systems run in and stop).
int players = Support.Option(args, "--players", 50);
int ticks = Support.Option(args, "--ticks", 1200);

// #region options
// A chunk's blocks, its water and the water's next step are 8 KB each (its border 4 KB), more than a buffer's pool keeps by default: let
// each chunk loaded reuse the lists of one that was unloaded.
DynamicBuffer<Block>.PoolCapacity = Terrain.BlocksPerChunk;
DynamicBuffer<Fluid>.PoolCapacity = Terrain.BlocksPerChunk;
DynamicBuffer<NextFluid>.PoolCapacity = Terrain.BlocksPerChunk;
DynamicBuffer<Surface>.PoolCapacity = Terrain.Size * Terrain.Size;
DynamicBuffer<Border>.PoolCapacity = Terrain.BorderSize;

using var world = new World(new WorldOptions { MeasureSystems = true });
// #endregion options

world.SetClock(Tick: 0, DayLength: Rules.DayLength);
world.AddSingleton<Stats>();
var regions = new RegionFiles();
world.SetRegions(regions);
SetPrefabs(world);

// #region systems
// One tick, in this order: what the clients asked for, the world around them, movement, the rules of the game, what
// is sent back.
world.AddSystem<FakeClientsSystem>();
world.AddSystem<ClockSystem>();
world.AddSystem<ChunkReadSystem>();
world.AddSystem<ChunkGenerateSystem>();
world.AddSystem<HeightMapSystem>();
world.AddSystem<WaterWakeSystem>();
world.AddSystem<WaterBorderSystem>();
world.AddSystem<WaterFlowSystem>();
world.AddSystem<WaterSleepSystem>();
world.AddSystem<WaterCommitSystem>();
world.AddSystem<PlayerMoveSystem>();
world.AddSystem<ZombieTargetSystem>();
world.AddSystem<ZombieChaseSystem>();
world.AddSystem<ZombieAttackSystem>();
world.AddSystem<PlayerRespawnSystem>();
world.AddSystem<FloatSystem>();
world.AddSystem<PhysicsSystem>();
world.AddSystem<GridSystem>();
world.AddSystem<DigSystem>();
world.AddSystem<PickupSystem>();
world.AddSystem<ItemExpirySystem>();
world.AddSystem<NightSpawnSystem>();
world.AddSystem<SunlightSystem>();
world.AddSystem<FireDamageSystem>();
world.AddSystem<ExtinguishSystem>();
world.AddSystem<ZombieDeathSystem>();
world.AddSystem<ChunkUnloadSystem>();
world.AddSystem<ChunkSaveSystem>();
world.AddSystem<AoiViewSystem>();
world.AddSystem<AoiChunkSystem>();
world.AddSystem<AoiSpawnSystem>();
world.AddSystem<AoiDespawnSystem>();
world.AddSystem<SendChunksSystem>();
world.AddSystem<ChunkSentSystem>();
world.AddSystem<SendEntitiesSystem>();
// #endregion systems

if (args.Contains("--schedule"))
{
    Console.WriteLine(world.DescribeSchedule());
    return;
}

// #region players
var player = world.Prefabs.Player;
for (int i = 0; i < players; i++)
{
    var at = Rules.SpawnPoint(i);
    world.Instantiate(player, new Player(i), new Position(at), new InChunk(Terrain.ChunkOf(at)));
}
// #endregion players

Server.Run(world, ticks, args.Contains("--realtime"));

// #region leaderboard
// The best three diggers: the ordered index walks the scores from the top, no sort and no list of all the players.
Console.Write("top diggers:");
int shown = 0;
foreach (var digger in world.ScoreIndex.Descending())
{
    Console.Write($" player {digger.Player.Client} ({digger.Score.BlocksDug})");
    if (++shown == 3) break;
}
Console.WriteLine();
// #endregion leaderboard

// #region save
// Stop the way a vanilla server does: save the world; the unloaded chunks are in the region files already. The prefabs
// and the region files are not in the save: every start builds the prefabs (a Prefab has no serialized form) and
// opens the files. A restart does both, then loads the save beside them.
var startup = ComponentSet.Create().WithSingleton<Prefabs>().WithSingleton<Regions>();
var save = new NinoArrayBufferWriter();                                    // Save writes into it in place, no copy
world.SaveWithout(save, in startup);

using var restarted = new World();
restarted.SetRegions(regions);
SetPrefabs(restarted);
restarted.LoadKeeping(save.WrittenSpan, in startup, markWritten: false);   // a client that reconnects gets it all anyway
Console.WriteLine($"restarted from the save: {restarted.Count<Player>()} players, {restarted.Count<Chunk>()} chunks, " +
                  $"{Terrain.WaterIn(restarted)} water, {restarted.Count<Zombie>()} zombies; {regions.Count} chunks in the region files");
Console.Error.WriteLine($"save: {save.WrittenCount / 1024} KiB");
// #endregion save

// #region prefabs
// The recipes chunks, players, zombies and items are created from: every start builds them. A chunk has every
// component and tag it will ever have, the tags absent, so all chunks share one table (Systems/Chunks.cs); a zombie
// has Burning, absent, for the same reason. A zombie carries the Vision it hunts within; a player's view is
// Rules.PlayerVision, the same for every player (Aoi.Range).
static void SetPrefabs(World world) =>
    world.SetPrefabs(
        Chunk: new Prefab()
            .SetChunk(default).SetBuffer<Block>().SetBuffer<Fluid>().SetBuffer<NextFluid>().SetBuffer<Surface>().SetBuffer<Border>()
            .SetViewers(0, 0).Set(FluidCount.Dry).SetReach(0, -1).SetFluidStep(StepResult.None, FluidCount.Dry, 0, 0)
            .Set(new Fresh(), new Flowing(), new Unloading()).RemoveUnloading(),
        Player: new Prefab()
            .SetPlayer(0).SetInChunk(default).SetVisible(default, false).SetScore(0)
            .SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.PlayerHealth).SetInput(default, false).SetBuffer<ItemStack>(),
        Zombie: new Prefab()
            .Set(new Zombie()).SetVision(Rules.ZombieVision).SetInChunk(default).SetVisible(default, false).SetTarget(default)
            .SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.ZombieHealth).SetCooldown(0).SetBurning(0).RemoveBurning(),
        Item: new Prefab()
            .SetItem(default, 1).SetInChunk(default).SetVisible(default, false).SetPosition(default).SetVelocity(new System.Numerics.Vector3(0, 0.2f, 0))
            .SetBody(false, false).SetDropped(0));
// #endregion prefabs
