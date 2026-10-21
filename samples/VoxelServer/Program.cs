using System.Buffers;
using EasyEcs;
using VoxelServer;

// A Minecraft-style server: 20 ticks a second, chunks loaded around the players, blocks dug into items, springs whose
// water runs and carries whoever stands in it, zombies at night. Options: --players N (50), --ticks N (1200: one
// minute, a day and a night twice), --realtime, --schedule (print the order the systems run in and stop).
int players = Support.Option(args, "--players", 50);
int ticks = Support.Option(args, "--ticks", 1200);

// #region options
// A chunk's blocks and its water are 8 KB each, more than a buffer's pool keeps by default: let each loaded chunk
// reuse the lists of one that was unloaded.
DynamicBuffer<Block>.PoolCapacity = Terrain.BlocksPerChunk;
DynamicBuffer<Fluid>.PoolCapacity = Terrain.BlocksPerChunk;

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
world.AddSystem<ChunkLoadSystem>();
world.AddSystem<ChunkReadSystem>();
world.AddSystem<BlockUpdateSystem>();
world.AddSystem<WaterFlowSystem>();
world.AddSystem<PlayerMoveSystem>();
world.AddSystem<SightSystem>();
world.AddSystem<ZombieTargetSystem>();
world.AddSystem<ZombieChaseSystem>();
world.AddSystem<ZombieAttackSystem>();
world.AddSystem<CooldownSystem>();
world.AddSystem<PlayerRespawnSystem>();
world.AddSystem<FloatSystem>();
world.AddSystem<PhysicsSystem>();
world.AddSystem<GridSystem>();
world.AddSystem<DigSystem>();
world.AddSystem<ItemAgeSystem>();
world.AddSystem<PickupSystem>();
world.AddSystem<ItemExpirySystem>();
world.AddSystem<NightSpawnSystem>();
world.AddSystem<SunlightSystem>();
world.AddSystem<BurnSystem>();
world.AddSystem<ExtinguishSystem>();
world.AddSystem<ZombieDeathSystem>();
world.AddSystem<ChunkUnloadSystem>();
world.AddSystem<ChunkSaveSystem>();
world.AddSystem<ChunkViewSystem>();
world.AddSystem<SendChunksSystem>();
world.AddSystem<SendEntitiesSystem>();
world.AddSystem<ClockSystem>();
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
var save = new ArrayBufferWriter<byte>();
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
// The recipes players, zombies and items are created from: every start builds them. Players and zombies differ in
// what they see: each prefab carries its own Vision.
static void SetPrefabs(World world) =>
    world.SetPrefabs(
        Player: new Prefab()
            .SetPlayer(0).SetVision(Rules.PlayerVision).SetInChunk(default).SetScore(0).SetBuffer<Seen>().SetBuffer<InView>()
            .SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.PlayerHealth).SetInput(default, false).SetBuffer<ItemStack>(),
        Zombie: new Prefab()
            .Set(new Zombie()).SetVision(Rules.ZombieVision).SetBuffer<Seen>().SetInChunk(default).SetTarget(default)
            .SetPosition(default).SetVelocity(default).SetBody(false, false)
            .SetHealth(Rules.ZombieHealth).SetCooldown(0),
        Item: new Prefab()
            .SetItem(default, 1).SetInChunk(default).SetPosition(default).SetVelocity(new System.Numerics.Vector3(0, 0.2f, 0))
            .SetBody(false, false).SetAge(0));
// #endregion prefabs
